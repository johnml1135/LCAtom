using System.Collections.Generic;

namespace SIL.Motif.Projection;

/// <summary>
/// A link to one word position carrying a manually approved analysis. The Segment GUID identifies the
/// durable container; the index is only a current-sequence navigation coordinate, never portable or
/// canonical identity.
/// </summary>
public sealed record AnalysisOccurrenceView(string SegmentGuid, int AnalysisIndex);

/// <summary>One manually approved analysis in the analysis aggregate report.</summary>
public sealed record ApprovedAnalysisView(
    string ContentDigest,
    string MorphBreakdown,
    IReadOnlyList<AnalysisOccurrenceView> Occurrences)
{
    /// <summary>How many project-text positions reference this analysis.</summary>
    public int OccurrenceCount => Occurrences.Count;
}

/// <summary>One automatic analysis recorded by an Assessment.</summary>
public sealed record AutomaticAnalysisView(string ContentDigest, string MorphBreakdown);

/// <summary>The aggregate reach figure for correctly-spelled word forms without manual analyses.</summary>
public sealed record UnanalysedReachView(int UnanalysedCount, int ParsedCount, string Statement);

/// <summary>One word form and its manual and automatic analyses in the analysis aggregate report.</summary>
public sealed record WordFormAnalysisView(
    string WordformGuid,
    string Form,
    IReadOnlyList<ApprovedAnalysisView> ManualAnalyses,
    IReadOnlyList<AutomaticAnalysisView>? AutomaticAnalyses = null)
{
    /// <summary>How many manually approved analyses the word form has.</summary>
    public int ManualAnalysisCount => ManualAnalyses.Count;

    /// <summary><c>null</c> when the Assessment did not cover the word; otherwise the automatic list's count.</summary>
    public int? AutomaticAnalysisCount => AutomaticAnalyses?.Count;
}

/// <summary>
/// The read-only analysis aggregate. Word forms without an approved analysis are omitted because the
/// unanalysed are reported only in aggregate.
/// </summary>
public sealed record AnalysisAggregateProjection(
    string AssessmentState,
    IReadOnlyList<WordFormAnalysisView> WordForms,
    UnanalysedReachView? UnanalysedReach = null)
{
    /// <summary>How many word forms have at least one manually approved analysis.</summary>
    public int WordFormCount => WordForms.Count;
}
