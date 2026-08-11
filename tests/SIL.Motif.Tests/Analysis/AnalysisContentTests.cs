using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="AnalysisContent.ComputeDigest"/> — Motif's mirror of FieldWorks' own
/// <c>ParseAnalysis.MatchesIWfiAnalysis</c>, the content shape ADR 0038 decision 3 requires analyses be
/// compared by, since a <c>WfiAnalysis</c> GUID is not durable identity.
/// </summary>
/// <remarks>
/// The rules defended here, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item>Content, and only content, decides identity: two <see cref="MorphBundleContent"/> lists that
/// name the same <c>MorphRA</c>/<c>MsaRA</c>/<c>InflTypeRA</c> guids digest identically no matter which
/// <c>WfiAnalysis</c> or <c>WfiMorphBundle</c> objects they came from. This is the entire reason a digest
/// exists instead of a GUID — get it wrong and an analysis edited into an equivalent new object silently
/// reads as a different test, exactly the bug ADR 0038 exists to avoid.</item>
/// <item>The digest must be sensitive to morph order, morph count, and each of the three references
/// independently — an insensitive digest calls two linguistically different analyses "the same test" and
/// quietly stops checking one of them.</item>
/// <item>Sense and word-level category never enter this type at all — there is no field for them, so a
/// human's sense choice cannot silently split what would otherwise be one test into two, or vice versa
/// (ADR 0027, which this codebase treats as settled: the parser cannot supply either).</item>
/// </list>
/// </remarks>
public class AnalysisContentTests
{
    [Fact]
    public void ContentBuiltFromSeparateEqualBundleLists_DigestsIdentically()
    {
        // Stands in for the same analysis recreated as a new WfiAnalysis (ADR 0038 decision 3): instance id irrelevant.
        var a = new List<MorphBundleContent> { new("morph-1", "msa-1", "infl-1") };
        var b = new List<MorphBundleContent> { new("morph-1", "msa-1", "infl-1") };

        Assert.Equal(AnalysisContent.ComputeDigest(a), AnalysisContent.ComputeDigest(b));
    }

    [Fact]
    public void MorphOrderIsPartOfTheContent_ReversingItChangesTheDigest()
    {
        // MorphBundlesOS is an owning SEQUENCE: morpheme order is content, so the digest must be order-sensitive.
        var forward = new List<MorphBundleContent> { new("m1", "s1", null), new("m2", "s2", null) };
        var backward = new List<MorphBundleContent> { new("m2", "s2", null), new("m1", "s1", null) };

        Assert.NotEqual(AnalysisContent.ComputeDigest(forward), AnalysisContent.ComputeDigest(backward));
    }

    [Fact]
    public void BundleCountAffectsTheDigest_EvenWhenTheSharedPrefixIsIdentical()
    {
        var one = new List<MorphBundleContent> { new("m1", "s1", null) };
        var two = new List<MorphBundleContent> { new("m1", "s1", null), new("m2", "s2", null) };

        Assert.NotEqual(AnalysisContent.ComputeDigest(one), AnalysisContent.ComputeDigest(two));
    }

    [Fact]
    public void EachOfMorphMsaAndInflTypeIndependentlyChangesTheDigest()
    {
        var baseline = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m1", "s1", "i1") });
        var differentMorph = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m2", "s1", "i1") });
        var differentMsa = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m1", "s2", "i1") });
        var differentInflType = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m1", "s1", "i2") });

        // Each field is load-bearing: dropping any one loses a real distinction FieldWorks' own comparison makes.
        Assert.NotEqual(baseline, differentMorph);
        Assert.NotEqual(baseline, differentMsa);
        Assert.NotEqual(baseline, differentInflType);
    }

    [Fact]
    public void ANullInflTypeIsDistinctFromABundleThatHasOne()
    {
        // InflTypeRA is genuinely optional (MasterLCModel.xml); absence must not collide with a bundle that has one.
        var withoutInflType = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m1", "s1", null) });
        var withInflType = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent("m1", "s1", "infl-1") });

        Assert.NotEqual(withoutInflType, withInflType);
    }

    [Fact]
    public void AnEmptyAnalysis_DigestsDifferentlyFromAOneBundleAnalysis()
    {
        // Without folding the count in, an empty list and a one-bundle list with no references would collide.
        var empty = AnalysisContent.ComputeDigest(Array.Empty<MorphBundleContent>());
        var oneEmptyBundle = AnalysisContent.ComputeDigest(new[] { new MorphBundleContent(null, null, null) });

        Assert.NotEqual(empty, oneEmptyBundle);
    }
}
