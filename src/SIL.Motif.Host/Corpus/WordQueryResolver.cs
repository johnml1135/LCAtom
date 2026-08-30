using SIL.LCModel;
using SIL.Motif.Host.Config;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Resolves a scope's declared word query (<see cref="AssessmentScopeConfiguration.Query"/>) into the
/// concrete word list a Selection is — <c>CONTEXT.md</c>'s Selection, "listed out in full... not a query
/// and not a sample". The vocabulary is deliberately tiny: <see cref="AssessmentScopeConfiguration.DefaultQueryText"/>
/// and <see cref="AllWordformsQueryText"/> are the only two queries a project's configuration can express
/// today, and <see cref="Resolve"/> refuses anything else by name rather than guessing at it (pinned by
/// `AnUnrecognisedQuery_RefusesNamingIt`). A larger vocabulary is a later, deliberate addition, not a
/// fallback path threaded through here now.
/// </summary>
public static class WordQueryResolver
{
    /// <summary>Every wordform with a non-empty surface form, whether or not it carries a manual analysis.</summary>
    public const string AllWordformsQueryText = "all wordforms";

    /// <summary>Every query this resolver understands, for a refusal message naming what is actually available.</summary>
    public static readonly IReadOnlyList<string> KnownQueries =
        [AssessmentScopeConfiguration.DefaultQueryText, AllWordformsQueryText];

    /// <exception cref="InvalidOperationException"><paramref name="query"/> names no query this resolver understands.</exception>
    public static IReadOnlyList<string> Resolve(string query, LcmCache cache)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A query is required.", nameof(query));
        ArgumentNullException.ThrowIfNull(cache);

        if (string.Equals(query, AssessmentScopeConfiguration.DefaultQueryText, StringComparison.Ordinal))
            return WordsCarryingAManualAnalysis(cache);
        if (string.Equals(query, AllWordformsQueryText, StringComparison.Ordinal))
            return LcmWordformCorpus.ExtractForms(cache).ToList();

        throw new InvalidOperationException(
            $"'{query}' does not name a recognized Assessment scope query. Known queries: " +
            string.Join(", ", KnownQueries.Select(known => $"'{known}'")) + ".");
    }

    // HumanApprovedAnalyses is liblcm's own accessor for a human-decided analysis; none means no word here.
    private static IReadOnlyList<string> WordsCarryingAManualAnalysis(LcmCache cache)
    {
        var repository = cache.ServiceLocator.GetInstance<IWfiWordformRepository>();
        var words = new List<string>();
        foreach (var wordform in repository.AllInstances())
        {
            if (!wordform.HumanApprovedAnalyses.Any()) continue;
            var form = wordform.Form.VernacularDefaultWritingSystem?.Text;
            if (!string.IsNullOrEmpty(form)) words.Add(form);
        }
        return words;
    }
}
