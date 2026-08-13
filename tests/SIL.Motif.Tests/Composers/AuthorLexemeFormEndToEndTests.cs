using System;
using System.Collections.Generic;
using System.Linq;
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
/// The construct's full loop on a real project: authored (JSON through
/// <see cref="AuthorLexemeFormIntentParser"/>), lowered (<see cref="AuthorLexemeFormComposer.Build"/>),
/// dry-run and reviewed, applied, and saved — through <see cref="CliSession"/>, the same seam the CLI
/// itself drives. Never attempts the Chorus Send/Receive leg: that needs an external system and is a
/// standing, separately-tracked risk, not something this test can honestly simulate.
/// </summary>
/// <remarks>
/// Split across two tests rather than one, and that split is itself a finding worth recording. The
/// full three-operation construct (including the <c>dependsOn</c>-chained <c>setIsAbstract</c>, whose
/// target is an entity this same Proposal's first operation creates) dry-runs correctly —
/// <see cref="AllThreeOperations_DryRunAgainstARealProject_ProduceTheCorrectEffects"/> proves it — but
/// <see cref="ProposalApplier.Apply"/>'s pre-flight drift check
/// (<see cref="FootprintProbe.ComputeCurrentFootprintDigest"/>) reads every operation's <em>current
/// live</em> footprint before any operation has run, including one whose target does not exist yet
/// because an earlier operation in the same Proposal is what creates it, and that read throws. This
/// is a pre-existing Runner gap in the entity-creation-then-same-target-chaining path, not something
/// introduced or worked around here, and not a claim this construct's design can settle unilaterally.
/// <see cref="Authored_Lowered_DryRun_Applied_Saved_RoundTripsOnARealProject"/> therefore proves the
/// full authored-to-saved loop with a construct instance that does not chain a target onto a
/// same-Proposal <c>entityId</c> — <c>setGloss</c> targets the sense, which already exists — so it
/// exercises exactly the part of the pipeline this Runner gap does not block.
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AuthorLexemeFormEndToEndTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public AuthorLexemeFormEndToEndTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void AllThreeOperations_DryRunAgainstARealProject_ProduceTheCorrectEffects()
    {
        var entryId = CanonicalId.FromGuid(_seed.SecondEntryId);
        var senseId = CanonicalId.FromGuid(_seed.SecondSenseId);
        const string formText = "zzMotifTemplateForm";
        const string glossText = "a newly authored, root-and-pattern headword";

        var intent = new AuthorLexemeFormIntent(
            entryId, CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem), "fr", formText,
            IsAbstract: true, Sense: senseId, GlossWritingSystem: "en", GlossText: glossText);

        using var session = CliSession.Open(_fwDataPath);
        var operations = AuthorLexemeFormComposer.Build(session.LiveCache, intent);
        Assert.Equal(3, operations.Count);
        var newFormId = operations[0].EntityId!.Value;

        var proposal = BuildProposal(operations);
        var dryRun = session.DryRun(proposal);

        Assert.Equal(3, dryRun.ExpectedEffects.Count);
        var abstractEffect = dryRun.ExpectedEffects.Single(e => e.CanonicalId == newFormId);
        Assert.Equal("true", abstractEffect.After.Values.Single());
        var glossEffect = dryRun.ExpectedEffects.Single(e => e.CanonicalId == senseId);
        Assert.Equal(SeededProject.SecondGloss, glossEffect.Before["en"]);
        Assert.Equal(glossText, glossEffect.After["en"]);
    }

    [Fact]
    public void Authored_Lowered_DryRun_Applied_Saved_RoundTripsOnARealProject()
    {
        var entryId = CanonicalId.FromGuid(_seed.SecondEntryId);
        var senseId = CanonicalId.FromGuid(_seed.SecondSenseId);
        var morphType = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem);
        const string formText = "zzMotifHeadword";
        const string glossText = "a newly authored headword";

        // --- authored: an agent's JSON, through the construct's own closed-schema parser ---
        var authoredJson = JsonSerializer.Serialize(new
        {
            entry = entryId.Value,
            morphType = morphType.Value,
            ws = "fr",
            text = formText,
            sense = senseId.Value,
            glossWs = "en",
            glossText,
        });
        using var authoredDocument = JsonDocument.Parse(authoredJson);
        var intent = AuthorLexemeFormIntentParser.Parse(authoredDocument.RootElement);

        using var session = CliSession.Open(_fwDataPath);

        // --- lowered: one construct becomes two correctly-ordered Layer-0 operations ---
        var operations = AuthorLexemeFormComposer.Build(session.LiveCache, intent);
        Assert.Equal(2, operations.Count);
        var newFormId = operations[0].EntityId!.Value;

        var proposal = BuildProposal(operations);

        // --- dry-run and review: real before/after read back from LibLCM, nothing mutated yet ---
        var dryRun = session.DryRun(proposal);
        Assert.Equal(2, dryRun.ExpectedEffects.Count);
        // Seeded entry already has a lexeme form; create-into-occupied replaces it, so Before names it.
        var createEffect = dryRun.ExpectedEffects.Single(e => e.CanonicalId == entryId);
        Assert.Equal(_seed.SecondLexemeFormId, CanonicalId.Parse(createEffect.Before["ref"]).ToGuid());
        Assert.Equal(newFormId.Value, createEffect.After["ref"]);
        var glossEffect = dryRun.ExpectedEffects.Single(e => e.CanonicalId == senseId);
        Assert.Equal(SeededProject.SecondGloss, glossEffect.Before["en"]);
        Assert.Equal(glossText, glossEffect.After["en"]);

        AssertFormIsAbsent(newFormId.ToGuid());

        // --- applied and saved ---
        var receipt = session.Apply(proposal, dryRun.Anchor, "motif-tests", "AuthorLexemeForm end-to-end test");
        Assert.False(receipt.AlreadyApplied);

        // Re-open from disk: proves Apply's Save, not just the in-memory mutation, actually happened.
        using var reloaded = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        var entry = reloaded.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(_seed.SecondEntryId);
        var form = entry.LexemeFormOA;
        Assert.NotNull(form);
        Assert.Equal(newFormId.ToGuid(), form!.Guid);
        Assert.Equal(MoMorphTypeTags.kguidMorphStem, form.MorphTypeRA.Guid);
        var wsHandle = reloaded.WritingSystemFactory.GetWsFromStr("fr");
        Assert.Equal(formText, form.Form.get_String(wsHandle).Text);

        var sense = reloaded.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(_seed.SecondSenseId);
        var analWsHandle = reloaded.WritingSystemFactory.GetWsFromStr("en");
        Assert.Equal(glossText, sense.Gloss.get_String(analWsHandle).Text);

        Assert.Single(ProjectAppliedLog.ReadAll(reloaded));
    }

    private static Proposal BuildProposal(IReadOnlyList<OperationEnvelope> operations) => new(
        contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0", ["grammar"] = "1.0" },
        proposalId: CanonicalId.Mint(),
        requires: null,
        operations: operations);

    private void AssertFormIsAbsent(Guid formGuid)
    {
        using var stillOnDisk = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        Assert.False(
            stillOnDisk.ServiceLocator.GetInstance<ICmObjectRepository>().IsValidObjectId(formGuid),
            "The dry run must not have mutated the saved project.");
    }
}
