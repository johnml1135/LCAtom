using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Apply;

/// <summary>
/// The acceptance scenario: two agents begin at one shared baseline, one Proposal applies in
/// authored order, and the other is stopped by drift before mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>What plays "one Baseline Token" here.</b> This codebase does not have a first-class,
/// whole-project <c>BaselineToken</c> type — its shape (whole-project digest versus per-Proposal
/// footprint, what identifies "the project" across a save/reload) is a design question the ADRs
/// governing this area (0004, 0006, 0016) do not settle, so this test does not invent one. What
/// two independent agents genuinely share today is the live project's saved state at the moment
/// each dry-runs: <see cref="ScratchDryRun.Of"/> copies that exact saved file for each agent (a
/// separate <see cref="LcmCache"/> per call, exactly as two independent processes would), and
/// <see cref="AnchorsAreCrossCachePortableTests"/> already establishes that a footprint digest
/// computed on one such copy equals the digest computed on the live project or any other copy of
/// the same data. That portability is the property a Baseline Token would need to have; this test
/// proves the apply/drift protocol built on it behaves correctly for two independently-authored
/// Proposals sharing one starting point.
/// </para>
/// <para>
/// <b>Why this is a genuine conflict, not just a replay.</b> Unlike the anchor-substitution case in
/// <see cref="ProposalApplierTests"/>, agent B here has a real anchor from a real Dry Run of its own
/// (never-substituted) Proposal. The refusal comes entirely from the footprint drift check: the
/// world moved because agent A applied first, exactly as the acceptance criterion describes.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class TwoAgentsOneBaselineTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public TwoAgentsOneBaselineTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Fact]
    public void AgentA_AppliesInAuthoredOrder_AgentB_RefusesDrift_BeforeMutation()
    {
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultAnalWs);
        var target = CanonicalId.FromGuid(_seed.FirstSenseId);
        var originalGloss = SeededProject.FirstGloss;

        var proposalA = SetGlossProposal(target, wsTag, originalGloss + " (agent A)");
        var proposalB = SetGlossProposal(target, wsTag, originalGloss + " (agent B, conflicting)");

        // Both agents dry-run privately against the SAME saved baseline -- neither has mutated anything yet.
        var dryRunA = ScratchDryRun.Of(_cache, proposalA);
        var dryRunB = ScratchDryRun.Of(_cache, proposalB);
        Assert.Equal(dryRunA.Anchor.FootprintDigest, dryRunB.Anchor.FootprintDigest);
        Assert.NotEqual(dryRunA.Anchor.IntentDigest, dryRunB.Anchor.IntentDigest);

        // Agent A applies first, in authored order: succeeds, and the live project moves.
        var receiptA = ProposalApplier.Apply(_cache, proposalA, dryRunA.Anchor, "agent-a");
        Assert.False(receiptA.AlreadyApplied);

        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(originalGloss + " (agent A)", senseRepo.GetObject(_seed.FirstSenseId).Gloss.get_String(wsHandle).Text);

        // Agent B's baseline has now moved underneath it: apply must refuse drift, not blindly overwrite.
        var ex = Assert.Throws<ApplyPreconditionException>(
            () => ProposalApplier.Apply(_cache, proposalB, dryRunB.Anchor, "agent-b"));
        Assert.Contains("drift", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Before mutation, proven: agent A's text stands, agent B's text is absent, one log entry exists.
        Assert.Equal(
            originalGloss + " (agent A)",
            senseRepo.GetObject(_seed.FirstSenseId).Gloss.get_String(wsHandle).Text);
        var entries = ProjectAppliedLog.ReadAll(_cache);
        var onlyEntry = Assert.Single(entries);
        Assert.Equal(proposalA.ProposalId.ToGuid(), onlyEntry.ProposalId);
    }

    private static Proposal SetGlossProposal(CanonicalId target, string wsTag, string text)
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
}
