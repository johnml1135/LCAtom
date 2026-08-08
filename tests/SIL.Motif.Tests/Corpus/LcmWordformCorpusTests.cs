using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.Parser;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Extraction from a real project. Gated by <see cref="RealParserFactAttribute"/> — that attribute also
/// requires the <c>pangloss</c> executable, which this test does not itself need, but it is the existing
/// gate that already knows how to find the real Sena 3 project and skip gracefully without it, and
/// duplicating that discovery for one test class is not worth a second attribute.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class LcmWordformCorpusTests
{
    [RealParserFact]
    public void Extract_ProducesTheSameDescriptorAsHashingExtractFormsDirectly()
    {
        using var cache = new FwDataProjectLoader().LoadCache(RealProject.Sena3Path()!);

        var viaExtract = LcmWordformCorpus.Extract(cache, "Sena 3", limit: 25);
        var viaFormsThenCreate = CorpusDescriptor.Create("Sena 3", LcmWordformCorpus.ExtractForms(cache).Take(25));

        // Extract is documented as ExtractForms (capped) piped into CorpusDescriptor.Create; this pins that
        // there is no hidden divergence between the two paths.
        Assert.Equal(viaFormsThenCreate.Sha256, viaExtract.Sha256);
        Assert.Equal(viaFormsThenCreate.Words, viaExtract.Words);
    }

    [RealParserFact]
    public void Extract_TheLimitCapsHowManyFormsAreHashed()
    {
        using var cache = new FwDataProjectLoader().LoadCache(RealProject.Sena3Path()!);

        var capped = LcmWordformCorpus.Extract(cache, "Sena 3 (capped)", limit: 5);
        var uncapped = LcmWordformCorpus.Extract(cache, "Sena 3 (whole)");

        Assert.True(capped.Words.Count <= 5);
        // Sena 3 has on the order of 6,973 word forms (docs/research/2026-08-06-parser-timing-measured.md),
        // so an uncapped extraction must produce strictly more than a 5-word cap did.
        Assert.True(uncapped.Words.Count > capped.Words.Count);
        Assert.NotEqual(uncapped.Sha256, capped.Sha256);
    }

    [RealParserFact]
    public void Extract_EveryFormIsNonEmptyText()
    {
        using var cache = new FwDataProjectLoader().LoadCache(RealProject.Sena3Path()!);

        var descriptor = LcmWordformCorpus.Extract(cache, "Sena 3", limit: 100);

        Assert.NotEmpty(descriptor.Words);
        Assert.All(descriptor.Words, word => Assert.False(string.IsNullOrWhiteSpace(word)));
    }
}
