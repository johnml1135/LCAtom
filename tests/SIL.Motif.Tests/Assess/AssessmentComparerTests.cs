using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// Covers the pure join logic (ADR 0042's amendment "comparability is a join on words, not containment of
/// scopes"): what gates a comparison, what is merely context that annotates it, and which shared words end
/// up itemised as a change.
/// </summary>
public sealed class AssessmentComparerTests
{
    [Fact]
    public void DifferentWordSets_CompareOnTheIntersection_AndReportEachSidesCount()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1",
            ("alpha", true), ("beta", true), ("gamma", true));
        var to = Build("assessment/to", "pangloss", "ParseTime", "none", "1",
            ("beta", true), ("gamma", true), ("delta", true));

        var comparison = AssessmentComparer.Compare(from, to);

        Assert.Equal(3, comparison.FromWordCount);
        Assert.Equal(3, comparison.ToWordCount);
        Assert.Equal(new[] { "beta", "gamma" }, comparison.SharedWords);
    }

    [Fact]
    public void DifferingTokenisers_StillCompare_AndCarryTheWarning()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "whitespace", "1", ("alpha", true));
        var to = Build("assessment/to", "pangloss", "ParseTime", "icu", "74", ("alpha", true));

        var comparison = AssessmentComparer.Compare(from, to);

        Assert.True(comparison.TokeniserMismatch);
        Assert.Contains("whitespace", comparison.TokeniserWarning, StringComparison.Ordinal);
        Assert.Contains("icu", comparison.TokeniserWarning, StringComparison.Ordinal);
        Assert.Single(comparison.SharedWords);
    }

    [Fact]
    public void SameTokeniser_CarriesNoWarning()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1", ("alpha", true));
        var to = Build("assessment/to", "pangloss", "ParseTime", "none", "1", ("alpha", true));

        var comparison = AssessmentComparer.Compare(from, to);

        Assert.False(comparison.TokeniserMismatch);
        Assert.Null(comparison.TokeniserWarning);
    }

    [Fact]
    public void DifferentKinds_Refuses_NamingBoth()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1", ("alpha", true));
        var to = Build("assessment/to", "pangloss", "Correctness", "none", "1", ("alpha", true));

        var failure = Assert.Throws<ComparisonRefusalException>(() => AssessmentComparer.Compare(from, to));

        Assert.Contains("ParseTime", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Correctness", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentAssessors_Refuses_NamingBoth()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1", ("alpha", true));
        var to = Build("assessment/to", "hermit-crab", "ParseTime", "none", "1", ("alpha", true));

        var failure = Assert.Throws<ComparisonRefusalException>(() => AssessmentComparer.Compare(from, to));

        Assert.Contains("pangloss", failure.Message, StringComparison.Ordinal);
        Assert.Contains("hermit-crab", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWordThatParsedInOneAndNotTheOther_AppearsInTheDifference()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1", ("alpha", true));
        var to = Build("assessment/to", "pangloss", "ParseTime", "none", "1", ("alpha", false));

        var comparison = AssessmentComparer.Compare(from, to);

        var change = Assert.Single(comparison.Changes);
        Assert.Equal("alpha", change.Word);
        Assert.Equal(WordChangeKind.LostAnalysis, change.Kind);
        Assert.Equal("analysed", change.FromOutcome);
        Assert.Equal("no-analysis", change.ToOutcome);
    }

    [Fact]
    public void AWordThatBehavedIdentically_DoesNotAppearInTheDifference()
    {
        var from = Build("assessment/from", "pangloss", "ParseTime", "none", "1", ("alpha", true), ("beta", false));
        var to = Build("assessment/to", "pangloss", "ParseTime", "none", "1", ("alpha", true), ("beta", false));

        var comparison = AssessmentComparer.Compare(from, to);

        Assert.Empty(comparison.Changes);
        Assert.Equal(2, comparison.SharedWords.Count);
    }

    [Fact]
    public void BothSidesAnalysed_ButWithADifferentAnalysis_IsReportedAsAnalysisChanged()
    {
        var from = new ComparableAssessment("assessment/from", "pangloss", "ParseTime", "none", "1",
            new[] { new AssessedWord("alpha", "analysed", new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest-1") }) });
        var to = new ComparableAssessment("assessment/to", "pangloss", "ParseTime", "none", "1",
            new[] { new AssessedWord("alpha", "analysed", new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest-2") }) });

        var comparison = AssessmentComparer.Compare(from, to);

        var change = Assert.Single(comparison.Changes);
        Assert.Equal(WordChangeKind.AnalysisChanged, change.Kind);
    }

    private static ComparableAssessment Build(string assessmentId, string assessor, string kind,
        string tokeniserName, string tokeniserVersion, params (string Word, bool Analysed)[] words)
    {
        var assessedWords = words.Select(w => new AssessedWord(
            w.Word, w.Analysed ? "analysed" : "no-analysis",
            w.Analysed
                ? new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest") }
                : Array.Empty<ParsedAnalysis>()));
        return new ComparableAssessment(assessmentId, assessor, kind, tokeniserName, tokeniserVersion, assessedWords.ToArray());
    }
}
