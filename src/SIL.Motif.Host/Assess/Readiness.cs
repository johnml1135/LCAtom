namespace SIL.Motif.Host.Assess;

/// <summary>
/// Whether a Proposal has been measured well enough to apply (CONTEXT.md's Readiness): an Assessment
/// covers its current content, that Assessment measured the project state the apply would land on, and
/// applying it would show no regression. Computed from evidence, never granted by a caller — every caller
/// asks the same question, whether that is <c>apply</c> deciding to refuse or a future reader just showing
/// the answer.
/// </summary>
public static class Readiness
{
    /// <summary>
    /// The reasons a Proposal is not ready to apply, in the same wording a refusal message quotes verbatim.
    /// Empty means ready. <paramref name="candidate"/> is the Assessment covering the content that would be
    /// applied, or <c>null</c> if none exists. <paramref name="current"/> is the project's current
    /// Assessment, already narrowed to <c>null</c> by the caller unless it is of
    /// <see cref="RegressionChecker.RequiredKind"/> — only that kind is comparable for a regression.
    /// <paramref name="currentBaselineToken"/> is the current Assessment's Baseline token regardless of its
    /// kind (or <c>null</c> if there is no current Assessment at all), since staleness is judged on any
    /// current Assessment, not only a <c>Correctness</c> one.
    /// </summary>
    public static IReadOnlyList<string> Assess(
        CorrectnessAssessment? candidate,
        CorrectnessAssessment? current,
        string? currentBaselineToken,
        string candidateBaselineToken,
        bool gateOnRegression)
    {
        if (candidate is null)
            return new[] { "no Assessment covers its current content, so nothing has measured what it would do" };

        var reasons = new List<string>();

        // Two Assessments only mean anything against each other when they measured the same project state.
        if (currentBaselineToken is not null &&
            !string.Equals(currentBaselineToken, candidateBaselineToken, StringComparison.Ordinal))
        {
            reasons.Add(
                "its Assessment was measured against a different project state than the current one, so it has " +
                "not been re-run since the project moved");
        }

        if (gateOnRegression && current is not null)
        {
            var finding = RegressionChecker.Check(current, candidate);
            if (finding is { IsRegression: true }) reasons.Add($"it would be a regression: {finding.Describe()}");
        }

        return reasons;
    }
}
