using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// The end-to-end path: a real project's wordforms go in, a real <c>pangloss</c> run comes back, and a
/// <see cref="CoverageFigure"/> comes out citing exactly what produced it. Every other test in this feature
/// exercises one seam at a time against synthetic or captured data; this one proves the seams actually fit
/// together against the real Sena 3 project.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class CoverageFigureIntegrationTests
{
    [RealParserFact]
    public void ExtractingAnalysingAndComputing_ProducesAFigureThatCitesItsOwnRun()
    {
        var projectPath = RealProject.Sena3Path()!;

        // A small cap: this test's purpose is to prove the wiring, not to time a full 6,973-word corpus
        // (that cost is already measured in docs/research/2026-08-06-parser-timing-measured.md).
        CorpusDescriptor corpus;
        using (var cache = new FwDataProjectLoader().LoadCache(projectPath))
        {
            corpus = LcmWordformCorpus.Extract(cache, "Sena 3 (smoke sample)", limit: 8);
        }

        Assert.NotEmpty(corpus.Words);

        var parser = new PanGlossParser();

        var batchResult = parser.AnalyseBatch(projectPath, corpus.Words, ParserEngine.FstPrunedByHermitCrab);
        Assert.True(batchResult.Succeeded, batchResult.Refusal?.Detail ?? "the parser refused this grammar");

        var (report, refusal) = parser.Assess(projectPath, corpus.Words, ParserEngine.FstPrunedByHermitCrab);
        Assert.Null(refusal);
        Assert.NotNull(report);

        var figure = CoverageFigure.Compute(batchResult.Analysis!, corpus, report!);

        // Every field ADR 0032 §4 requires is present and traceable back to what actually ran.
        Assert.Equal(corpus.CorpusId, figure.CorpusId);
        Assert.Equal(corpus.Sha256, figure.CorpusSha256);
        Assert.Equal(report!.GrammarSourceSha256, figure.GrammarSourceSha256);
        Assert.StartsWith("sha256:", figure.GrammarSourceSha256);
        Assert.Equal(ParserEngine.FstPrunedByHermitCrab, figure.Engine);
        Assert.Equal(5000, figure.PerWordTimeoutMs);

        // The denominator can never exceed the corpus size, and every word is accounted for one way or
        // another (analysed, no-analysis, timed out, or skipped).
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
