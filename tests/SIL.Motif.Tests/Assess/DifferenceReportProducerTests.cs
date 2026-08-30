using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// Covers what <see cref="DifferenceReportProducer"/> renders from a <c>Difference</c> Assessment's own
/// stored rows — the same rendering <c>compare</c> prints at the moment it stores one, and what
/// <c>report --kind difference</c> would print reading the row back later.
/// </summary>
public sealed class DifferenceReportProducerTests
{
    private const string ScopeJson = """
        {"fromAssessmentId":"assessment/from","toAssessmentId":"assessment/to","fromWordCount":2,
         "toWordCount":2,"sharedWordCount":2,"tokeniserMismatch":false,"tokeniserWarning":null}
        """;

    private static readonly AssessorCatalog NoAssessorsRegistered = new(Array.Empty<IAssessor>());

    [Fact]
    public void RefusesAnAssessmentOfAnyOtherKind_NamingTheReason()
    {
        var corpus = CorpusDescriptor.Create("test", new[] { "alpha" });
        var assessment = new ReportableAssessment(
            "assessment/1", "pangloss", "ParseTime", ScopeJson,
            corpus.CorpusId, corpus.Words, corpus.Sha256, string.Empty, Array.Empty<AssessedWord>());

        var failure = Assert.Throws<ReportRefusalException>(
            () => new DifferenceReportProducer().Produce(assessment, new ReportQuery(), NoAssessorsRegistered));

        Assert.Equal("difference", failure.Kind);
        Assert.Contains("ParseTime", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Difference", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersTheStoredChanges_AndTheCounts_FromScopeJsonAlone()
    {
        var corpus = CorpusDescriptor.Create("difference:from..to", new[] { "alpha", "beta" });
        var words = new[]
        {
            new AssessedWord("alpha", "LostAnalysis:analysed->no-analysis", Array.Empty<ParsedAnalysis>()),
        };
        var assessment = new ReportableAssessment(
            "assessment/diff", "pangloss", "Difference", ScopeJson,
            corpus.CorpusId, corpus.Words, corpus.Sha256, string.Empty, words);

        var rendered = new DifferenceReportProducer().Produce(assessment, new ReportQuery(), NoAssessorsRegistered);

        Assert.Equal("difference", rendered.Kind);
        Assert.Contains("assessment/from", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("assessment/to", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("2 vs 2, 2 shared, 1 changed", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("alpha: LostAnalysis:analysed->no-analysis", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATokeniserMismatch_IsRenderedAsAWarning()
    {
        var corpus = CorpusDescriptor.Create("difference:from..to", new[] { "alpha" });
        const string mismatchedScope = """
            {"fromAssessmentId":"assessment/from","toAssessmentId":"assessment/to","fromWordCount":1,
             "toWordCount":1,"sharedWordCount":1,"tokeniserMismatch":true,
             "tokeniserWarning":"tokenisers differ"}
            """;
        var assessment = new ReportableAssessment(
            "assessment/diff", "pangloss", "Difference", mismatchedScope,
            corpus.CorpusId, corpus.Words, corpus.Sha256, string.Empty, Array.Empty<AssessedWord>());

        var rendered = new DifferenceReportProducer().Produce(assessment, new ReportQuery(), NoAssessorsRegistered);

        Assert.Contains("WARNING: tokenisers differ", rendered.Text, StringComparison.Ordinal);
    }
}
