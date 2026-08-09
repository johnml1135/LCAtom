using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Unit tests for assembling a <see cref="GrammarCoverageFigure"/> from a <see cref="BatchAnalysis"/> and a
/// <see cref="CorpusDescriptor"/>. Built on synthetic rows rather than captured fixtures — the mapping from
/// raw parser text to <see cref="WordAnalysis"/> is already covered by <see cref="ParserOutputTests"/>
/// against real captured output; what is new and untested here is purely the arithmetic and provenance
/// assembly on top of already-typed results.
/// </summary>
public class GrammarCoverageFigureTests
{
    private static BatchAnalysis Batch(params (string Word, WordOutcome Outcome)[] words) =>
        new(
            Words: words.Select((w, i) => new WordAnalysis(i, w.Word, ElapsedMs: 10, w.Outcome, Signature: "-")).ToList(),
            Engine: ParserEngine.FstPrunedByHermitCrab,
            PerWordTimeoutMs: 5000,
            ProjectPath: "irrelevant.fwdata",
            Warnings: Array.Empty<string>());

    [Fact]
    public void Compute_DenominatorExcludesTimedOutAndSkippedWords()
    {
        var batch = Batch(
            ("mbali", WordOutcome.Analysed),
            ("ya", WordOutcome.Analysed),
            ("nkazi", WordOutcome.NoAnalysis),
            ("munthu", WordOutcome.TimedOut),
            ("anthu", WordOutcome.Skipped));
        var corpus = CorpusDescriptor.Create("test-corpus", batch.Words.Select(w => w.Word));

        var figure = GrammarCoverageFigure.Compute(batch, corpus, "sha256:" + new string('a', 64));

        // Denominator is analysed (2) + no-analysis (1) = 3, not the 5 rows in the batch.
        Assert.Equal(3, figure.Adjudicated);
        Assert.Equal(2, figure.Analysed);
        Assert.Equal(1, figure.TimedOutCount);
        Assert.Equal(2.0 / 3.0, figure.Fraction);
    }

    [Fact]
    public void Compute_MarksLowerBoundWhenAnyWordTimedOut()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.TimedOut));
        var corpus = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, corpus, "sha256:" + new string('a', 64));

        Assert.True(figure.IsLowerBound);
    }

    [Fact]
    public void Compute_NotALowerBoundWhenNoWordTimedOut()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.NoAnalysis));
        var corpus = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, corpus, "sha256:" + new string('a', 64));

        Assert.False(figure.IsLowerBound);
        Assert.Equal(0, figure.TimedOutCount);
    }

    [Fact]
    public void Compute_FractionIsNullWhenNothingWasAdjudicated()
    {
        // Every word either timed out or was skipped: nothing was ever judged against the grammar, so there
        // is no honest percentage to report — not 0%, not 100%.
        var batch = Batch(("mbali", WordOutcome.TimedOut), ("ya", WordOutcome.Skipped));
        var corpus = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya" });

        var figure = GrammarCoverageFigure.Compute(batch, corpus, "sha256:" + new string('a', 64));

        Assert.Equal(0, figure.Adjudicated);
        Assert.Null(figure.Fraction);
        Assert.True(figure.IsLowerBound);
    }

    [Fact]
    public void Compute_CitesTheFullProvenanceSetAdr0032Requires()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed));
        var corpus = CorpusDescriptor.Create("Sena 3", new[] { "mbali" });
        var grammarHash = "sha256:" + new string('b', 64);

        var figure = GrammarCoverageFigure.Compute(batch, corpus, grammarHash);

        Assert.Equal(corpus.CorpusId, figure.CorpusId);
        Assert.Equal(corpus.Sha256, figure.CorpusSha256);
        Assert.Equal(grammarHash, figure.GrammarSourceSha256);
        Assert.Equal(ParserEngine.FstPrunedByHermitCrab, figure.Engine);
        Assert.Equal(5000, figure.PerWordTimeoutMs);
        Assert.Equal(0, figure.TimedOutCount);
    }

    [Fact]
    public void Compute_ThrowsWhenTheBatchsWordsDoNotMatchTheCorpus()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed), ("ya", WordOutcome.Analysed));
        var corpus = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "somethingElse" });

        var ex = Assert.Throws<ArgumentException>(
            () => GrammarCoverageFigure.Compute(batch, corpus, "sha256:" + new string('a', 64)));

        Assert.Contains("test-corpus", ex.Message);
    }

    [Fact]
    public void Compute_TheAssessReportOverload_PullsTheGrammarHashFromTheReport()
    {
        var batch = Batch(("mbali", WordOutcome.Analysed));
        var corpus = CorpusDescriptor.Create("test-corpus", new[] { "mbali" });
        var report = new AssessReport(
            Words: Array.Empty<AssessedWord>(),
            OutcomeDigest: "irrelevant",
            SemanticDigest: "irrelevant",
            GrammarSourceSha256: "sha256:" + new string('c', 64),
            ModelFingerprint: "irrelevant",
            Pipeline: "foma-confirm",
            DiagnosticCount: 0);

        var figure = GrammarCoverageFigure.Compute(batch, corpus, report);

        Assert.Equal(report.GrammarSourceSha256, figure.GrammarSourceSha256);
    }
}
