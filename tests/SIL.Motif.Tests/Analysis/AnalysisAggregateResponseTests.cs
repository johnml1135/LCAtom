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
    private static string Hash(char digit) => "sha256:" + new string(digit, 64);

    [Fact]
    public void AssessmentAndUnanalysedReachMustEitherBothBePresentOrBothBeAbsent()
    {
        var provenance = new AnalysisAssessmentProvenance("corpus", Hash('a'), Hash('b'));
        var reach = new UnanalysedReachFigure(1, 0);

        Assert.Throws<ArgumentException>(() => new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(), provenance));
        Assert.Throws<ArgumentException>(() => new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(), Assessment: null, reach));
    }

    [Fact]
    public void AutomaticAnalysesRequireAssessmentProvenance()
    {
        var wordform = new WordFormAnalysisAggregate(
            "wordform-guid-1",
            "mbali",
            Array.Empty<ApprovedAnalysis>(),
            Array.Empty<AutomaticAnalysis>());

        Assert.Throws<ArgumentException>(() => new AnalysisAggregateResponse(
            new[] { wordform },
            Assessment: null));
    }

    [Fact]
    public void AssessmentMayLeaveUncoveredWordformsWithoutAutomaticAnalyses()
    {
        var wordform = new WordFormAnalysisAggregate(
            "wordform-guid-1",
            "mbali",
            Array.Empty<ApprovedAnalysis>(),
            AutomaticAnalyses: null);

        var response = new AnalysisAggregateResponse(
            new[] { wordform },
            new AnalysisAssessmentProvenance("corpus", Hash('a'), Hash('b')),
            new UnanalysedReachFigure(0, 0));

        Assert.Null(response.WordForms[0].AutomaticAnalyses);
    }

    [Fact]
    public void NoAssessmentOnRecord_StillReturnsTheManualSide_AndSaysNothingIsOnRecord()
    {
        var wordform = new WordFormAnalysisAggregate(
            "w1", "mbali",
            new[] { new ApprovedAnalysis("d1", "root", Array.Empty<AnalysisOccurrenceLink>()) },
            AutomaticAnalyses: null);
        var response = new AnalysisAggregateResponse(new[] { wordform }, Assessment: null);

        Assert.False(response.HasAssessment);
        Assert.False(response.IsCurrent(Hash('a'), Hash('b')));

        // The manual side — the test suite — is untouched and needs no parser at all.
        Assert.Single(response.WordForms);
        Assert.Single(response.WordForms[0].ManualAnalyses);

        var description = response.DescribeAssessmentState(Hash('a'), Hash('b'));
        Assert.Contains("No assessment is on record", description);
    }

    [Fact]
    public void EveryRenderingNamesTheCorpusAndTheGrammar_WhenAnAssessmentExists()
    {
        var response = new AnalysisAggregateResponse(
            Array.Empty<WordFormAnalysisAggregate>(),
            new AnalysisAssessmentProvenance("seh-wikipedia", Hash('a'), Hash('c')),
            new UnanalysedReachFigure(0, 0));

        foreach (var sentence in new[]
                 {
                     response.DescribeAssessmentState(Hash('a'), Hash('c')), // current
                     response.DescribeAssessmentState(Hash('9'), Hash('c')), // corpus moved
                     response.DescribeAssessmentState(Hash('a'), Hash('8')), // grammar moved
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
            new AnalysisAssessmentProvenance("seh-wikipedia", Hash('a'), Hash('c')),
            new UnanalysedReachFigure(0, 0));

        var current = response.DescribeAssessmentState(Hash('a'), Hash('c'));
        Assert.Contains("still describes the current project", current);
        Assert.DoesNotContain("no longer exists", current);

        var grammarMoved = response.DescribeAssessmentState(Hash('a'), Hash('8'));
        Assert.StartsWith("As of the assessment", grammarMoved);
        Assert.Contains("the grammar has changed", grammarMoved);
        Assert.Contains("888888888888", grammarMoved); // names what it moved to
        Assert.Contains("no longer exists", grammarMoved);
        Assert.DoesNotContain("the corpus has changed", grammarMoved);

        var bothMoved = response.DescribeAssessmentState(Hash('9'), Hash('8'));
        Assert.Contains("the corpus has changed", bothMoved);
        Assert.Contains("the grammar has changed", bothMoved);
    }
}
