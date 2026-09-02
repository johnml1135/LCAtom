using System.Linq;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// Covers CONTEXT.md's Readiness rule directly, with hand-built <see cref="CorrectnessAssessment"/> values
/// and no project or database: no candidate; a stale baseline; a regression with the gate on and off; and
/// the all-clear case.
/// </summary>
public sealed class ReadinessTests
{
    private const string GrammarSha = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void NoCandidate_GivesTheSingleReasonAndStops()
    {
        var reasons = Readiness.Assess(
            candidate: null, current: null, currentBaselineToken: null,
            candidateBaselineToken: "baseline-1", gateOnRegression: true);

        var reason = Assert.Single(reasons);
        Assert.Contains("no Assessment covers its current content", reason);
    }

    [Fact]
    public void DifferentBaselineTokens_AreStale()
    {
        var current = Build("current", ("alpha", true));
        var candidate = Build("candidate", ("alpha", true));

        var reasons = Readiness.Assess(
            candidate, current, currentBaselineToken: "baseline-old",
            candidateBaselineToken: "baseline-new", gateOnRegression: true);

        Assert.Contains(reasons, reason => reason.Contains("not been re-run since the project moved"));
    }

    [Fact]
    public void RegressionWithGateOn_IsAReason()
    {
        var current = Build("current", ("alpha", true), ("beta", true));
        var candidate = Build("candidate", ("alpha", false), ("beta", true));

        var reasons = Readiness.Assess(
            candidate, current, currentBaselineToken: "baseline-1",
            candidateBaselineToken: "baseline-1", gateOnRegression: true);

        Assert.Contains(reasons, reason => reason.Contains("would be a regression"));
    }

    [Fact]
    public void RegressionWithGateOff_IsNotAReason()
    {
        var current = Build("current", ("alpha", true), ("beta", true));
        var candidate = Build("candidate", ("alpha", false), ("beta", true));

        var reasons = Readiness.Assess(
            candidate, current, currentBaselineToken: "baseline-1",
            candidateBaselineToken: "baseline-1", gateOnRegression: false);

        Assert.Empty(reasons);
    }

    [Fact]
    public void SameBaseline_NoRegression_IsAllClear()
    {
        var current = Build("current", ("alpha", true), ("beta", true));
        var candidate = Build("candidate", ("alpha", true), ("beta", true));

        var reasons = Readiness.Assess(
            candidate, current, currentBaselineToken: "baseline-1",
            candidateBaselineToken: "baseline-1", gateOnRegression: true);

        Assert.Empty(reasons);
    }

    private static CorrectnessAssessment Build(string assessmentId, params (string Word, bool Analysed)[] words)
    {
        var selection = Selection.Create("test", words.Select(w => w.Word));
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word, w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, System.Array.Empty<string>(), 0, "digest:" + w.Word) }
                    : System.Array.Empty<ParsedAnalysis>()))
            .ToArray();
        return new CorrectnessAssessment(
            assessmentId, "pangloss", "none", "1",
            new StoredScope.Trial("all", Array.Empty<string>(), "fast", Array.Empty<AssessmentKind>(), TimeSpan.FromSeconds(1)),
            selection, GrammarSha, assessedWords);
    }
}
