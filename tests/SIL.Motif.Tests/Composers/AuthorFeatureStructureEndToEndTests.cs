using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Composers;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Composers;

/// <summary>
/// The grammar construct's full loop on a real project: authored (JSON through
/// <see cref="AuthorFeatureStructureIntentParser"/>), lowered (<see cref="AuthorFeatureStructureComposer.Build"/>),
/// dry-run and reviewed, applied, and saved — the same seam <see cref="AuthorLexemeFormEndToEndTests"/>
/// proves for the lexical construct. Does not attempt the parser leg of "authored, reviewed, applied,
/// and parsed": that needs an external executable this environment does not run (see the skipped
/// <c>ParserSeamIntegrationTests</c>).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AuthorFeatureStructureEndToEndTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public AuthorFeatureStructureEndToEndTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void Authored_Lowered_DryRun_Applied_Saved_RoundTripsOnARealProject()
    {
        using var session = CliSession.Open(_fwDataPath);
        var msaId = CanonicalId.FromGuid(
            session.LiveCache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(_seed.FirstSenseId)
                .MorphoSyntaxAnalysisRA.Guid);

        // --- authored: an agent's JSON, through the construct's own closed-schema parser ---
        var authoredJson = JsonSerializer.Serialize(new { msa = msaId.Value });
        using var authoredDocument = JsonDocument.Parse(authoredJson);
        var intent = AuthorFeatureStructureIntentParser.Parse(authoredDocument.RootElement);

        // --- lowered: one construct becomes one correctly-targeted Layer-0 operation ---
        var operations = AuthorFeatureStructureComposer.Build(session.LiveCache, intent);
        var op = Assert.Single(operations);
        var newFeatStrucId = op.EntityId!.Value;

        var proposal = BuildProposal(operations);

        // --- dry-run and review: real before/after read back from LibLCM, nothing mutated yet ---
        var dryRun = session.DryRun(proposal);
        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal(msaId, effect.CanonicalId);
        Assert.Empty(effect.Before);
        Assert.Equal(newFeatStrucId.Value, effect.After["ref"]);
        AssertFeatStrucIsAbsent(newFeatStrucId.ToGuid());

        // --- applied and saved ---
        var receipt = session.Apply(proposal, dryRun.Anchor, "motif-tests", "AuthorFeatureStructure end-to-end test");
        Assert.False(receipt.AlreadyApplied);

        // Re-open from disk: proves Apply's Save, not just the in-memory mutation, actually happened.
        using var reloaded = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        var msa = (IMoStemMsa)reloaded.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(msaId.ToGuid());
        Assert.NotNull(msa.MsFeaturesOA);
        Assert.Equal(newFeatStrucId.ToGuid(), msa.MsFeaturesOA.Guid);

        Assert.Single(ProjectAppliedLog.ReadAll(reloaded));
    }

    private static Proposal BuildProposal(IReadOnlyList<OperationEnvelope> operations) => new(
        contractVersions: new Dictionary<string, string> { ["grammar"] = "1.0" },
        proposalId: CanonicalId.Mint(),
        requires: null,
        operations: operations);

    private void AssertFeatStrucIsAbsent(Guid featStrucGuid)
    {
        using var stillOnDisk = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        Assert.False(
            stillOnDisk.ServiceLocator.GetInstance<ICmObjectRepository>().IsValidObjectId(featStrucGuid),
            "The dry run must not have mutated the saved project.");
    }
}
