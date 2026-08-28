using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.Composers;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Apply;

/// <summary>
/// The two sides of the drift check must measure the same thing. A Dry Run builds its footprint while
/// running operations one after another; Apply's pre-flight builds its own by reading every target
/// before anything runs. Those are different computations over the same Proposal, and a Proposal is
/// refused when they disagree — so a shape where they disagree for a reason other than real drift is
/// indistinguishable from drift, and a shape where one of them cannot be computed at all cannot apply.
/// </summary>
/// <remarks>
/// Excluding targets the Proposal mints is what makes the two agree, and this is the test that keeps
/// them agreeing: the rule lives in one place both sides call, and every shape below asserts the two
/// digests are equal. Before that rule existed, the chained shapes here could not produce an Apply
/// digest at all — the pre-flight read a target no operation had created yet and threw, so a Proposal
/// dry-ran cleanly and then failed at apply.
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class FootprintPlanAgreementTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public FootprintPlanAgreementTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void NoOperationTargetsAMintedEntity_TheDigestsAgree() =>
        AssertDigestsAgree(cache => Compose(cache, isAbstract: false, withSense: true));

    [Fact]
    public void OneOperationTargetsAMintedEntity_TheDigestsAgree() =>
        AssertDigestsAgree(cache => Compose(cache, isAbstract: true, withSense: false));

    [Fact]
    public void AChainedOperationAlongsideAnIndependentOne_TheDigestsAgree() =>
        AssertDigestsAgree(cache => Compose(cache, isAbstract: true, withSense: true));

    [Fact]
    public void TwoChainsInOneProposal_TheDigestsAgree() =>
        AssertDigestsAgree(cache =>
        {
            // Different entries: LexemeForm is owning/atomic, so two creates on one entry would address one slot.
            var first = Compose(cache, isAbstract: true, withSense: false, entryId: _seed.SecondEntryId);
            var second = Compose(
                cache, isAbstract: true, withSense: false, entryId: _seed.FirstEntryId, text: "zzMotifSecondForm");
            return first.Concat(second).ToList();
        });

    [Fact]
    public void AMintedTargetReachedTwoOperationsLater_TheDigestsAgree() =>
        AssertDigestsAgree(cache =>
        {
            // The gloss sits between the create and the operation targeting what the create minted.
            var chained = Compose(cache, isAbstract: true, withSense: true);
            return new List<OperationEnvelope> { chained[0], chained[2], chained[1] };
        });

    private void AssertDigestsAgree(Func<LcmCache, IReadOnlyList<OperationEnvelope>> compose)
    {
        var loader = new FwDataProjectLoader();
        using var cache = loader.LoadCache(_fwDataPath);
        var proposal = BuildProposal(compose(cache));

        // The Dry Run mutates a throwaway copy, so the live project is still at the state Apply reads.
        var scratchRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.FootprintAgreement", Guid.NewGuid().ToString("N"));
        loader.Save(cache);
        using var scratch = DryRunScratch.Adopt(
            new ScratchCacheFactory(loader).CreateFromFileCopy(_fwDataPath, scratchRoot),
            $"file copy of {_fwDataPath}",
            onDisposed: () =>
            {
                if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
            });

        var dryRunDigest = ProposalDryRunner.Run(scratch, proposal).Anchor.FootprintDigest;
        var applyDigest = FootprintProbe.ComputeCurrentFootprintDigest(cache, proposal);

        Assert.Equal(dryRunDigest, applyDigest);
    }

    private IReadOnlyList<OperationEnvelope> Compose(
        LcmCache cache, bool isAbstract, bool withSense,
        Guid? entryId = null, string text = "zzMotifAgreementForm")
    {
        var intent = new AuthorLexemeFormIntent(
            CanonicalId.FromGuid(entryId ?? _seed.SecondEntryId),
            CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem),
            "fr",
            text,
            IsAbstract: isAbstract,
            Sense: withSense ? CanonicalId.FromGuid(_seed.SecondSenseId) : null,
            GlossWritingSystem: withSense ? "en" : null,
            GlossText: withSense ? "an agreement-test gloss" : null);

        return AuthorLexemeFormComposer.Build(cache, intent);
    }

    private static Proposal BuildProposal(IReadOnlyList<OperationEnvelope> operations) =>
        new(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: operations);
}
