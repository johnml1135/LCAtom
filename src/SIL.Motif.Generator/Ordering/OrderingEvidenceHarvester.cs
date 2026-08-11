using System.Text.RegularExpressions;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Ordering;

/// <summary>
/// Pulls whatever <c>MasterLCModel.xml</c> says about ordering onto every in-scope row whose
/// <c>ComparisonClass</c> claims order matters, so the claim can be reviewed against the model instead of
/// re-derived from a field name.
/// </summary>
/// <remarks>
/// The 2026-08-03 manifest trust audit recommended "100%-review the 61 non-default rows before the
/// generator ships from them" and called it a bounded, one-sitting task. It is — once someone has the
/// evidence in front of them. This is the part a machine can do: find the sentences, quote them, cite them,
/// digest them. What it deliberately does not do is decide whether they support the classification.
/// </remarks>
public static class OrderingEvidenceHarvester
{
    public const string SourceName = "liblcm/MasterLCModel.xml";

    /// <summary>
    /// Every in-scope row whose <c>ComparisonClass</c> is something other than <c>unordered</c> — i.e. every
    /// row that asserts order carries meaning, and therefore every row whose assertion is worth evidence.
    /// <c>n/a</c> rows are excluded: a non-authorable field has no diff to get wrong.
    /// </summary>
    public static IReadOnlyList<JoinedRow> RowsNeedingEvidence(IReadOnlyList<JoinedRow> rows) =>
        rows.Where(row =>
                row.Manifest.Scope == "in" &&
                row.Manifest.ComparisonClass is not ("" or "unordered" or "n/a"))
            .OrderBy(row => row.DeclaringClass, StringComparer.Ordinal)
            .ThenBy(row => row.FieldName, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<OrderingEvidence> Harvest(
        IReadOnlyList<JoinedRow> rows,
        IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> comments)
    {
        var harvested = new List<OrderingEvidence>();

        foreach (var row in RowsNeedingEvidence(rows))
        {
            var key = (row.DeclaringClass, row.FieldName);
            comments.TryGetValue(key, out var comment);

            var commentText = comment?.FullText ?? "";
            var (statement, terms) = SelectOrderingSentences(commentText);

            harvested.Add(new OrderingEvidence(
                Class: row.DeclaringClass,
                Field: row.FieldName,
                Card: row.Card?.ToString().ToLowerInvariant() ?? "",
                ComparisonClass: row.Manifest.ComparisonClass,
                Statement: statement,
                MatchedTerms: string.Join(" ", terms),
                Source: statement.Length > 0 ? SourceName : "",
                SourceDetail: statement.Length > 0 && comment is not null ? comment.Citation : "",
                SourceHash: statement.Length > 0 ? SourceDigest.OfText(statement) : ""));
        }

        return harvested;
    }

    /// <summary>
    /// The sentences of <paramref name="commentText"/> that say something about order, joined back together
    /// in their original order, plus every vocabulary term they matched.
    /// </summary>
    public static (string Statement, IReadOnlyList<string> Terms) SelectOrderingSentences(string commentText)
    {
        if (string.IsNullOrWhiteSpace(commentText)) return ("", []);

        var selected = new List<string>();
        var terms = new List<string>();

        foreach (var sentence in SplitSentences(commentText))
        {
            var matched = OrderingVocabulary.Match(sentence);
            if (matched.Count == 0) continue;

            selected.Add(sentence);
            foreach (var term in matched)
            {
                if (!terms.Contains(term)) terms.Add(term);
            }
        }

        return (string.Join(" ", selected), terms);
    }

    /// <summary>
    /// <c>i.e.</c>, <c>e.g.</c> and friends are everywhere in this file's prose ("i.e. those slots which
    /// correspond to enclitics"), and splitting on them would cut an ordering statement in half and quote
    /// the wrong fragment. They are protected before the split and restored after, which is cruder than a
    /// real sentence segmenter and entirely sufficient for one XML file's comments.
    /// </summary>
    private static readonly (string Abbreviation, string Placeholder)[] Abbreviations =
    [
        ("i.e.", "IE"),
        ("e.g.", "EG"),
        ("cf.", "CF"),
        ("etc.", "ETC"),
        ("vs.", "VS"),
    ];

    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    private static IEnumerable<string> SplitSentences(string text)
    {
        var protectedText = text;
        foreach (var (abbreviation, placeholder) in Abbreviations)
            protectedText = protectedText.Replace(abbreviation, placeholder, StringComparison.OrdinalIgnoreCase);

        foreach (var piece in SentenceBoundary.Split(protectedText))
        {
            var restored = piece;
            foreach (var (abbreviation, placeholder) in Abbreviations)
                restored = restored.Replace(placeholder, abbreviation, StringComparison.Ordinal);

            var trimmed = restored.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }
}
