using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Extraction from a live project's <see cref="IWfiWordform"/> records, seeded directly rather than read
/// from a real corpus: this utility only needs wordforms to exist, not corpus scale, and every test below
/// seeds exactly the ones its own assertion depends on.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class LcmWordformCorpusTests : IDisposable
{
    private readonly LcmCache _cache;

    public LcmWordformCorpusTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    private void SeedWordforms(params string[] forms)
    {
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var factory = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>();
            foreach (var form in forms)
                factory.Create(TsStringUtils.MakeString(form, _cache.DefaultVernWs));
        });
    }

    [Fact]
    public void Extract_ProducesTheSameDescriptorAsHashingExtractFormsDirectly()
    {
        SeedWordforms("motifwf-a", "motifwf-b", "motifwf-c");

        var viaExtract = LcmWordformCorpus.Extract(_cache, "seeded", limit: 25);
        var viaFormsThenCreate = CorpusDescriptor.Create("seeded", LcmWordformCorpus.ExtractForms(_cache).Take(25));

        // Extract is documented as ExtractForms (capped) piped into CorpusDescriptor.Create; pins no divergence.
        Assert.Equal(viaFormsThenCreate.Sha256, viaExtract.Sha256);
        Assert.Equal(viaFormsThenCreate.Words, viaExtract.Words);
    }

    [Fact]
    public void Extract_TheLimitCapsHowManyFormsAreHashed()
    {
        SeedWordforms("motifwf-1", "motifwf-2", "motifwf-3", "motifwf-4",
            "motifwf-5", "motifwf-6", "motifwf-7", "motifwf-8");

        var capped = LcmWordformCorpus.Extract(_cache, "seeded (capped)", limit: 5);
        var uncapped = LcmWordformCorpus.Extract(_cache, "seeded (whole)");

        Assert.True(capped.Words.Count <= 5);
        Assert.True(uncapped.Words.Count > capped.Words.Count);
        Assert.NotEqual(uncapped.Sha256, capped.Sha256);
    }

    [Fact]
    public void Extract_SkipsWordformsWithNoTextInTheDefaultVernacularWritingSystem()
    {
        SeedWordforms("motifwf-x", "motifwf-y");
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            _cache.ServiceLocator.GetInstance<IWfiWordformFactory>().Create(); // no Form in any writing system
        });

        var descriptor = LcmWordformCorpus.Extract(_cache, "seeded");

        // Proves the documented skip behaviour, not just that non-empty forms stay non-empty.
        Assert.Equal(2, descriptor.Words.Count);
        Assert.All(descriptor.Words, word => Assert.False(string.IsNullOrWhiteSpace(word)));
    }
}
