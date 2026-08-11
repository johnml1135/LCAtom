namespace SIL.Motif.Host.Analysis;

/// <summary>
/// Which Assessment, if any, the automatic side of an <see cref="AnalysisAggregateResponse"/> came from —
/// the naming <c>GrammarCoverageFigure</c> requires of every figure it renders, carried here instead so
/// this response can honour the same rule (ADR 0038 decision 8).
/// </summary>
public sealed record AnalysisAssessmentProvenance(string CorpusId, string CorpusSha256, string GrammarSourceSha256);

/// <summary>
/// The whole answer, for a set of word forms: what has a human approved and what did the parser say, as
/// of whatever Assessment (if any) is on record. A Report in this codebase's sense (<c>CONTEXT.md</c>) —
/// advisory always, gates nothing, renders no verdict.
/// </summary>
/// <param name="WordForms">One entry per word form read. See <see cref="WordFormAnalysisAggregate"/>.</param>
/// <param name="Assessment">
/// <c>null</c> when no Assessment was on record at the time this response was built — in which case every
/// <see cref="WordFormAnalysisAggregate.AutomaticAnalyses"/> in <see cref="WordForms"/> is also
/// <c>null</c>, and the response is still useful: the manual side is the test suite and needs no parser at
/// all (ADR 0038 decision 5). When not <c>null</c>, names the corpus and grammar the automatic side
/// describes, exactly as <see cref="Parser.GrammarCoverageFigure"/> already requires for a coverage figure.
/// </param>
public sealed record AnalysisAggregateResponse(
    IReadOnlyList<WordFormAnalysisAggregate> WordForms,
    AnalysisAssessmentProvenance? Assessment)
{
    /// <summary>Whether an Assessment was on record at all, independent of whether it is still current.</summary>
    public bool HasAssessment => Assessment is not null;

    /// <summary>
    /// Whether the recorded Assessment (if any) still describes the current project. Always <c>false</c>
    /// when <see cref="Assessment"/> is <c>null</c> — nothing recorded cannot be "current"; call
    /// <see cref="DescribeAssessmentState"/> to render that case in its own words rather than as a stale one.
    /// </summary>
    public bool IsCurrent(string currentCorpusSha256, string currentGrammarSourceSha256) =>
        Assessment is not null
        && string.Equals(Assessment.CorpusSha256, currentCorpusSha256, StringComparison.Ordinal)
        && string.Equals(Assessment.GrammarSourceSha256, currentGrammarSourceSha256, StringComparison.Ordinal);

    /// <summary>
    /// The sentence a report prints about the automatic side. <b>The only way to render that state, and
    /// deliberately so</b> — no parameterless overload, following <c>GrammarCoverageFigure.Describe</c>'s
    /// exact discipline (ADR 0038 decision 8): a caller cannot state whether the automatic analyses are
    /// current without naming what current means, so this can never be rendered as a bare, undated claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three states, one method: no Assessment at all; a current Assessment; and a stale one. The stale
    /// case is stated in the <i>past tense, naming what moved</i> — never refused and never a present-tense
    /// number with a caveat attached, for the reason <c>GrammarCoverageFigure.Describe</c>'s own remarks
    /// give: a footnote is what gets dropped when a number is quoted, and a past-tense sentence cannot be
    /// paraphrased into a present-tense claim.
    /// </para>
    /// </remarks>
    public string DescribeAssessmentState(string currentCorpusSha256, string currentGrammarSourceSha256)
    {
        if (Assessment is null)
        {
            return "No assessment is on record for this project. The analyses above are the manually " +
                   "approved ones only — producing an assessment is a separate, slow, explicitly-invoked " +
                   "step, not part of reading this aggregate.";
        }

        var subject = $"corpus '{Assessment.CorpusId}' ({Short(Assessment.CorpusSha256)}) " +
                      $"under grammar {Short(Assessment.GrammarSourceSha256)}";

        if (IsCurrent(currentCorpusSha256, currentGrammarSourceSha256))
        {
            return $"The automatic analyses above are from the assessment over {subject}, which still " +
                   "describes the current project.";
        }

        var moved = new List<string>();
        if (!string.Equals(Assessment.CorpusSha256, currentCorpusSha256, StringComparison.Ordinal))
            moved.Add($"the corpus has changed (now {Short(currentCorpusSha256)})");
        if (!string.Equals(Assessment.GrammarSourceSha256, currentGrammarSourceSha256, StringComparison.Ordinal))
            moved.Add($"the grammar has changed (now {Short(currentGrammarSourceSha256)})");

        return $"As of the assessment over {subject}, that is what the parser said. Since then, " +
               $"{string.Join(" and ", moved)}, so this describes a state that no longer exists. Rerun " +
               "the assessment to measure the current one.";
    }

    /// <summary>A hash short enough to read in a sentence. The full value stays on the record.</summary>
    private static string Short(string sha256)
    {
        if (string.IsNullOrEmpty(sha256)) return "(unrecorded)";
        var body = sha256.StartsWith("sha256:", StringComparison.Ordinal) ? sha256[7..] : sha256;
        return body.Length <= 12 ? body : body[..12] + "...";
    }
}
