using SIL.Motif.Generator.Tsv;

namespace SIL.Motif.Generator.Ordering;

/// <summary>
/// Reads and writes <c>manifest/ordering-evidence.tsv</c> — one row per in-scope field that claims order
/// carries meaning, with whatever the model says about it.
/// </summary>
public static class OrderingEvidenceTsv
{
    public static readonly string[] Header =
        ["Class", "Field", "Card", "ComparisonClass", "Statement", "MatchedTerms", "Source", "SourceDetail", "SourceHash"];

    public static void Write(string path, IEnumerable<OrderingEvidence> rows) =>
        QuotedTsv.Write(path, Header, rows.Select(r => new[]
        {
            r.Class, r.Field, r.Card, r.ComparisonClass, r.Statement, r.MatchedTerms, r.Source, r.SourceDetail,
            r.SourceHash,
        }));

    public static IReadOnlyList<OrderingEvidence> Read(string path) =>
        QuotedTsv.Read(path, Header)
            .Select(c => new OrderingEvidence(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8]))
            .ToList();
}
