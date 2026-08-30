using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Assess;

/// <summary>
/// The four fields of an Assessment that decide whether, and how, it joins with another
/// (ADR 0042's amendment "comparability is a join on words, not containment of scopes"): which Assessor made
/// it, which kind it is, its tokeniser identity, and the per-word rows the join is computed over. Everything
/// else an Assessment carries — engine, limits, which corpus the words came from — is context a comparison
/// annotates with, never gates on.
/// </summary>
public sealed record ComparableAssessment(
    string AssessmentId,
    string Assessor,
    string Kind,
    string TokeniserName,
    string TokeniserVersion,
    IReadOnlyList<AssessedWord> Words);

/// <summary>Where a caller turns what it already has into the narrow join view above.</summary>
public static class ComparableAssessmentProjections
{
    /// <summary>
    /// A <c>Correctness</c> Assessment's comparable view. <see cref="CorrectnessAssessment"/> carries no Kind
    /// field of its own — being that type already says a measurement is <see cref="AssessmentKind.Correctness"/> —
    /// so this is the one place that fact becomes the string a join gates on.
    /// </summary>
    public static ComparableAssessment ToComparable(this CorrectnessAssessment assessment) => new(
        assessment.AssessmentId, assessment.Assessor, AssessmentKind.Correctness.ToStoredKind(),
        assessment.TokeniserName, assessment.TokeniserVersion, assessment.Words);
}

/// <summary>
/// How one shared word's behaviour differed between two Assessments. A word absent from either side, or
/// present in both with identical outcome and the identical set of produced analyses, is not a change and
/// never appears here.
/// </summary>
public enum WordChangeKind
{
    /// <summary>The word was not analysed on one side and was analysed on the other.</summary>
    GainedAnalysis,

    /// <summary>The word was analysed on one side and was not analysed on the other.</summary>
    LostAnalysis,

    /// <summary>Neither side analysed the word, but the recorded outcome differs (for example a timeout).</summary>
    OutcomeChanged,

    /// <summary>Both sides analysed the word, but the set of analyses produced differs.</summary>
    AnalysisChanged,
}

/// <summary>One shared word whose recorded behaviour differed between the two Assessments compared.</summary>
public sealed record WordChange(string Word, WordChangeKind Kind, string FromOutcome, string ToOutcome);

/// <summary>
/// Raised when two Assessments cannot be compared at all. Never raised for a differing word set or a
/// differing tokeniser — ADR 0042's amendment makes both of those context that annotates the comparison
/// rather than gates it.
/// </summary>
public sealed class ComparisonRefusalException : Exception
{
    public ComparisonRefusalException(string reason) : base(reason) { }
}

/// <summary>
/// The result of joining two Assessments on the word. <see cref="Changes"/> holds only the words that
/// differed; a shared word that behaved identically on both sides is counted in
/// <see cref="SharedWords"/> but never itemised.
/// </summary>
public sealed record AssessmentComparison(
    int FromWordCount,
    int ToWordCount,
    IReadOnlyList<string> SharedWords,
    bool TokeniserMismatch,
    string? TokeniserWarning,
    IReadOnlyList<WordChange> Changes);

/// <summary>
/// Joins two Assessments on the word (ADR 0042's amendment). What must match to compare at all is the
/// Assessor (decision 1: two Assessments compare only when they share one) and the kind (the amendment: kind
/// is part of an Assessment's identity and what a comparison matches on alongside the word). Everything
/// else — engine, limits, which corpus the words came from, and the tokeniser — is context: a differing word
/// set narrows the join silently, and a differing tokeniser narrows it while saying so.
/// </summary>
public static class AssessmentComparer
{
    /// <exception cref="ComparisonRefusalException">
    /// The two Assessments do not share an Assessor, or are not the same kind — never for a differing word
    /// set or tokeniser, which are context rather than a gate (pinned by `DifferentKinds_Refuses_NamingBoth`,
    /// `DifferentAssessors_Refuses_NamingBoth`).
    /// </exception>
    public static AssessmentComparison Compare(ComparableAssessment from, ComparableAssessment to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (!string.Equals(from.Assessor, to.Assessor, StringComparison.Ordinal))
        {
            throw new ComparisonRefusalException(
                $"'{from.AssessmentId}' was made by '{from.Assessor}' and '{to.AssessmentId}' by " +
                $"'{to.Assessor}'; two Assessments compare only when they share an Assessor.");
        }
        if (!string.Equals(from.Kind, to.Kind, StringComparison.Ordinal))
        {
            throw new ComparisonRefusalException(
                $"'{from.AssessmentId}' is a '{from.Kind}' measurement and '{to.AssessmentId}' is a " +
                $"'{to.Kind}' measurement; a comparison joins on kind as well as on word.");
        }

        var tokeniserMismatch = !string.Equals(from.TokeniserName, to.TokeniserName, StringComparison.Ordinal) ||
            !string.Equals(from.TokeniserVersion, to.TokeniserVersion, StringComparison.Ordinal);
        var warning = tokeniserMismatch
            ? $"'{from.AssessmentId}' was tokenised with {from.TokeniserName} {from.TokeniserVersion}; " +
              $"'{to.AssessmentId}' with {to.TokeniserName} {to.TokeniserVersion}. An under-matched join " +
              "looks exactly like a smaller corpus, so treat the word counts below with that in mind."
            : null;

        var fromByWord = from.Words.ToDictionary(word => word.Word, StringComparer.Ordinal);
        var toByWord = to.Words.ToDictionary(word => word.Word, StringComparer.Ordinal);
        var sharedWords = fromByWord.Keys.Where(toByWord.ContainsKey)
            .OrderBy(word => word, StringComparer.Ordinal).ToArray();

        var changes = new List<WordChange>();
        foreach (var word in sharedWords)
        {
            var change = Classify(word, fromByWord[word], toByWord[word]);
            if (change is not null) changes.Add(change);
        }

        return new AssessmentComparison(from.Words.Count, to.Words.Count, sharedWords, tokeniserMismatch, warning, changes);
    }

    private static WordChange? Classify(string word, AssessedWord from, AssessedWord to)
    {
        var fromAnalysed = string.Equals(from.Outcome, "analysed", StringComparison.Ordinal);
        var toAnalysed = string.Equals(to.Outcome, "analysed", StringComparison.Ordinal);

        if (fromAnalysed && !toAnalysed) return new WordChange(word, WordChangeKind.LostAnalysis, from.Outcome, to.Outcome);
        if (!fromAnalysed && toAnalysed) return new WordChange(word, WordChangeKind.GainedAnalysis, from.Outcome, to.Outcome);

        if (fromAnalysed) // both analysed: same digest scheme on both sides, since the Assessor is shared
        {
            var fromIdentities = from.Analyses.Select(a => a.IdentityDigest).ToHashSet(StringComparer.Ordinal);
            var toIdentities = to.Analyses.Select(a => a.IdentityDigest).ToHashSet(StringComparer.Ordinal);
            return fromIdentities.SetEquals(toIdentities)
                ? null
                : new WordChange(word, WordChangeKind.AnalysisChanged, from.Outcome, to.Outcome);
        }

        return string.Equals(from.Outcome, to.Outcome, StringComparison.Ordinal)
            ? null
            : new WordChange(word, WordChangeKind.OutcomeChanged, from.Outcome, to.Outcome);
    }
}
