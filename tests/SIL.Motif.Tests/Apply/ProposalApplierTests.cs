using System.Linq;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Apply;

/// <summary>
/// Proof, on a real project: <see cref="ProposalApplier.Apply"/> commits a
/// <c>lexical/lexSense/setGloss</c> Proposal, writes exactly one applied-log entry inside the same
/// unit of work, and the result survives a real save + reopen from disk — not merely an in-memory
/// assertion. Also proves idempotence (a second Apply of the same Proposal against the reopened
/// project does nothing) and that a distinct Proposal adds a second, distinct log entry. Per the
/// Change Set contract's Application Receipt, apply writes exactly one entry into the applied log
/// inside the same unit of work as the change; per the applied log format, identity is matched on
/// the stable Change Set GUID, which is what makes a repeat apply a no-op instead of a duplicate
/// entry.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
[Trait("Fixture", "TestLangProj")]
public sealed class ProposalApplierTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private LcmCache _cache;

    public ProposalApplierTests()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = _loader.LoadCache(_fwDataPath);
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; a locked native handle should not fail the test
        }
    }

    [Fact]
    public void Apply_SetGloss_CommitsAndPersistsAcrossReopen_IsIdempotent_AndUnionsWithADistinctProposal()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var canonicalId = CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised sense, Stage D)";

        // Baseline: whatever foreign CmResource entries the fixture carries must stay untouched below.
        var foreignEntriesBefore = ReadForeignResourceVersions(_cache);

        var proposal = BuildSetGlossProposal(canonicalId, wsTag, newGloss);
        const string applierIdentity = "motif-tests";
        const string description = "Stage D test: revise sense gloss";

        // --- 1. First apply: a real commit, not a rollback. Bound to a prior dry run (ADR 0004 §3). ---
        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, applierIdentity, description);

        Assert.False(receipt.AlreadyApplied);
        Assert.Equal(proposal.ProposalId, receipt.ProposalId);
        var effect = Assert.Single(receipt.ActualEffects);
        Assert.Equal(canonicalId, effect.CanonicalId);
        Assert.Equal(newGloss, effect.After[wsTag]);
        Assert.Equal(receipt.AppliedLogEntry.ProposalId, proposal.ProposalId.ToGuid());
        Assert.Equal(applierIdentity, receipt.AppliedLogEntry.User);

        // Gloss is changed in the live (not-yet-saved) cache.
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(newGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);

        // Exactly one Motif entry has been written; the pre-existing foreign entries are intact.
        var loggedAfterFirstApply = ProjectAppliedLog.ReadAll(_cache);
        var firstEntry = Assert.Single(loggedAfterFirstApply);
        Assert.Equal(proposal.ProposalId.ToGuid(), firstEntry.ProposalId);
        Assert.Equal(applierIdentity, firstEntry.User);
        Assert.Equal(description, firstEntry.Description);
        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);

        // --- 2. Persist, then re-open the project from disk: the actual Stage D proof. ---
        _loader.Save(_cache);
        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);

        var wsHandleAfterReopen = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepoAfterReopen = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        var reopenedSense = senseRepoAfterReopen.GetObject(senseGuid);

        // (a) The gloss really is the new value after a from-disk reload.
        Assert.Equal(newGloss, reopenedSense.Gloss.get_String(wsHandleAfterReopen).Text);

        // (b) Exactly one Motif applied-log entry, with the right proposalId GUID, digest, and user.
        var loggedAfterReopen = ProjectAppliedLog.ReadAll(_cache);
        var reopenedEntry = Assert.Single(loggedAfterReopen);
        Assert.Equal(proposal.ProposalId.ToGuid(), reopenedEntry.ProposalId);
        Assert.Equal(receipt.AppliedLogEntry.IntentDigest, reopenedEntry.IntentDigest);
        Assert.Equal(applierIdentity, reopenedEntry.User);
        Assert.Equal(description, reopenedEntry.Description);

        // (c) No other CmResource entries were disturbed.
        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);
        Assert.Equal(
            foreignEntriesBefore.Count + 1,
            _cache.LangProject.LexDbOA.ResourcesOC.Count);

        // 3. Idempotence: the stale anchor proves the applied-log check wins before any drift check runs.
        var reapplyReceipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, applierIdentity, description);

        Assert.True(reapplyReceipt.AlreadyApplied);
        Assert.Empty(reapplyReceipt.ActualEffects);
        Assert.Equal(newGloss, senseRepoAfterReopen.GetObject(senseGuid).Gloss.get_String(wsHandleAfterReopen).Text);

        var loggedAfterReapply = ProjectAppliedLog.ReadAll(_cache);
        var onlyEntryAfterReapply = Assert.Single(loggedAfterReapply);
        Assert.Equal(reopenedEntry.TimestampUtc, onlyEntryAfterReapply.TimestampUtc); // untouched, not rewritten

        // --- 4. A distinct Proposal adds a second, distinct log entry (union-friendly). ---
        var secondGloss = newGloss + " v2";
        var secondProposal = BuildSetGlossProposal(canonicalId, wsTag, secondGloss);
        const string secondDescription = "Stage D test: second, distinct proposal";

        // A genuinely new mutation needs a fresh dry run against the live (post-first-apply) baseline.
        var secondDryRun = ScratchDryRun.Of(_cache, secondProposal);
        var secondReceipt = ProposalApplier.Apply(
            _cache, secondProposal, secondDryRun.Anchor, applierIdentity, secondDescription);

        Assert.False(secondReceipt.AlreadyApplied);
        Assert.Equal(secondGloss, senseRepoAfterReopen.GetObject(senseGuid).Gloss.get_String(wsHandleAfterReopen).Text);

        var loggedAfterSecondApply = ProjectAppliedLog.ReadAll(_cache);
        Assert.Equal(2, loggedAfterSecondApply.Count);
        Assert.Contains(loggedAfterSecondApply, e => e.ProposalId == proposal.ProposalId.ToGuid());
        Assert.Contains(loggedAfterSecondApply, e => e.ProposalId == secondProposal.ProposalId.ToGuid());
        Assert.NotEqual(
            loggedAfterSecondApply.Single(e => e.ProposalId == proposal.ProposalId.ToGuid()).IntentDigest,
            loggedAfterSecondApply.Single(e => e.ProposalId == secondProposal.ProposalId.ToGuid()).IntentDigest);

        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);
        Assert.Equal(
            foreignEntriesBefore.Count + 2,
            _cache.LangProject.LexDbOA.ResourcesOC.Count);
    }

    [Fact]
    public void Apply_UnknownTarget_ThrowsAndRollsBack_AndWritesNoAppliedLogEntry()
    {
        var bogusTarget = CanonicalId.FromGuid(Guid.NewGuid());
        var proposal = BuildSetGlossProposal(bogusTarget, "en", "does not matter");

        // The bogus target fails in the footprint pre-flight before the anchor digest is compared.
        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, DummyAnchor(), "motif-tests"));

        // The failed apply must leave no applied-log entry at all (docs/applied-log.md, "Atomicity").
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    // --- Defect 2 (ADR 0004 §3): apply is bound to a prior DryRun. ---

    [Fact]
    public void Apply_WithNullAnchor_IsAHardError()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(senseGuid), wsTag, originalGloss + " x");

        var ex = Assert.Throws<ApplyPreconditionException>(
            () => ProposalApplier.Apply(_cache, proposal, null!, "motif-tests"));

        Assert.Contains("bound DryRun", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    [Fact]
    public void Apply_AfterDryRun_Succeeds()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var target = CanonicalId.FromGuid(senseGuid);
        var proposal = BuildSetGlossProposal(target, wsTag, originalGloss + " (bound apply)");

        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.Single(ProjectAppliedLog.ReadAll(_cache));
    }

    [Fact]
    public void Apply_AfterFootprintMovedSinceDryRun_IsADriftHardStop()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var target = CanonicalId.FromGuid(senseGuid);
        var proposal = BuildSetGlossProposal(target, wsTag, originalGloss + " (intended apply)");

        // Run against the ORIGINAL baseline.
        var dryRun = ScratchDryRun.Of(_cache, proposal);

        // The baseline moves: someone else commits a different gloss to the same target/field first.
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        var sense = senseRepo.GetObject(senseGuid);
        var actionHandler = _cache.ServiceLocator.GetInstance<IActionHandler>();
        UndoableUnitOfWorkHelper.Do(
            "drift", "drift", actionHandler,
            () => SetGlossLowering.Apply(_cache, sense, wsTag, originalGloss + " (drifted from underneath)"));

        var ex = Assert.Throws<ApplyPreconditionException>(
            () => ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests"));

        Assert.Contains("drift", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Hard stop: no applied-log entry, and the drifted value (not the intended one) stands.
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        Assert.Equal(
            originalGloss + " (drifted from underneath)",
            senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);
    }

    // --- The one surviving rollback: a mid-Change-Set failure unwinds the whole Proposal. ---

    /// <remarks>
    /// This is the only rollback left in Motif, and it is forced by atomicity rather than chosen
    /// (AGENTS.md rule 4; ADR 0016). What this
    /// test no longer asserts is as important as what it does: there is no "the cache is now flagged
    /// non-reusable" check, because the flag, the field list that fed it, and the exception it threw
    /// were all deleted. The obligation moved to the host as an unconditional rule — if Apply throws,
    /// reload — which needs no per-field knowledge and is strictly stronger, since reload also discards
    /// ADR 0005's non-undoable schema phase that a rollback cannot reach.
    /// </remarks>
    /// <remarks>
    /// op2's payload (not its target) is malformed, so <c>SetGlossPayload.Parse</c> throws only once the
    /// committing apply loop reads it — the footprint pre-flight never looks at <c>after</c>, only the
    /// target — which is exactly the "failed partway through, must roll back" scenario, and deliberately
    /// not an unresolvable-target failure (that fails earlier; see <c>Apply_UnknownTarget_...</c>).
    /// </remarks>
    [Fact]
    public void Apply_MidProposalFailure_RollsBack_AndWritesNoAppliedLogEntry()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var target = CanonicalId.FromGuid(senseGuid);

        // op1 is valid; op2 targets the same sense but has a malformed payload (see remarks above).
        var op1 = BuildSetGlossOperation(target, wsTag, originalGloss + " (op1, will be rolled back)");
        using var malformedAfter = JsonDocument.Parse(JsonSerializer.Serialize(new { ws = "en" })); // no 'text'
        var op2 = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: malformedAfter.RootElement.Clone());

        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { op1, op2 });

        // Both targets resolve fine, so the footprint pre-flight succeeds even though apply will not.
        var footprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);
        var anchor = DummyAnchor() with { FootprintDigest = footprintDigest };

        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, anchor, "motif-tests"));

        // Rollback proof: op1's mutation was undone too (the whole Proposal is one unit of work).
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(originalGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);

        // No applied-log entry (docs/applied-log.md, "Atomicity").
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));

        // Nothing else to assert: the failure path does nothing -- no invalidation attempt, no flag.
    }

    private static OperationEnvelope BuildSetGlossOperation(CanonicalId target, string wsTag, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = wsTag, text });
        using var afterDocument = JsonDocument.Parse(afterJson);
        return new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: afterDocument.RootElement.Clone());
    }

    private static BoundDryRunAnchor DummyAnchor() => new(
        FootprintDigest: "sha256:" + new string('0', 64),
        EffectDigest: "sha256:" + new string('0', 64),
        RunnerVersion: "test",
        LibLcmVersion: "test",
        ProjectionVersion: "1",
        DryRunAtUtc: "20260101T000000Z");

    /// <summary>Picks the first sense with a non-empty gloss, read straight back from LibLCM.</summary>
    private static (Guid SenseGuid, string WsTag, string Gloss) FindSenseWithKnownGloss(LcmCache cache)
    {
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();

        foreach (var sense in senseRepo.AllInstances())
        {
            foreach (var wsHandle in sense.Gloss.AvailableWritingSystemIds)
            {
                var text = sense.Gloss.get_String(wsHandle).Text;
                if (!string.IsNullOrEmpty(text))
                {
                    var wsTag = cache.WritingSystemFactory.GetStrFromWs(wsHandle);
                    return (sense.Guid, wsTag, text);
                }
            }
        }

        throw new InvalidOperationException(
            "Expected the real TestLangProj fixture to contain at least one LexSense with a non-empty gloss.");
    }

    private static Proposal BuildSetGlossProposal(CanonicalId target, string wsTag, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = wsTag, text });
        using var afterDocument = JsonDocument.Parse(afterJson);

        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: afterDocument.RootElement.Clone());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }

    /// <summary>Snapshots every current (necessarily foreign, before any Apply) resource's Version.</summary>
    private static List<Guid> ReadForeignResourceVersions(LcmCache cache) =>
        cache.LangProject.LexDbOA.ResourcesOC.Select(r => r.Version).ToList();

    private static void AssertForeignResourcesUntouched(LcmCache cache, List<Guid> expectedForeignVersions)
    {
        var currentForeign = cache.LangProject.LexDbOA.ResourcesOC
            .Where(r => r.Name is null || !r.Name.StartsWith("Motif|", StringComparison.Ordinal))
            .Select(r => r.Version)
            .ToList();

        Assert.Equal(expectedForeignVersions.OrderBy(v => v), currentForeign.OrderBy(v => v));
    }
}
