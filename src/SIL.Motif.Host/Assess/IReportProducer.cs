namespace SIL.Motif.Host.Assess;

/// <summary>
/// The stored material a Report is computed from — an Assessment's own kind, scope and identity, plus the
/// words and analyses already recorded for it. Read back from the store rather than re-produced, so a
/// Report never needs the Assessor that made the Assessment to still be reachable.
/// </summary>
public sealed record ReportableAssessment(
    string AssessmentId,
    string Assessor,
    string Kind,
    string ScopeJson,
    string CorpusId,
    IReadOnlyList<string> CorpusWords,
    string CorpusSha256,
    string GrammarSourceSha256,
    IReadOnlyList<Parser.AssessedWord> Words);

/// <summary>The optional narrowing a caller of <c>motif report</c> may supply; unused by every kind so far.</summary>
public sealed record ReportQuery(string? Word = null, string? Text = null);

/// <summary>One Report's rendered, storable form — what <c>Reports.RenderedText</c> keeps.</summary>
public sealed record RenderedReport(string Kind, string Text);

/// <summary>
/// Raised when a report kind cannot be produced from the Assessment it was asked about — ADR 0042 decision
/// 4's *"this scope did not collect X"*, never a zero indistinguishable from a real one.
/// </summary>
public sealed class ReportRefusalException : Exception
{
    public ReportRefusalException(string kind, string reason) : base(reason) => Kind = kind;

    /// <summary>The report kind that was refused.</summary>
    public string Kind { get; }
}

/// <summary>
/// Produces one report kind from a <see cref="ReportableAssessment"/>. Registered into a
/// <see cref="ReportCatalog"/>, never dispatched by name in a caller's own switch — that is the whole
/// point of the registry (ADR 0042 decision 4's amendment on Reports).
/// </summary>
/// <remarks>
/// <see cref="Produce"/>'s <c>assessors</c> parameter exists so a kind that needs to ask the Assessor that
/// owns a raw format (for example reading a PanGloss stats cache) has a seam to do it through, without
/// every producer needing one. A kind that renders entirely from the Assessment's own stored rows — every
/// kind registered so far — never calls it.
/// </remarks>
public interface IReportProducer
{
    /// <summary>The registry key this kind is asked for under, and what <c>--list-kinds</c> names.</summary>
    string Kind { get; }

    /// <summary>One line describing what this kind reports, for <c>--list-kinds</c>.</summary>
    string Description { get; }

    /// <exception cref="ReportRefusalException">
    /// <paramref name="assessment"/> was not collected in a way this kind can report from.
    /// </exception>
    RenderedReport Produce(ReportableAssessment assessment, ReportQuery query, IAssessorCatalog assessors);
}
