using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Assess;

/// <summary>
/// The <c>Correctness</c>-kind fields <see cref="RegressionChecker"/> needs from an Assessment — never the
/// full stored row, so a caller in the store layer converts once rather than this type reaching upward for
/// one.
/// </summary>
public sealed record CorrectnessAssessment(
    string AssessmentId,
    string Assessor,
    string TokeniserName,
    string TokeniserVersion,
    StoredScope.Trial Scope,
    Selection Selection,
    string GrammarSourceSha256,
    IReadOnlyList<AssessedWord> Words);

/// <summary>
/// Whether applying would be a regression (ADR 0042 decision 5): coverage dropping, or a word that carried
/// an approved analysis no longer producing one. Both signals are computed from the same pair of
/// <c>Correctness</c> Assessments — the second is the sharper, word-level form of the first, not a second
/// measurement.
/// </summary>
public sealed record RegressionFinding(
    bool CoverageDropped,
    GrammarCoverageFigure PreviousCoverage,
    GrammarCoverageFigure CandidateCoverage,
    IReadOnlyList<WordChange> LostAnalyses)
{
    /// <summary>Either signal firing is enough to call this a regression.</summary>
    public bool IsRegression => CoverageDropped || LostAnalyses.Count > 0;

    /// <summary>One sentence naming what regressed, for a refusal message or an override's Decision comment.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CoverageDropped)
        {
            parts.Add(
                $"grammar coverage dropped from {PreviousCoverage.Fraction:P1} to {CandidateCoverage.Fraction:P1}");
        }
        if (LostAnalyses.Count > 0)
        {
            parts.Add($"{LostAnalyses.Count} word(s) that carried an approved analysis no longer produce one: " +
                string.Join(", ", LostAnalyses.Select(change => change.Word)));
        }
        return parts.Count == 0 ? "no regression detected" : string.Join("; ", parts);
    }
}

/// <summary>
/// Compares a candidate's <c>Correctness</c> Assessment against the project's previous current one
/// (ADR 0042 decision 5). Never gates by itself — a caller decides, from its own configuration, whether a
/// finding blocks <c>apply</c>.
/// </summary>
public static class RegressionChecker
{
    /// <summary>The stored kind both inputs must carry.</summary>
    public static readonly string RequiredKind = AssessmentKind.Correctness.ToStoredKind();

    /// <summary>
    /// <c>null</c> when there is nothing to regress against yet (no previous Assessment), or when the two
    /// are not comparable at all — not <c>Correctness</c>-kind, or made by different Assessors (ADR 0042
    /// decision 1) — since neither case is a regression finding one way or the other.
    /// </summary>
    public static RegressionFinding? Check(CorrectnessAssessment? previous, CorrectnessAssessment candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (previous is null) return null;

        AssessmentComparison comparison;
        try
        {
            comparison = AssessmentComparer.Compare(previous.ToComparable(), candidate.ToComparable());
        }
        catch (ComparisonRefusalException)
        {
            // Different Assessors: ADR 0042 decision 1 says these were never comparable in the first place.
            return null;
        }
        var lostAnalyses = comparison.Changes.Where(change => change.Kind == WordChangeKind.LostAnalysis).ToArray();

        var previousCoverage = CorrectnessCoverage.Compute(
            previous.Words, previous.Scope, previous.Selection, previous.GrammarSourceSha256, "regression");
        var candidateCoverage = CorrectnessCoverage.Compute(
            candidate.Words, candidate.Scope, candidate.Selection, candidate.GrammarSourceSha256, "regression");
        var coverageDropped = previousCoverage.Fraction is { } previousFraction &&
            candidateCoverage.Fraction is { } candidateFraction && candidateFraction < previousFraction;

        return new RegressionFinding(coverageDropped, previousCoverage, candidateCoverage, lostAnalyses);
    }
}
