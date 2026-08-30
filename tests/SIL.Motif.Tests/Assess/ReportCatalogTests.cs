using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// Covers the registry itself — resolving a registered kind, refusing an unregistered one, and listing
/// what is available — separately from what any one kind computes.
/// </summary>
public sealed class ReportCatalogTests
{
    [Fact]
    public void ListsEveryRegisteredKind()
    {
        var catalog = new ReportCatalog([new CoverageReportProducer(), new CorrectnessReportProducer()]);

        Assert.Equal(new[] { "correctness", "coverage" }, catalog.All.Select(p => p.Kind));
    }

    [Fact]
    public void ResolvesARegisteredKindByName()
    {
        var coverage = new CoverageReportProducer();
        var catalog = new ReportCatalog([coverage, new CorrectnessReportProducer()]);

        Assert.Same(coverage, catalog.Resolve("coverage"));
    }

    [Fact]
    public void RefusesAnUnknownKindNamingIt()
    {
        var catalog = new ReportCatalog([new CoverageReportProducer()]);

        var failure = Assert.Throws<KeyNotFoundException>(() => catalog.Resolve("nonexistent-kind"));

        Assert.Contains("nonexistent-kind", failure.Message, StringComparison.Ordinal);
        Assert.Contains("coverage", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTwoKindsRegisteredUnderTheSameName()
    {
        var duplicate = Assert.Throws<ArgumentException>(() => new ReportCatalog(
        [
            new CoverageReportProducer(),
            new CoverageReportProducer(),
        ]));

        Assert.Contains("coverage", duplicate.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Covers what each registered kind actually computes and the conditions that stop it, plus that neither
/// one reaches into the Assessor catalog it is handed — proven with one that has nothing registered, not
/// by omitting the parameter.
/// </summary>
public sealed class ReportProducerTests
{
    private const string ScopeJson = """{"words":[],"engine":"fast","collect":[],"perWordLimitMs":1000}""";
    private const string GrammarSha = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // "The Assessor may since be gone" simulated: nothing is registered, so a call to it would throw.
    private static readonly AssessorCatalog NoAssessorRegistered = new(Array.Empty<IAssessor>());

    [Fact]
    public void Coverage_RefusesAnAssessmentOfAnyOtherKindNamingTheReason()
    {
        var assessment = Build("Correctness", ("a", true));
        var producer = new CoverageReportProducer();

        var failure = Assert.Throws<ReportRefusalException>(
            () => producer.Produce(assessment, new ReportQuery(), NoAssessorRegistered));

        Assert.Equal("coverage", failure.Kind);
        Assert.Contains("Correctness", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ParseTime", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Correctness_RefusesAnAssessmentOfAnyOtherKindNamingTheReason()
    {
        var assessment = Build("ParseTime", ("a", true));
        var producer = new CorrectnessReportProducer();

        var failure = Assert.Throws<ReportRefusalException>(
            () => producer.Produce(assessment, new ReportQuery(), NoAssessorRegistered));

        Assert.Equal("correctness", failure.Kind);
        Assert.Contains("ParseTime", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Correctness", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_ComputesFromStoredWordsAndOutcomes_WithNoAssessorRegistered()
    {
        var assessment = Build("ParseTime", ("motifa", true), ("motifb", false));
        var producer = new CoverageReportProducer();

        var rendered = producer.Produce(assessment, new ReportQuery(), NoAssessorRegistered);

        Assert.Equal("coverage", rendered.Kind);
        Assert.Contains("50.0%", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Correctness_ComputesFromWhetherAWordCarriesAStoredAnalysis_WithNoAssessorRegistered()
    {
        var assessment = Build("Correctness", ("motifa", true), ("motifb", false), ("motifc", false));
        var producer = new CorrectnessReportProducer();

        var rendered = producer.Produce(assessment, new ReportQuery(), NoAssessorRegistered);

        Assert.Equal("correctness", rendered.Kind);
        Assert.Contains("33.3%", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_OverAnEmptyAdjudicatedSet_DoesNotOverclaimAPercentage()
    {
        var words = new[]
        {
            new AssessedWord("motifa", "timed-out", Array.Empty<ParsedAnalysis>()),
        };
        var corpus = Selection.Create("test", words.Select(w => w.Word));
        var assessment = new ReportableAssessment(
            "assessment/1", "pangloss", "ParseTime", ScopeJson,
            corpus.Name, corpus.Words, corpus.Sha256, GrammarSha, words);

        var rendered = new CoverageReportProducer().Produce(assessment, new ReportQuery(), NoAssessorRegistered);

        Assert.Contains("not computable", rendered.Text, StringComparison.Ordinal);
    }

    private static ReportableAssessment Build(string kind, params (string Word, bool Analysed)[] words)
    {
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word,
                w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest") }
                    : Array.Empty<ParsedAnalysis>()))
            .ToArray();
        var corpus = Selection.Create("test", words.Select(w => w.Word));
        return new ReportableAssessment(
            "assessment/1", "pangloss", kind, ScopeJson,
            corpus.Name, corpus.Words, corpus.Sha256, GrammarSha, assessedWords);
    }
}
