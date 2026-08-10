namespace SIL.Motif.Host.Analysis;

/// <summary>
/// Everything Motif can say about one word form without parsing: the human-approved test suite it
/// carries, and — only if an Assessment is already on record — what the parser made of it. The unit
/// <c>MOT-23</c> (<c>docs/plan-motif.md</c>) and ADR 0038 both key on, keyed by the one durable identity a
/// word form has: its own GUID, never a <c>WfiAnalysis</c> GUID.
/// </summary>
/// <param name="WordformGuid">
/// The <c>WfiWordform</c>'s own GUID, as text. Stable across breakdown edits even though the analyses
/// hanging off it are not (ADR 0038 decision 3) — this is why the whole record is keyed here rather than
/// on any analysis.
/// </param>
/// <param name="Form">
/// The wordform's surface text in the project's default vernacular writing system, for a human or a link
/// to read — never part of any identity comparison. ADR 0038 decision 3's standing caveat applies: a
/// corrected spelling repoints occurrences at a <i>different</i> word form and may delete this one, so
/// watching <c>Form</c> across two responses would miss the ordinary case. Comparisons in this codebase
/// key on <see cref="WordformGuid"/> for exactly that reason.
/// </param>
/// <param name="ManualAnalyses">
/// The set of human-approved analyses — ADR 0038 decision 2's "durable test". A <i>set</i>, deliberately:
/// a genuinely ambiguous word form carries more than one correct reading, and nothing here collapses them
/// to "the" analysis. Empty means no human has approved anything for this word form — decision 7's "no
/// expectation", not a failing test.
/// </param>
/// <param name="AutomaticAnalyses">
/// The parser's analyses for this word form, resolved from a <see cref="StoredAssessment"/> the caller
/// already had — reading this record <b>never runs the parser itself</b> (ADR 0038 decision 5). Three
/// states, not two: <c>null</c> means either no Assessment is on record at all, or one is on record but
/// did not cover this word form (so nothing is known either way); an empty list means the Assessment
/// covered this word and the parser produced nothing. Collapsing those two would silently misreport a
/// word nobody asked the parser about as a word the parser failed on.
/// </param>
public sealed record WordFormAnalysisAggregate(
    string WordformGuid,
    string Form,
    IReadOnlyList<ApprovedAnalysis> ManualAnalyses,
    IReadOnlyList<AutomaticAnalysis>? AutomaticAnalyses)
{
    /// <summary>
    /// How many analyses the parser produced for this word form — <b>the aggregate over-generation
    /// signal</b> named in <c>docs/plan-motif.md</c> MOT-23. <c>null</c> when nothing is known (see
    /// <see cref="AutomaticAnalyses"/>); otherwise the count, including zero.
    /// </summary>
    /// <remarks>
    /// A rising count is reported, not judged. ADR 0038 decision 6 forbids attributing cause, and the same
    /// caution applies to magnitude: several analyses for one word form is exactly what genuine ambiguity
    /// looks like, and it is also exactly what an over-permissive grammar looks like. This property answers
    /// "how many", and nothing in this codebase computes a verdict from it.
    /// </remarks>
    public int? AutomaticAnalysisCount => AutomaticAnalyses?.Count;
}
