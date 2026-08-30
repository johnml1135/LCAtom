using System.Linq;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// Covers what counts as a regression (ADR 0042 decision 5): coverage dropping, and an approved analysis no
/// longer being produced. Both signals come from a pair of <c>Correctness</c> Assessments, never from a bare
/// percentage.
/// </summary>
public sealed class RegressionCheckerTests
{
    private const string GrammarSha = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void NoPreviousAssessment_IsNotARegression()
    {
        var candidate = Build("candidate", ("alpha", true), ("beta", true));

        var finding = RegressionChecker.Check(null, candidate);

        Assert.Null(finding);
    }

    [Fact]
    public void AWordThatLosesItsApprovedAnalysis_IsARegression()
    {
        var previous = Build("previous", ("alpha", true), ("beta", true));
        var candidate = Build("candidate", ("alpha", false), ("beta", true));

        var finding = RegressionChecker.Check(previous, candidate);

        Assert.NotNull(finding);
        Assert.True(finding!.IsRegression);
        Assert.Single(finding.LostAnalyses);
        Assert.Equal("alpha", finding.LostAnalyses[0].Word);
        Assert.Contains("alpha", finding.Describe());
    }

    [Fact]
    public void CoverageDropping_WithNoWordLevelLoss_IsStillARegression()
    {
        // A fourth, failing word dilutes the fraction even though every shared word parses identically.
        var previous = Build("previous", ("alpha", true), ("beta", true), ("gamma", true));
        var candidate = Build("candidate", ("alpha", true), ("beta", true), ("gamma", true), ("delta", false));

        var finding = RegressionChecker.Check(previous, candidate);

        Assert.NotNull(finding);
        Assert.True(finding!.CoverageDropped);
        Assert.True(finding.IsRegression);
        Assert.Empty(finding.LostAnalyses);
        Assert.Contains("coverage dropped", finding.Describe());
    }

    [Fact]
    public void NothingWorse_IsNotARegression()
    {
        var previous = Build("previous", ("alpha", true), ("beta", false));
        var candidate = Build("candidate", ("alpha", true), ("beta", true));

        var finding = RegressionChecker.Check(previous, candidate);

        Assert.NotNull(finding);
        Assert.False(finding!.IsRegression);
    }

    [Fact]
    public void DifferentAssessors_AreNotComparable_AndYieldNoFinding()
    {
        var previous = Build("previous", assessor: "pangloss", words: [("alpha", true)]);
        var candidate = Build("candidate", assessor: "hermit-crab", words: [("alpha", false)]);

        var finding = RegressionChecker.Check(previous, candidate);

        Assert.Null(finding);
    }

    private static CorrectnessAssessment Build(
        string assessmentId, params (string Word, bool Analysed)[] words) =>
        Build(assessmentId, "pangloss", words);

    private static CorrectnessAssessment Build(
        string assessmentId, string assessor, (string Word, bool Analysed)[] words)
    {
        var corpus = CorpusDescriptor.Create("test", words.Select(w => w.Word));
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word, w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, System.Array.Empty<string>(), 0, "digest:" + w.Word) }
                    : System.Array.Empty<ParsedAnalysis>()))
            .ToArray();
        return new CorrectnessAssessment(
            assessmentId, assessor, "none", "1",
            new StoredScope.Trial("all", Array.Empty<string>(), "fast", Array.Empty<AssessmentKind>(), TimeSpan.FromSeconds(1)),
            corpus, GrammarSha, assessedWords);
    }
}
