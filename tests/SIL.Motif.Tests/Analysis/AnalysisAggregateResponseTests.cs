using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="AnalysisAggregateResponse"/> — the whole-response record, and specifically the two
/// obligations ADR 0038 decisions 5 and 8 place on it: a missing Assessment is a valid, useful answer on
/// its own, and a stale one is rendered by the type itself, in the past tense, never as a bare number.
/// </summary>
/// <remarks>
/// Modelled directly on <c>GrammarCoverageFigureTests</c>' rendering tests, which pin the same discipline
/// on <see cref="Parser.GrammarCoverageFigure"/> — this response reuses that idea rather than inventing a
/// second one, per ADR 0038 decision 8's own instruction.
/// <list type="number">
/// <item>No Assessment on record must not read as an error or an empty response — the manual side is the
/// test suite and stands on its own (ADR 0038 decision 5).</item>
/// <item><see cref="AnalysisAggregateResponse.DescribeAssessmentState"/> has no parameterless overload,
/// so a caller cannot state the automatic side's freshness without naming what "current" means.</item>
/// <item>A stale Assessment is described in the past tense, naming what moved — never suppressed and
/// never a present-tense number with a caveat that a reader could drop when quoting it.</item>
/// </list>
/// </remarks>
public class AnalysisAggregateResponseTests
{
    [Fact]
    public void NoAssessmentOnRecord_StillReturnsTheManualSide_AndSaysNothingIsOnRecord()
    {
        var wordform = new WordFormAnalysisAggregate(
            "w1", "mbali",
            new[] { new ApprovedAnalysis("d1", "root", Array.Empty<AnalysisOccurrenceLink>()) },
            AutomaticAnalyses: null);
        var response = new AnalysisAggregateResponse(new[] { wordform }, Assessment: null);

        Assert.False(response.HasAssessment);
        Assert.False(response.IsCurrent("sha256:aaa", "sha256:bbb"));

        // The manual side — the test suite — is untouched and needs no parser at all.
        Assert.Single(response.WordForms);
        Assert.Single(response.WordForms[0].ManualAnalyses);

        var description = response.DescribeAssessmentState("sha256:aaa", "sha256:bbb");
        Assert.Contains("No assessment is on record", description);
    }

    [Fact]
    public void EveryRenderingNamesTheCorpusAndTheGrammar_WhenAnAssessmentExists()
    {
        var response = new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(),
            new AnalysisAssessmentProvenance("seh-wikipedia", "sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"));

        foreach (var sentence in new[]
                 {
                     response.DescribeAssessmentState("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"), // current
                     response.DescribeAssessmentState("sha256:9999999999990000", "sha256:ccccccccccccdddd"), // corpus moved
                     response.DescribeAssessmentState("sha256:aaaaaaaaaaaabbbb", "sha256:8888888888887777"), // grammar moved
                 })
        {
            Assert.Contains("seh-wikipedia", sentence);
            Assert.Contains("aaaaaaaaaaaa", sentence);
            Assert.Contains("cccccccccccc", sentence);
        }
    }

    [Fact]
    public void AStaleAssessment_RendersInThePastTense_NamingWhatMoved()
    {
        var response = new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(),
            new AnalysisAssessmentProvenance("seh-wikipedia", "sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd"));

        var current = response.DescribeAssessmentState("sha256:aaaaaaaaaaaabbbb", "sha256:ccccccccccccdddd");
        Assert.Contains("still describes the current project", current);
        Assert.DoesNotContain("no longer exists", current);

        var grammarMoved = response.DescribeAssessmentState("sha256:aaaaaaaaaaaabbbb", "sha256:8888888888887777");
        Assert.StartsWith("As of the assessment", grammarMoved);
        Assert.Contains("the grammar has changed", grammarMoved);
        Assert.Contains("888888888888", grammarMoved); // names what it moved to
        Assert.Contains("no longer exists", grammarMoved);
        Assert.DoesNotContain("the corpus has changed", grammarMoved);

        var bothMoved = response.DescribeAssessmentState("sha256:9999999999990000", "sha256:8888888888887777");
        Assert.Contains("the corpus has changed", bothMoved);
        Assert.Contains("the grammar has changed", bothMoved);
    }
}
