using System.Text.RegularExpressions;

namespace SIL.Motif.Generator.Ordering;

/// <summary>
/// The words that make a sentence in <c>MasterLCModel.xml</c> a statement about <em>order</em> rather than
/// about the field generally. Deliberately a short, checked-in, auditable list rather than a cleverer test:
/// every selection records which of these it matched (<see cref="OrderingEvidence.MatchedTerms"/>), so a
/// reviewer can see why a sentence was pulled in and disagree with it specifically.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a filter, not a judgement.</b> It decides which sentences a human reads, and nothing else. A
/// sentence it selects may still argue that order does <em>not</em> matter — <c>PhSimpleContextNC.PlusConstr</c>
/// says "although this attr is defined as a collection seq (not an ordered seq)", which reads as a denial and
/// is in fact followed by the reason order is nonetheless stable. That is precisely the kind of sentence a
/// person must read rather than a keyword must score, so the harvest quotes it and stops there.
/// </para>
/// <para>
/// <b>Recall over precision.</b> A missed ordering statement leaves a row looking unevidenced when evidence
/// exists — the failure this whole exercise is trying to remove. A false positive costs a reviewer one
/// sentence of reading. So the list errs wide.
/// </para>
/// </remarks>
public static class OrderingVocabulary
{
    /// <summary>
    /// Matched case-insensitively, on word boundaries so <c>order</c> does not fire on <c>disorder</c> and
    /// <c>first</c> does not fire inside <c>firstly</c>-style compounds in other words.
    /// </summary>
    public static readonly IReadOnlyList<string> Terms =
    [
        "order", "ordered", "orders", "ordering", "unordered",
        "sequence", "sequential", "sequentially", "seq",
        "first", "last", "position", "positional",
        "precede", "precedes", "preceding", "follow", "follows", "following",
        "before", "after", "innermost", "outermost", "outward",
        "left-to-right", "right-to-left", "shallowest", "deepest",
        "applied", "apply", "rank", "index",
    ];

    private static readonly Regex Pattern = new(
        @"\b(" + string.Join("|", Terms.Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The distinct terms this sentence matched, lower-cased and in the order they appear.</summary>
    public static IReadOnlyList<string> Match(string sentence)
    {
        var seen = new List<string>();
        foreach (Match match in Pattern.Matches(sentence))
        {
            var term = match.Value.ToLowerInvariant();
            if (!seen.Contains(term)) seen.Add(term);
        }

        return seen;
    }
}
