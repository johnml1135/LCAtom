using System.Text.RegularExpressions;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// Why the FST path declined to build a grammar, when it did. Recognising this is what lets Motif fall back to
/// HermitCrab instead of reporting "the parser failed" and losing the grammar.
/// </summary>
/// <param name="Kind">The recognised category.</param>
/// <param name="Detail">The parser's own words, kept verbatim — it names the budget and the limit.</param>
public sealed record ParserRefusal(ParserRefusalKind Kind, string Detail);

/// <summary>Categories of FST-build refusal, distinguished because only some of them are worth retrying.</summary>
public enum ParserRefusalKind
{
    /// <summary>
    /// The grammar's morphotactics produce more composite material than the FST enumerator will expand.
    /// Observed on a real project: <i>"composite lexc entries (fusion + interdigitation + structural) = 200500
    /// (limit 200000)"</i>. <b>Falling back to HermitCrab is the correct response</b> — the parser's own error
    /// text says so. Raising the budget is possible but is a decision about memory, not a retry.
    /// </summary>
    FstEnumerationBudgetExceeded,

    /// <summary>The FST path refused for a reason this type does not recognise. Fall back, and surface the text.</summary>
    FstBuildRefused,
}

/// <summary>
/// Recognises FST-build refusals in the parser's stderr.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching on message text is fragile, and it is still the right call here.</b> The alternative is
/// treating every non-zero exit as "unavailable", which would silently downgrade a grammar Motif could have
/// parsed with the fallback engine, and would make the two failures — <i>this grammar is too big for the FST</i>
/// and <i>the parser is broken or missing</i> — indistinguishable. Those need different responses.
/// </para>
/// <para>
/// So the fragility is bounded deliberately: an unrecognised refusal still yields
/// <see cref="ParserRefusalKind.FstBuildRefused"/> and still triggers the fallback, carrying the parser's own
/// words. A message change costs the specific diagnosis, never the fallback. If PanGloss ever gives the
/// refusal a stable machine-readable code, this class should read that instead and the pattern below should be
/// deleted rather than kept as a belt.
/// </para>
/// </remarks>
public static class ParserRefusalRecognizer
{
    // Anchored on PanGloss's stable noun phrases, not the whole sentence, which is more likely to change.
    private static readonly Regex BudgetExceeded = new(
        @"exceeds the foma-engine's eager-enumeration budget|composite lexc entries.*\(limit \d+\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FomaCompileFailed = new(
        @"foma compile failed", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the refusal if <paramref name="stdErr"/> describes one, or <c>null</c> when the failure is
    /// something else — a missing executable, a bad path, a crash — which must not be mistaken for a grammar
    /// that merely needs the other engine.
    /// </summary>
    public static ParserRefusal? Recognize(string stdErr)
    {
        if (string.IsNullOrWhiteSpace(stdErr)) return null;

        if (BudgetExceeded.IsMatch(stdErr))
            return new ParserRefusal(ParserRefusalKind.FstEnumerationBudgetExceeded, FirstRelevantLine(stdErr));

        if (FomaCompileFailed.IsMatch(stdErr))
            return new ParserRefusal(ParserRefusalKind.FstBuildRefused, FirstRelevantLine(stdErr));

        return null;
    }

    /// <summary>The refusal line only; buried among other notes, a diagnostic stops being read.</summary>
    private static string FirstRelevantLine(string stdErr)
    {
        var lines = stdErr.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (BudgetExceeded.IsMatch(trimmed) || FomaCompileFailed.IsMatch(trimmed))
                return trimmed;
        }

        return stdErr.Trim();
    }
}
