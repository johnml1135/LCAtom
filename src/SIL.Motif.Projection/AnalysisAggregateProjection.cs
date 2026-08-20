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

/// <summary>One word form and its manually approved analyses in the analysis aggregate report.</summary>
public sealed record WordFormAnalysisView(
    string WordformGuid,
    string Form,
    IReadOnlyList<ApprovedAnalysisView> ManualAnalyses)
{
    /// <summary>How many manually approved analyses the word form has.</summary>
    public int ManualAnalysisCount => ManualAnalyses.Count;
}

/// <summary>
/// The read-only analysis aggregate when no Assessment is supplied: the manually approved analyses
/// already stored in the project and an explicit statement that automatic analyses are absent. Word
/// forms without an approved analysis are omitted because the unanalysed are reported only in aggregate.
/// </summary>
public sealed record AnalysisAggregateProjection(
    string AssessmentState,
    IReadOnlyList<WordFormAnalysisView> WordForms)
{
    /// <summary>How many word forms have at least one manually approved analysis.</summary>
    public int WordFormCount => WordForms.Count;
}
