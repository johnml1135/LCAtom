using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// The end-to-end path: a project's wordforms go in, a real <c>pangloss</c> run comes back, and a
/// <see cref="GrammarCoverageFigure"/> comes out citing exactly what produced it. Every other test in this
/// feature exercises one seam at a time against synthetic or captured data; this one proves the seams
/// actually fit together against a live project and a live parser run.
/// </summary>
/// <remarks>
/// Runs against <see cref="PristineProjectFixture"/>'s two seeded stems, not a realistic corpus, so it
/// cannot claim anything about coverage <i>quality</i> — only that the wiring produces a figure and that
/// the figure's own invariants (denominator bound, lower-bound flag, fraction range) hold for whatever the
/// parser actually returned.
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class GrammarCoverageFigureIntegrationTests : IDisposable
{
    private readonly LcmCache _cache;

    public GrammarCoverageFigureIntegrationTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        RealParserProject.PrepareForParsing(_cache, "m", "o", "t", "i", "f", "a", "b");
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [RealParserFact]
    public void ExtractingAnalysingAndComputing_ProducesAFigureThatCitesItsOwnRun()
    {
        // One word matching a seeded stem (should be analysable) and one that matches nothing.
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var factory = _cache.ServiceLocator.GetInstance<IWfiWordformFactory>();
            factory.Create(TsStringUtils.MakeString(SeededProject.FirstForm, _cache.DefaultVernWs));
            factory.Create(TsStringUtils.MakeString("zzznotaseededword", _cache.DefaultVernWs));
        });

        var corpus = LcmWordformCorpus.Extract(_cache, "seeded project (smoke sample)");
        Assert.NotEmpty(corpus.Words);

        var projectPath = _cache.ProjectId.Path;
        var parser = new PanGlossParser();

        var batchResult = parser.AnalyseBatch(projectPath, corpus.Words, ParserEngine.FstPrunedByHermitCrab);
        Assert.True(batchResult.Succeeded, batchResult.Refusal?.Detail ?? "the parser refused this grammar");

        var (report, refusal) = parser.Assess(projectPath, corpus.Words, ParserEngine.FstPrunedByHermitCrab);
        Assert.Null(refusal);
        Assert.NotNull(report);

        var figure = GrammarCoverageFigure.Compute(batchResult.Analysis!, corpus, report!);

        // Every field ADR 0032 §4 requires is present and traceable back to what actually ran.
        Assert.Equal(corpus.CorpusId, figure.CorpusId);
        Assert.Equal(corpus.Sha256, figure.CorpusSha256);
        Assert.Equal(report!.GrammarSourceSha256, figure.GrammarSourceSha256);
        Assert.StartsWith("sha256:", figure.GrammarSourceSha256);
        Assert.Equal(ParserEngine.FstPrunedByHermitCrab, figure.Engine);
        Assert.Equal(5000, figure.PerWordTimeoutMs);

        // The denominator can never exceed corpus size; every word is analysed, no-analysis, timed out, or skipped.
        var batch = batchResult.Analysis!;
        Assert.Equal(corpus.Words.Count, batch.Analysed + batch.NoAnalysis + batch.TimedOut + batch.Skipped);
        Assert.True(figure.Adjudicated <= corpus.Words.Count);
        Assert.Equal(batch.IsLowerBound, figure.IsLowerBound);

        if (figure.Adjudicated == 0)
            Assert.Null(figure.Fraction);
        else
            Assert.InRange(figure.Fraction!.Value, 0.0, 1.0);
    }
}
