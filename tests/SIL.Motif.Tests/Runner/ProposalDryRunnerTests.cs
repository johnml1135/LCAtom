using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Proof, on a real project: <see cref="ProposalDryRunner.Run"/> reads back real LibLCM
/// state (not the intent) to compute expected effects for a <c>lexical/lexSense/setGloss</c> change
/// set, is deterministic, and never leaves a mutation committed. Per ADR 0006 decision 1, LibLCM's
/// side effects — ownership cascade, computed defaults — apply synchronously as each mutation
/// happens, so a before/after snapshot diff taken inside the still-open, never-committed unit of
/// work sees the true cascaded state without needing to replay or predict it.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ProposalDryRunnerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public ProposalDryRunnerTests(PristineProjectFixture pristine)
    {
        // A scratch is opened from files already carrying the seed, so no save is needed to put it on disk.
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;

        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
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
    public void DryRun_SetGloss_ReportsRealBeforeAndAfterGloss_IsDeterministic_AndDoesNotMutateProject()
    {
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var newGloss = originalGloss + " (revised sense)";
        var canonicalId = CanonicalId.FromGuid(sense.Guid);

        var proposal = BuildSetGlossProposal(canonicalId, wsTag, newGloss);

        var firstDryRun = ScratchDryRun.Of(_cache, proposal);
        var secondDryRun = ScratchDryRun.Of(_cache, proposal);

        // (a) Before/after are the real read-back values, not an echo of the authored intent.
        var effect = Assert.Single(firstDryRun.ExpectedEffects);
        Assert.Equal(canonicalId, effect.CanonicalId);
        Assert.Equal(SnapshotFields.LexSenseGloss, effect.Field);
        Assert.Equal(originalGloss, effect.Before[wsTag]);
        Assert.Equal(newGloss, effect.After[wsTag]);

        // (b) the effect digest is stable across two identical dryRuns.
        Assert.StartsWith("sha256:", firstDryRun.EffectDigest);
        Assert.Equal(firstDryRun.EffectDigest, secondDryRun.EffectDigest);
        Assert.Equal(firstDryRun.IntentDigest, secondDryRun.IntentDigest);

        // (c) Non-mutating rollback proven by re-reading the live object, not the returned DryRun.
        Assert.Equal(originalGloss, sense.Gloss.get_String(wsHandle).Text);
    }

    [Fact]
    public void DryRun_UnknownTarget_Throws()
    {
        var bogusTarget = CanonicalId.FromGuid(Guid.NewGuid());
        var proposal = BuildSetGlossProposal(bogusTarget, "en", "does not matter");

        Assert.ThrowsAny<Exception>(() => ScratchDryRun.Of(_cache, proposal));
    }

    // --- The scratch is single-use, and it is really mutated. ---

    /// <remarks>
    /// Two tests named DryRun_SetGloss_UnflaggedKind_DoesNotMarkCachePoisoned and
    /// DryRun_FlaggedKind_MarksCachePoisoned stood here, guarding a hand-maintained list of fields whose
    /// derived caches a rollback would leave stale. Both are gone with the rollback
    /// (ADR 0016). What replaces them is not
    /// another classification but the two properties the new design actually rests on: the scratch is
    /// genuinely mutated (so read-back is real), and it can only be used once (so a baseline is always
    /// a baseline the live project was really in).
    /// </remarks>
    [Fact]
    public void DryRun_MutatesTheScratchAndDoesNotRevertIt()
    {
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var newGloss = originalGloss + " (written into the scratch)";
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(sense.Guid), wsTag, newGloss);

        // Keep our own scratch cache reference: DryRunScratch doesn't hand it back (no production need to).
        var scratchRoot = Path.Combine(_tempRoot, "scratch-inspect");
        var scratchCache = new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot);
        using var scratch = DryRunScratch.Adopt(scratchCache, "test scratch, inspected after the run");

        var dryRun = ProposalDryRunner.Run(scratch, proposal);

        // Nothing reverted it: rollback is what skipped LibLCM's setter hooks and left caches stale.
        var scratchWsHandle = scratchCache.WritingSystemFactory.GetWsFromStr(wsTag);
        var scratchSense = scratchCache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(sense.Guid);
        Assert.Equal(newGloss, scratchSense.Gloss.get_String(scratchWsHandle).Text);

        // And the live cache is untouched, for a better reason than before: the DryRun was never here.
        Assert.Equal(originalGloss, sense.Gloss.get_String(wsHandle).Text);
        Assert.Equal(newGloss, Assert.Single(dryRun.ExpectedEffects).After[wsTag]);
    }

    [Fact]
    public void DryRunScratch_RefusesASecondRun()
    {
        var (sense, _, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(sense.Guid), wsTag, originalGloss + " x");

        var scratchRoot = Path.Combine(_tempRoot, "scratch-single-use");
        using var scratch = DryRunScratch.Adopt(
            new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot),
            "test scratch, reused on purpose");

        ProposalDryRunner.Run(scratch, proposal);

        // A second run would digest a baseline the live project was never in -- refused, not silently wrong.
        var reuse = Assert.Throws<InvalidOperationException>(() => ProposalDryRunner.Run(scratch, proposal));
        Assert.Contains("single-use", reuse.Message);
    }

    [Fact]
    public void DryRun_ReversedCandidateInputStillExecutesDeclaredPrerequisiteFirst()
    {
        var (sense, _, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var target = CanonicalId.FromGuid(sense.Guid);
        var first = BuildSetGlossProposal(target, wsTag, originalGloss + " first");
        var second = WithRequirements(
            BuildSetGlossProposal(target, wsTag, originalGloss + " second"), first.ProposalId);
        var dependent = WithRequirements(
            BuildSetGlossProposal(target, wsTag, originalGloss + " dependent"), second.ProposalId);
        var plan = PrerequisiteExecutionPlan.Create(
            dependent, new[] { second, first }, Array.Empty<Guid>());

        var scratchRoot = Path.Combine(_tempRoot, "scratch-prerequisite-order");
        using var scratch = DryRunScratch.Adopt(
            new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot),
            "test scratch with reversed prerequisite collection");

        var dryRun = ProposalDryRunner.Run(scratch, plan);

        Assert.Equal(originalGloss + " second", Assert.Single(dryRun.ExpectedEffects).Before[wsTag]);
    }

    [Fact]
    public void ExecutionPlan_MissingUnappliedPrerequisite_RefusesAndNamesIt()
    {
        var missing = CanonicalId.Mint();
        var dependent = WithRequirements(
            BuildSetGlossProposal(CanonicalId.Mint(), "en", "dependent"), missing);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PrerequisiteExecutionPlan.Create(
                dependent, Array.Empty<Proposal>(), Array.Empty<Guid>()));

        Assert.Contains(missing.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPlan_CyclicCandidateClosure_RefusesAndReportsCompleteCycle()
    {
        var firstId = CanonicalId.Mint();
        var secondId = CanonicalId.Mint();
        var first = WithIdentityAndRequirements(
            BuildSetGlossProposal(CanonicalId.Mint(), "en", "first"), firstId, secondId);
        var second = WithIdentityAndRequirements(
            BuildSetGlossProposal(CanonicalId.Mint(), "en", "second"), secondId, firstId);
        var dependent = WithRequirements(
            BuildSetGlossProposal(CanonicalId.Mint(), "en", "dependent"), firstId);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PrerequisiteExecutionPlan.Create(
                dependent, new[] { second, first }, Array.Empty<Guid>()));

        Assert.Contains(
            $"{firstId.Value} -> {secondId.Value} -> {firstId.Value}",
            ex.Message,
            StringComparison.Ordinal);
    }

    private (ILexSense Sense, int WsHandle, string WsTag, string Gloss) FindSenseWithKnownGloss()
    {
        var sense = _cache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(_seed.FirstSenseId);
        var wsHandle = _cache.DefaultAnalWs;
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(wsHandle);
        return (sense, wsHandle, wsTag, SeededProject.FirstGloss);
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

    private static Proposal WithRequirements(Proposal proposal, params CanonicalId[] requirements) =>
        WithIdentityAndRequirements(proposal, proposal.ProposalId, requirements);

    private static Proposal WithIdentityAndRequirements(
        Proposal proposal, CanonicalId proposalId, params CanonicalId[] requirements) =>
        new(
            proposal.ContractVersions,
            proposalId,
            requirements,
            proposal.Operations,
            proposal.Extensions);
}
