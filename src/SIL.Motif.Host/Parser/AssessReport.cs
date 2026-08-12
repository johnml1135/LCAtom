using System.Text.Json;

namespace SIL.Motif.Host.Parser;

/// <summary>One analysis the parser found, with every key resolved to the FieldWorks GUID it names.</summary>
/// <param name="CategoryGuid">The word's category, or null when the parser assigned none.</param>
/// <param name="MorphemeGuids">The morphemes in order — entries and rules, by GUID.</param>
/// <param name="RootIndex">Which morpheme is the root.</param>
/// <param name="IdentityDigest">The parser's own digest of this identity, for comparing across runs.</param>
public sealed record ParsedAnalysis(
    string? CategoryGuid,
    IReadOnlyList<string> MorphemeGuids,
    int RootIndex,
    string IdentityDigest);

/// <summary>One word and what the parser made of it.</summary>
public sealed record AssessedWord(string Word, string Outcome, IReadOnlyList<ParsedAnalysis> Analyses);

/// <summary>
/// A parsed assessment report, with the provenance a grammar coverage figure must carry.
/// </summary>
/// <param name="GrammarSourceSha256">
/// The parser's hash of the grammar source it read. This is the "grammar identity" half of
/// ADR 0032 §4's provenance requirement, and it
/// comes free — no need for Motif to hash anything itself.
/// </param>
/// <param name="DiagnosticCount">
/// How many warnings the parser raised. Non-zero is normal — Sena 3 produces 137 — but a grammar coverage figure
/// computed while these were ignored is not a figure about the grammar, so the count travels with the report.
/// </param>
public sealed record AssessReport(
    IReadOnlyList<AssessedWord> Words,
    string OutcomeDigest,
    string SemanticDigest,
    string GrammarSourceSha256,
    string ModelFingerprint,
    string Pipeline,
    int DiagnosticCount);

/// <summary>
/// Reads <c>pangloss assess --report</c> JSON, resolving its interned keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>The report interns its keys.</b> Each analysis identity carries integer indices, and a flat
/// <c>keyTable</c> holds the strings. For a report produced from a <c>.fwdata</c> project those strings are
/// <b>FieldWorks GUIDs</b> — which is the entire reason Motif reads projects rather than HermitCrab XML, whose
/// keys are synthetic and cannot be tied back to the entry or rule a Proposal edited.
/// </para>
/// <para>
/// An index outside the table throws rather than yielding a placeholder. A silently-dropped morpheme would
/// make an analysis look shorter than it is, and morph count is one of the things
/// ADR 0027 gates equality on — so a placeholder here
/// could make two different analyses compare equal.
/// </para>
/// </remarks>
public static class AssessReportParser
{
    public static AssessReport Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Required.", nameof(json));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var keyTable = ReadKeyTable(root);
        var words = new List<AssessedWord>();

        if (root.TryGetProperty("cases", out var cases))
        {
            foreach (var element in cases.EnumerateArray())
                words.Add(ReadCase(element, keyTable));
        }

        return new AssessReport(
            Words: words,
            OutcomeDigest: StringOrEmpty(root, "outcomeDigest"),
            SemanticDigest: StringOrEmpty(root, "semanticDigest"),
            GrammarSourceSha256: NestedStringOrEmpty(root, "provenance", "sourceSha256"),
            ModelFingerprint: NestedStringOrEmpty(root, "provenance", "modelFingerprint"),
            Pipeline: NestedStringOrEmpty(root, "execution", "pipeline"),
            DiagnosticCount: root.TryGetProperty("diagnostics", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.GetArrayLength()
                : 0);
    }

    private static IReadOnlyList<string> ReadKeyTable(JsonElement root)
    {
        if (!root.TryGetProperty("keyTable", out var table) || table.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "The assessment report has no keyTable, so its identity indices cannot be resolved to " +
                "FieldWorks GUIDs. Motif cannot correlate a parse result with a Proposal without them.");

        return table.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
    }

    private static AssessedWord ReadCase(JsonElement element, IReadOnlyList<string> keyTable)
    {
        var analyses = new List<ParsedAnalysis>();

        if (element.TryGetProperty("analyses", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var analysis in list.EnumerateArray())
            {
                var identity = analysis.GetProperty("identity");

                string? category = null;
                if (identity.TryGetProperty("category", out var categoryElement)
                    && categoryElement.ValueKind == JsonValueKind.Number)
                {
                    category = Resolve(categoryElement.GetInt32(), keyTable, "category");
                }

                var morphemes = new List<string>();
                if (identity.TryGetProperty("morphemes", out var morphemeList))
                {
                    foreach (var index in morphemeList.EnumerateArray())
                        morphemes.Add(Resolve(index.GetInt32(), keyTable, "morpheme"));
                }

                analyses.Add(new ParsedAnalysis(
                    CategoryGuid: category,
                    MorphemeGuids: morphemes,
                    RootIndex: identity.TryGetProperty("rootIndex", out var r) ? r.GetInt32() : -1,
                    IdentityDigest: StringOrEmpty(analysis, "identityDigest")));
            }
        }

        return new AssessedWord(
            Word: StringOrEmpty(element, "input"),
            Outcome: StringOrEmpty(element, "outcome"),
            Analyses: analyses);
    }

    private static string Resolve(int index, IReadOnlyList<string> keyTable, string what)
    {
        if (index < 0 || index >= keyTable.Count)
            throw new InvalidOperationException(
                $"An analysis references {what} key {index}, outside the report's {keyTable.Count}-entry " +
                "keyTable. Refusing rather than dropping it: a missing morpheme shortens the analysis, and " +
                "morph count is part of what decides whether two analyses are the same (ADR 0027).");

        return keyTable[index];
    }

    private static string StringOrEmpty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string NestedStringOrEmpty(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? StringOrEmpty(child, name)
            : string.Empty;
}
