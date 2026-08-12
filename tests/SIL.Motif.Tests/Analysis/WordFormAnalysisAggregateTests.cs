using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// <see cref="WordFormAnalysisAggregate"/> — the per-word-form record ADR 0038 decision 2 requires to be
/// a set of analyses, never "the analysis", plus the three-state automatic side ADR 0038 decision 5 needs.
/// </summary>
/// <remarks>
/// Rules defended here, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item>Several approved analyses on one word form are all represented, never collapsed to one — a
/// genuinely ambiguous word form has more than one correct reading, and losing one silently stops
/// checking it.</item>
/// <item>"No assessment known" (<c>null</c>), "assessment covered this word, parser found nothing"
/// (empty list), and "the parser found N analyses" are three distinct states, not two — collapsing the
/// first two would misreport a word nobody asked the parser about as a parser failure.</item>
/// <item><see cref="WordFormAnalysisAggregate.AutomaticAnalysisCount"/> — the over-generation signal —
/// reports a plain count with no verdict attached.</item>
/// </list>
/// </remarks>
public class WordFormAnalysisAggregateTests
{
    [Fact]
    public void TwoApprovedAnalysesOnOneWordform_AreBothRepresented_NotCollapsedToOne()
    {
        var first = new ApprovedAnalysis("digest-1", "root-SFX", OccurrenceCount: 3);
        var second = new ApprovedAnalysis("digest-2", "root-PFX", OccurrenceCount: 1);

        var wordform = new WordFormAnalysisAggregate(
            "wordform-guid-1", "mbali", new[] { first, second }, AutomaticAnalyses: null);

        // The unit is a set (ADR 0038 decision 2): both ambiguous readings survive, at their own digest.
        Assert.Equal(2, wordform.ManualAnalyses.Count);
        Assert.Contains(first, wordform.ManualAnalyses);
        Assert.Contains(second, wordform.ManualAnalyses);
        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void AutomaticAnalysisCount_IsNull_WhenNothingIsKnownAboutTheParserSide()
    {
        var wordform = new WordFormAnalysisAggregate(
            "w1", "mbali", Array.Empty<ApprovedAnalysis>(), AutomaticAnalyses: null);

        Assert.Null(wordform.AutomaticAnalysisCount);
    }

    [Fact]
    public void AutomaticAnalysisCount_IsZero_NotNull_WhenTheAssessmentCoveredTheWordAndFoundNothing()
    {
        // "Covered, found nothing" vs "not covered": distinguished by null vs empty, not "no analyses".
        var wordform = new WordFormAnalysisAggregate(
            "w1", "mbali", Array.Empty<ApprovedAnalysis>(), AutomaticAnalyses: Array.Empty<AutomaticAnalysis>());

        Assert.NotNull(wordform.AutomaticAnalysisCount);
        Assert.Equal(0, wordform.AutomaticAnalysisCount);
    }

    [Fact]
    public void AutomaticAnalysisCount_ReflectsHowManyTheParserProduced()
    {
        var many = new[]
        {
            new AutomaticAnalysis("d1", "a"),
            new AutomaticAnalysis("d2", "b"),
            new AutomaticAnalysis("d3", "c"),
        };
        var wordform = new WordFormAnalysisAggregate("w1", "mbali", Array.Empty<ApprovedAnalysis>(), many);

        // A plain count, not a verdict (ADR 0038 decision 6): ambiguity vs looseness is unsaid.
        Assert.Equal(3, wordform.AutomaticAnalysisCount);
    }
}
