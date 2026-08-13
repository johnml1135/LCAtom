using SIL.Motif.Host.Analysis;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// The one place these types touch a real <c>LcmCache</c>: <see cref="AnalysisAggregateReader"/> itself.
/// Everything else in this folder — <see cref="AnalysisContentTests"/>, <see cref="AnalysisAggregateDiffTests"/>,
/// <see cref="AnalysisAggregateResponseTests"/>, <see cref="WordFormAnalysisAggregateTests"/> — is pure and
/// needs no project, following <c>CorpusDescriptorTests</c>' house pattern. This class exists only to prove
/// the reader's wiring — <c>IWfiWordform.HumanApprovedAnalyses</c>, content comparison, and the
/// <c>Opinions</c> tri-state — against a real project, using the same fixture and unit-of-work pattern
/// already established by <c>GeneratedSlice3OperationsTests</c> and <c>SaveIsSynchronousTests</c> rather
/// than inventing a new one.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AnalysisAggregateReaderRealProjectTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public AnalysisAggregateReaderRealProjectTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Fact]
    public void TwoApprovedAnalysesOnOneWordform_BothAppear_AsASetWithDistinctDigests()
    {
        var vernWs = _cache.DefaultVernWs;

        var formRepo = _cache.ServiceLocator.GetInstance<IMoFormRepository>();
        var firstForm = formRepo.GetObject(_seed.FirstLexemeFormId);
        var secondForm = formRepo.GetObject(_seed.SecondLexemeFormId);
        var msa = _cache.ServiceLocator.GetInstance<IMoStemMsaRepository>().AllInstances().First();

        IWfiWordform wordform = null!;

        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString("zzMotifTestWord", vernWs));

            var first = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(first);
            var firstBundle = _cache.ServiceLocator.GetInstance<IWfiMorphBundleFactory>().Create();
            first.MorphBundlesOS.Add(firstBundle);
            firstBundle.MorphRA = firstForm;
            firstBundle.MsaRA = msa;

            var second = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(second);
            var secondBundle = _cache.ServiceLocator.GetInstance<IWfiMorphBundleFactory>().Create();
            second.MorphBundlesOS.Add(secondBundle);
            secondBundle.MorphRA = secondForm; // a different allomorph -> a different content digest
            secondBundle.MsaRA = msa;

            _cache.LangProject.DefaultUserAgent.SetEvaluation(first, Opinions.approves);
            _cache.LangProject.DefaultUserAgent.SetEvaluation(second, Opinions.approves);
        });

        // Reads only human-approved analyses; must not invoke the parser (ADR 0038 decision 5).
        var response = AnalysisAggregateReader.Read(_cache);

        var read = response.WordForms.Single(w => w.WordformGuid == wordform.Guid.ToString());
        Assert.Equal("zzMotifTestWord", read.Form);

        // The unit is a set, not "the analysis" (ADR 0038 decision 2): both approved readings are present.
        Assert.Equal(2, read.ManualAnalyses.Count);
        Assert.NotEqual(read.ManualAnalyses[0].ContentDigest, read.ManualAnalyses[1].ContentDigest);

        // Nothing was supplied to Read about the parser side: "not known", never "known to be empty".
        Assert.Null(read.AutomaticAnalyses);
        Assert.Null(read.AutomaticAnalysisCount);
    }

    [Fact]
    public void ADisapprovedAnalysis_DoesNotAppearAmongTheApprovedSet()
    {
        var vernWs = _cache.DefaultVernWs;
        var form = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(_seed.FirstLexemeFormId);
        var msa = _cache.ServiceLocator.GetInstance<IMoStemMsaRepository>().AllInstances().First();

        IWfiWordform wordform = null!;

        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString("zzMotifDisapprovedWord", vernWs));

            var analysis = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            var bundle = _cache.ServiceLocator.GetInstance<IWfiMorphBundleFactory>().Create();
            analysis.MorphBundlesOS.Add(bundle);
            bundle.MorphRA = form;
            bundle.MsaRA = msa;

            _cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.disapproves);
        });

        var response = AnalysisAggregateReader.Read(_cache);

        var read = response.WordForms.Single(w => w.WordformGuid == wordform.Guid.ToString());

        // Opinions is tri-state; disapproved is not "no opinion". Mirrors IWfiWordform.HumanApprovedAnalyses.
        Assert.Empty(read.ManualAnalyses);
    }

    [Fact]
    public void WithNoStoredAssessment_ReadStillReturnsTheManualSide_AndReportsNoAssessmentRatherThanParsing()
    {
        var vernWs = _cache.DefaultVernWs;
        var form = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(_seed.FirstLexemeFormId);
        var msa = _cache.ServiceLocator.GetInstance<IMoStemMsaRepository>().AllInstances().First();

        IWfiWordform wordform = null!;
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString("zzMotifNoAssessmentWord", vernWs));

            var analysis = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            var bundle = _cache.ServiceLocator.GetInstance<IWfiMorphBundleFactory>().Create();
            analysis.MorphBundlesOS.Add(bundle);
            bundle.MorphRA = form;
            bundle.MsaRA = msa;

            _cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.approves);
        });

        // No StoredAssessment argument at all - ADR 0038 decision 5: this must not fall back to parsing.
        var response = AnalysisAggregateReader.Read(_cache);

        Assert.False(response.HasAssessment);
        Assert.Null(response.UnanalysedReach);
        Assert.Contains("No assessment is on record", response.DescribeAssessmentState("sha256:a", "sha256:b"));

        var read = response.WordForms.Single(w => w.WordformGuid == wordform.Guid.ToString());
        Assert.Single(read.ManualAnalyses); // the manual side - the test suite - needs no parser at all
        Assert.Null(read.AutomaticAnalyses); // "not known", never "known to be empty"
    }
}
