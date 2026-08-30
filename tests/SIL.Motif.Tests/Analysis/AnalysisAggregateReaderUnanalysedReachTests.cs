using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="AnalysisAggregateReader.Read"/>'s computation of
/// <see cref="AnalysisAggregateResponse.UnanalysedReach"/> — ADR 0038 decision 7's one counted figure —
/// against a real <c>LcmCache</c>. <see cref="UnanalysedReachFigureTests"/> covers the figure's own
/// rendering in isolation; this class is the one place that proves the population it counts is built
/// correctly from wordforms, <c>SpellingStatus</c> and a <see cref="StoredAssessment"/> together.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class AnalysisAggregateReaderUnanalysedReachTests : IDisposable
{
    private readonly LcmCache _cache;

    public AnalysisAggregateReaderUnanalysedReachTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    private IWfiWordform SeedWordform(string form, int spellingStatus = 0)
    {
        IWfiWordform wordform = null!;
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            wordform = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(form, _cache.DefaultVernWs));
            wordform.SpellingStatus = spellingStatus;
        });
        return wordform;
    }

    private void ApproveManualAnalysis(IWfiWordform wordform)
    {
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var analysis = _cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            var bundle = _cache.ServiceLocator.GetInstance<IWfiMorphBundleFactory>().Create();
            analysis.MorphBundlesOS.Add(bundle);
            _cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.approves);
        });
    }

    private static AssessedWord Parsed(string word) =>
        new(word, "Analysed", new[] { new ParsedAnalysis(null, new[] { "m1" }, 0, "digest-" + word) });

    private static AssessedWord Unparsed(string word) => new(word, "NoAnalysis", Array.Empty<ParsedAnalysis>());

    private static StoredAssessment Assessment(params AssessedWord[] words)
    {
        var report = new AssessReport(
            Words: words,
            OutcomeDigest: "irrelevant",
            SemanticDigest: "irrelevant",
            GrammarSourceSha256: "sha256:" + new string('a', 64),
            ModelFingerprint: "irrelevant",
            Pipeline: "foma-confirm",
            DiagnosticCount: 0);
        var corpus = Selection.Create("reach-test", words.Select(w => w.Word));
        return new StoredAssessment(report, corpus);
    }

    [Fact]
    public void NoAssessmentSupplied_UnanalysedReachIsNull()
    {
        SeedWordform("zzReachNoAssessment");

        var response = AnalysisAggregateReader.Read(_cache);

        Assert.Null(response.UnanalysedReach);
    }

    [Fact]
    public void ExcludesWordFormsWithAManualAnalysis_RegardlessOfWhatTheGrammarSays()
    {
        var withManual = SeedWordform("zzReachHasManual");
        ApproveManualAnalysis(withManual);

        var response = AnalysisAggregateReader.Read(_cache, Assessment(Parsed("zzReachHasManual")));

        // Has a test already: not part of the unanalysed population at all (ADR 0038 decision 7).
        Assert.Equal(0, response.UnanalysedReach!.UnanalysedCount);
    }

    [Fact]
    public void ExcludesWordFormsMarkedIncorrectlySpelled_ButIncludesUndecidedAndCorrect()
    {
        SeedWordform("zzReachIncorrect", spellingStatus: 2); // Incorrect
        SeedWordform("zzReachUndecided", spellingStatus: 0); // Undecided - the default, still counted
        SeedWordform("zzReachCorrect", spellingStatus: 1); // Correct

        var response = AnalysisAggregateReader.Read(
            _cache,
            Assessment(
                Parsed("zzReachIncorrect"),
                Parsed("zzReachUndecided"),
                Parsed("zzReachCorrect")));

        // Only the Incorrect one is excluded - "nobody has judged this yet" is not "known to be wrong".
        Assert.Equal(2, response.UnanalysedReach!.UnanalysedCount);
        Assert.Equal(2, response.UnanalysedReach.ParsedCount);
    }

    [Fact]
    public void AWordFormTheAssessmentDidNotCover_CountsAsNotParsed()
    {
        SeedWordform("zzReachNotCovered");

        // The assessment exists but says nothing about this particular word form.
        var response = AnalysisAggregateReader.Read(_cache, Assessment(Parsed("someOtherWord")));

        Assert.Equal(1, response.UnanalysedReach!.UnanalysedCount);
        Assert.Equal(0, response.UnanalysedReach.ParsedCount);
    }

    [Fact]
    public void ParsedCount_OnlyCountsWordFormsTheAssessmentActuallyProducedAnAnalysisFor()
    {
        SeedWordform("zzReachParsed");
        SeedWordform("zzReachUnparsed");

        var response = AnalysisAggregateReader.Read(
            _cache, Assessment(Parsed("zzReachParsed"), Unparsed("zzReachUnparsed")));

        Assert.Equal(2, response.UnanalysedReach!.UnanalysedCount);
        Assert.Equal(1, response.UnanalysedReach.ParsedCount);
    }
}
