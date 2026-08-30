using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.Config;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Covers the two queries a scope's configuration can actually express: the declared default ("all words
/// carrying a manual analysis") and <see cref="WordQueryResolver.AllWordformsQueryText"/>, plus the refusal
/// that closes off anything else.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class WordQueryResolverTests : IDisposable
{
    private readonly LcmCache _cache;

    public WordQueryResolverTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    private IWfiWordform SeedApprovedWordform(string form)
    {
        IWfiWordform wordform = null!;
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(form, _cache.DefaultVernWs));
            var analysis = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            _cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.approves);
        });
        return wordform;
    }

    private IWfiWordform SeedUnanalysedWordform(string form)
    {
        IWfiWordform wordform = null!;
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(form, _cache.DefaultVernWs));
        });
        return wordform;
    }

    [Fact]
    public void TheDefaultQuery_YieldsOnlyWordformsCarryingAManualAnalysis()
    {
        SeedApprovedWordform("zzmotifqueryapproved");
        SeedUnanalysedWordform("zzmotifqueryunanalysed");

        var words = WordQueryResolver.Resolve(AssessmentScopeConfiguration.DefaultQueryText, _cache);

        Assert.Contains("zzmotifqueryapproved", words);
        Assert.DoesNotContain("zzmotifqueryunanalysed", words);
    }

    [Fact]
    public void TheDefaultQuery_OverAProjectWithNoManualAnalysis_YieldsAnEmptySelection()
    {
        SeedUnanalysedWordform("zzmotifquerynoanalysis");

        var words = WordQueryResolver.Resolve(AssessmentScopeConfiguration.DefaultQueryText, _cache);

        Assert.Empty(words);
    }

    [Fact]
    public void ADisapprovedAnalysis_DoesNotCountAsCarryingAManualAnalysis()
    {
        IWfiWordform wordform = null!;
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString("zzmotifquerydisapproved", _cache.DefaultVernWs));
            var analysis = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            _cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.disapproves);
        });

        var words = WordQueryResolver.Resolve(AssessmentScopeConfiguration.DefaultQueryText, _cache);

        Assert.DoesNotContain("zzmotifquerydisapproved", words);
    }

    [Fact]
    public void TheAllWordformsQuery_YieldsEveryWordformRegardlessOfManualAnalysis()
    {
        SeedApprovedWordform("zzmotifqueryallanalysed");
        SeedUnanalysedWordform("zzmotifqueryallunanalysed");

        var words = WordQueryResolver.Resolve(WordQueryResolver.AllWordformsQueryText, _cache);

        Assert.Contains("zzmotifqueryallanalysed", words);
        Assert.Contains("zzmotifqueryallunanalysed", words);
    }

    [Fact]
    public void AnUnrecognisedQuery_RefusesNamingIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WordQueryResolver.Resolve("every third word", _cache));

        Assert.Contains("every third word", exception.Message);
    }
}
