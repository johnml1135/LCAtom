using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Proves <see cref="CliSession"/>'s acceptance criterion on a real project: N dry runs across one
/// session cost one live project load, and the second dry run's effects are identical to the first's
/// when nothing changed. Also proves footprint-gated scratch re-copy and that teardown always
/// releases the project lock, including on a failed apply.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class CliSessionTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public CliSessionTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void DryRun_NCallsAcrossOneSession_LoadTheLiveProjectExactlyOnce()
    {
        var loader = new CountingFwDataProjectLoader();
        var proposal = BuildSetGlossProposal(_seed.FirstSenseId, "revised once");

        using var session = CliSession.Open(_fwDataPath, loader);

        for (var i = 0; i < 4; i++)
            session.DryRun(proposal);

        Assert.Equal(1, loader.LoadCacheCalls);
        // The scratch mechanics (pristine + fan-out) still ran, through the separate LoadScratchCache path.
        Assert.True(loader.LoadScratchCacheCalls > 0);
    }

    [Fact]
    public void DryRun_SecondCall_EffectsIdenticalToFirst_WhenNothingChanged()
    {
        var proposal = BuildSetGlossProposal(_seed.FirstSenseId, "revised twice, same input");

        using var session = CliSession.Open(_fwDataPath);

        var first = session.DryRun(proposal);
        var second = session.DryRun(proposal);

        Assert.Equal(first.IntentDigest, second.IntentDigest);
        Assert.Equal(first.EffectDigest, second.EffectDigest);
        Assert.Equal(first.Anchor.FootprintDigest, second.Anchor.FootprintDigest);
        AssertEffectsEqual(first.ExpectedEffects, second.ExpectedEffects);

        // Footprint gating: nothing changed the live project between the two calls, so no re-copy happened.
        Assert.Equal(1, session.PristineRebuildCount);
    }

    [Fact]
    public void DryRun_ThenApply_ThenDryRunAgain_RebuildsPristineOnlyWhenTheFootprintMoved()
    {
        var proposal = BuildSetGlossProposal(_seed.FirstSenseId, "revised by footprint-gating test");

        using var session = CliSession.Open(_fwDataPath);

        var firstDryRun = session.DryRun(proposal);
        Assert.Equal(1, session.PristineRebuildCount);

        // Repeating the same dry run with nothing changed must not re-copy the pristine scratch.
        session.DryRun(proposal);
        Assert.Equal(1, session.PristineRebuildCount);

        // Apply moves the live project's state for exactly the target this Proposal touches.
        session.Apply(proposal, firstDryRun.Anchor, "session-tests");

        // The pristine scratch's snapshot of that target is now stale, so the next dry run must re-copy.
        session.DryRun(proposal);
        Assert.Equal(2, session.PristineRebuildCount);

        // And the state is stable again: a further dry run with nothing changed reuses the rebuilt scratch.
        session.DryRun(proposal);
        Assert.Equal(2, session.PristineRebuildCount);
    }

    [Fact]
    public void Dispose_ReleasesTheProjectLock_SoItCanBeReopenedInTheSameProcess()
    {
        var proposal = BuildSetGlossProposal(_seed.FirstSenseId, "before disposal");

        var session = CliSession.Open(_fwDataPath);
        session.DryRun(proposal);
        session.Dispose();

        // If the lock were still held, this throws LcmFileLockedException.
        using var reopened = new FwDataProjectLoader().LoadCache(_fwDataPath);
        Assert.False(string.IsNullOrWhiteSpace(reopened.ProjectId.Name));
    }

    [Fact]
    public void Apply_Fails_DiscardsTheLiveCache_AndDisposeStillReleasesTheProjectLock()
    {
        // A bad target makes ProposalApplier.Apply throw before it opens its unit of work.
        var badProposal = BuildSetGlossProposal(CanonicalId.Mint(), "does not exist");
        var goodProposal = BuildSetGlossProposal(_seed.FirstSenseId, "used only to obtain a real anchor");

        var session = CliSession.Open(_fwDataPath);
        var anchorSource = session.DryRun(goodProposal);

        Assert.ThrowsAny<Exception>(() => session.Apply(badProposal, anchorSource.Anchor, "session-tests"));

        // The live cache is gone: any further use fails clearly rather than reusing a suspect cache.
        Assert.Throws<ObjectDisposedException>(() => session.DryRun(goodProposal));

        // Teardown must still succeed and release the lock even though the live cache was already discarded.
        session.Dispose();
        using var reopened = new FwDataProjectLoader().LoadCache(_fwDataPath);
        Assert.False(string.IsNullOrWhiteSpace(reopened.ProjectId.Name));
    }

    /// <remarks>
    /// This is the save boundary: <c>ProposalApplier.Apply</c> already committed the mutation to the
    /// live cache (a real, non-idempotent apply, not a no-op) before the injected failure happens, so
    /// this is not a rollback and must not be reported as one -- see
    /// <see cref="NeedsReconciliationException"/> and <see cref="CliSession.Apply"/>'s remarks.
    /// </remarks>
    [Fact]
    public void Apply_SaveFails_AfterACommittedMutation_ThrowsNeedsReconciliation_NotRollback()
    {
        var proposal = BuildSetGlossProposal(_seed.FirstSenseId, "revised, then save fails");
        var loader = new SaveThrowingFwDataProjectLoader();

        using var session = CliSession.Open(_fwDataPath, loader);
        // The dry run's own pristine-scratch rebuild also saves; let that one succeed, arm only for Apply.
        var dryRun = session.DryRun(proposal);
        loader.ArmedToThrow = true;

        var ex = Assert.Throws<NeedsReconciliationException>(
            () => session.Apply(proposal, dryRun.Anchor, "session-tests"));

        Assert.Equal(ReconciliationBoundary.Save, ex.Boundary);
        Assert.IsType<IOException>(ex.InnerException);

        // The live cache is no longer trustworthy either way: further use fails clearly.
        Assert.Throws<ObjectDisposedException>(() => session.DryRun(proposal));
    }

    /// <summary>Throws from <see cref="Save"/> once armed, so setup saves behave normally first.</summary>
    private sealed class SaveThrowingFwDataProjectLoader : FwDataProjectLoader
    {
        public bool ArmedToThrow { get; set; }

        public override void Save(LcmCache cache)
        {
            if (ArmedToThrow)
                throw new IOException("Injected: the project file could not be written.");
            base.Save(cache);
        }
    }

    private static void AssertEffectsEqual(
        IReadOnlyList<ExpectedEffect> first, IReadOnlyList<ExpectedEffect> second)
    {
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].CanonicalId, second[i].CanonicalId);
            Assert.Equal(first[i].Field, second[i].Field);
            Assert.Equal(first[i].Before, second[i].Before);
            Assert.Equal(first[i].After, second[i].After);
        }
    }

    private static Proposal BuildSetGlossProposal(Guid senseGuid, string text) =>
        BuildSetGlossProposal(CanonicalId.FromGuid(senseGuid), text);

    private static Proposal BuildSetGlossProposal(CanonicalId target, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = NewLangProjFixture.AnalysisTag, text });
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

    /// <summary>Counts real loader calls without changing behaviour, to prove the load count directly.</summary>
    private sealed class CountingFwDataProjectLoader : FwDataProjectLoader
    {
        public int LoadCacheCalls { get; private set; }
        public int LoadScratchCacheCalls { get; private set; }

        public override LcmCache LoadCache(string fwDataFilePath, string? templatesFolder = null)
        {
            LoadCacheCalls++;
            return base.LoadCache(fwDataFilePath, templatesFolder);
        }

        public override LcmCache LoadScratchCache(string fwDataFilePath, string? templatesFolder = null)
        {
            LoadScratchCacheCalls++;
            return base.LoadScratchCache(fwDataFilePath, templatesFolder);
        }
    }
}
