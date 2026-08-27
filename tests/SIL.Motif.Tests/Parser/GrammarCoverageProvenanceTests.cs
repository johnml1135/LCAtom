using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Covers that a coverage figure cites the run it came from, using a report no parser had to produce.
/// </summary>
/// <remarks>
/// The sibling of this in <c>GrammarCoverageFigureIntegrationTests</c> keeps <c>RealParserFact</c> and
/// keeps skipping without a parser build, because it asserts a real grammar's reach. This one asserts
/// only that Motif carries the identities it was handed — plumbing, not linguistics — so a report built
/// here serves it honestly. That distinction is the whole reason both exist.
/// </remarks>
public sealed class GrammarCoverageProvenanceTests
{
    [Fact]
    public void TheFigureCitesTheCorpusAndGrammarItWasComputedFrom()
    {
        var corpus = CorpusDescriptor.Create("corpus/sample", ["motifa", "zzznotaword"]);
        var report = AssessReportParser.Parse(ReportJson);
        var batch = new BatchAnalysis(
            [new WordAnalysis(0, "motifa", 3, WordOutcome.Analysed, "sig-1"),
             new WordAnalysis(1, "zzznotaword", 2, WordOutcome.NoAnalysis, "sig-2")],
            ParserEngine.FstPrunedByHermitCrab, 5000, @"C:\projects\sample.fwdata", []);

        var figure = GrammarCoverageFigure.Compute(batch, corpus, report);

        Assert.Equal(corpus.CorpusId, figure.CorpusId);
        Assert.Equal(corpus.Sha256, figure.CorpusSha256);
        // The grammar identity comes from the parser's own hash rather than anything Motif computed.
        Assert.Equal(report.GrammarSourceSha256, figure.GrammarSourceSha256);
        Assert.Equal(ParserEngine.FstPrunedByHermitCrab, figure.Engine);
        Assert.Equal(5000, figure.PerWordTimeoutMs);
    }

    [Fact]
    public void EveryWordIsAccountedForAndTheFractionStaysWithinItsDenominator()
    {
        var corpus = CorpusDescriptor.Create("corpus/sample", ["a", "b", "c"]);
        var batch = new BatchAnalysis(
            [new WordAnalysis(0, "a", 1, WordOutcome.Analysed, "s"),
             new WordAnalysis(1, "b", 1, WordOutcome.NoAnalysis, "s"),
             new WordAnalysis(2, "c", 1, WordOutcome.TimedOut, "s")],
            ParserEngine.FstPrunedByHermitCrab, 5000, @"C:\projects\sample.fwdata", []);

        var figure = GrammarCoverageFigure.Compute(batch, corpus, AssessReportParser.Parse(ReportJson));

        Assert.Equal(corpus.Words.Count, batch.Analysed + batch.NoAnalysis + batch.TimedOut + batch.Skipped);
        Assert.True(figure.Adjudicated <= corpus.Words.Count);
        // A timed-out word means the figure can only be a floor, and must say so.
        Assert.True(figure.IsLowerBound);
        Assert.InRange(figure.Fraction!.Value, 0.0, 1.0);
    }

    private const string ReportJson = """
        {
          "keyTable": ["11111111-1111-1111-1111-111111111111"],
          "cases": [
            { "input": "motifa", "outcome": "complete",
              "analyses": [ { "identity": { "morphemes": [0], "rootIndex": 0 },
                              "identityDigest": "digest-1" } ] }
          ],
          "outcomeDigest": "sha256:aaaa",
          "semanticDigest": "sha256:bbbb",
          "provenance": { "sourceSha256": "sha256:cccc", "modelFingerprint": "fp-1" },
          "execution": { "pipeline": "foma-confirm" },
          "diagnostics": []
        }
        """;
}
