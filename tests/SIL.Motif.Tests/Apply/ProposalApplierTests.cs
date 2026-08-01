using System.Linq;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Caching;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Apply;

/// <summary>
/// Stage D proof, on a real project: <see cref="ProposalApplier.Apply"/> commits a
/// <c>lexical/sense/setGloss</c> Proposal, writes exactly one applied-log entry inside the same
/// unit of work, and the result survives a real save + reopen from disk — not merely an in-memory
/// assertion. Also proves idempotence (a second Apply of the same Proposal against the reopened
/// project does nothing) and that a distinct Proposal adds a second, distinct log entry. See
/// docs/change-set-contract.md, "Application Receipt", and docs/applied-log.md.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
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

        // Baseline: whatever CmResource entries the real fixture already carries (foreign to
        // Motif) must be left completely untouched by everything below.
        var foreignEntriesBefore = ReadForeignResourceVersions(_cache);

        var proposal = BuildSetGlossProposal(canonicalId, wsTag, newGloss);
        const string applierIdentity = "motif-tests";
        const string description = "Stage D test: revise sense gloss";

        // --- 1. First apply: a real commit, not a rollback. Bound to a prior dry run (ADR 0004 §3). ---
        var dryRun = ProposalDryRunner.Run(_cache, proposal);
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

        // (b) Exactly one Motif applied-log entry exists, with the right proposalId GUID, intent
        // digest, and user.
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

        // --- 3. Idempotence: re-apply the SAME proposal against the reopened cache. Passing the
        // ORIGINAL (now stale) anchor here deliberately proves idempotence short-circuits before the
        // drift check ever runs: the applied-log lookup wins first (ADR 0004 §3 binds apply to an
        // DryRun, but never re-applies/re-mutates once idempotence already says "done"). ---
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
        var secondDryRun = ProposalDryRunner.Run(_cache, secondProposal);
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

        // Resolution of the bogus target fails inside Apply's footprint pre-flight, before the
        // anchor's digest is ever compared — so a placeholder anchor is fine here.
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

        var dryRun = ProposalDryRunner.Run(_cache, proposal);
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
        var dryRun = ProposalDryRunner.Run(_cache, proposal);

        // The baseline moves out from underneath: someone else commits a real, different gloss
        // change to the very same target/field before apply runs.
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

    // --- Defect 3 (RollbackCacheInvalidator): a mid-Change-Set failure rolls back and marks the
    // cache instance non-reusable, never commits a homograph renumber. ---

    [Fact]
    public void Apply_MidProposalFailure_RollsBack_AndMarksCacheNonReusable()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var target = CanonicalId.FromGuid(senseGuid);

        // op1: a normal, valid setGloss (will succeed and mutate). op2: the SAME (valid, resolvable)
        // target but a malformed 'after' payload (no 'text') — SetGlossPayload.Parse throws only
        // once the real, committing apply loop actually reads the payload (the footprint pre-flight
        // never looks at 'after' at all, only at the target), which is exactly the "failed partway
        // through, must roll back" scenario, and deliberately NOT an unresolvable-target failure
        // (that fails earlier, during Apply's footprint pre-flight, before any unit of work opens at
        // all — see the companion Apply_UnknownTarget_... test).
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

        // Both operations' targets resolve fine, so the footprint pre-flight (and thus a
        // from-scratch anchor built from it) succeeds even though the real apply below will not.
        var footprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);
        var anchor = DummyAnchor() with { FootprintDigest = footprintDigest };

        Assert.False(CacheReusability.IsPoisoned(_cache, out _));

        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, anchor, "motif-tests"));

        // Rollback proof: op1's mutation was undone too (the whole Proposal is one unit of work).
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(originalGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);

        // No applied-log entry (docs/applied-log.md, "Atomicity").
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));

        // The cache instance is now flagged non-reusable, per the fixed RollbackCacheInvalidator
        // (it must NOT have committed a project-wide homograph renumber to get there — see that
        // type's remarks for the liblcm citations proving why a real commit was the old bug).
        Assert.True(CacheReusability.IsPoisoned(_cache, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // And a poisoned cache now refuses a further dry run or apply outright.
        Assert.Throws<CachePoisonedException>(() => ProposalDryRunner.Run(_cache, proposal));
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

    /// <summary>
    /// Enumerates senses via the real <see cref="ILexSenseRepository"/> and picks the first one
    /// with a non-empty gloss alternative, reading the current gloss text straight back from
    /// LibLCM (never hardcoded).
    /// </summary>
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
