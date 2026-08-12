namespace SIL.Motif.Generator.Descriptions;

/// <summary>
/// Parses <c>manifest/kind-descriptions.tsv</c>: eight columns, tab-separated, every value double-quoted,
/// CRLF line endings — the same dialect as <c>liblcm-inventory.tsv</c> so one set of habits reads both
/// (the manifest README, "Companion files").
/// </summary>
/// <remarks>
/// <para>
/// Read-only, like every other manifest artifact the generator touches. Descriptions are seeded per family as
/// it ships (ADR 0023 decision 5, as amended), so this file grows over time and is expected to be
/// incomplete — which is why
/// <see cref="Checks.DescriptionCheck"/> checks the kinds actually being emitted rather than every row in
/// the manifest.
/// </para>
/// <para>
/// The trailing <c>Source</c>/<c>SourceDetail</c>/<c>SourceHash</c> columns exist because a
/// description with no recorded provenance is exactly the failure mode that let four inverted
/// <c>ProdRestrict</c>-family descriptions pass the original presence-only check. This file is Stage 2 of a
/// two-stage pipeline — <c>Descriptions.Harvest.KindDescriptionRefresher</c> is Stage 1, the re-runnable
/// producer that (re)writes this TSV from <c>MasterLCModel.xml</c> and FieldWorks' <c>ContextHelp.xml</c>.
/// This parser only ever reads whatever is currently checked in.
/// </para>
/// </remarks>
public static class KindDescriptionTsvParser
{
    private const int ColumnCount = 8;

    public static IReadOnlyList<KindDescription> Parse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not read descriptions '{path}': {ex.Message}", ex);
        }

        return ParseText(path, text);
    }

    /// <summary>Exposed for tests, which supply the file's content inline rather than on disk.</summary>
    public static IReadOnlyList<KindDescription> ParseText(string path, string text)
    {
        // CRLF-only split (matches ManifestTsvParser): a line-ending drift must surface, not be tolerated.
        var lines = text.Split("\r\n");

        if (lines.Length == 0 || !lines[0].StartsWith("\"Class\"", StringComparison.Ordinal))
            throw new GeneratorException($"'{path}' does not start with the expected header row.");

        var rows = new List<KindDescription>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue; // trailing blank line after the final CRLF

            var columns = SplitRow(path, i + 1, line);
            var row = new KindDescription(
                Class: columns[0],
                Field: columns[1],
                Label: columns[2],
                Description: columns[3],
                Reviewed: columns[4],
                Source: columns[5],
                SourceDetail: columns[6],
                SourceHash: columns[7]);

            // One description per field, else text depends on read order (ADR 0026's class of defect).
            if (seen.TryGetValue(row.Key, out var firstLine))
                throw new GeneratorException(
                    $"'{path}' line {i + 1}: '{row.Key}' already has a description on line {firstLine}. " +
                    "One description per field.");

            seen[row.Key] = i + 1;
            rows.Add(row);
        }

        return rows;
    }

    private static string[] SplitRow(string path, int lineNumber, string line)
    {
        var rawColumns = line.Split('\t');
        if (rawColumns.Length != ColumnCount)
            throw new GeneratorException(
                $"'{path}' line {lineNumber}: expected {ColumnCount} columns, found {rawColumns.Length}.");

        var columns = new string[ColumnCount];
        for (var i = 0; i < ColumnCount; i++)
            columns[i] = Unquote(path, lineNumber, rawColumns[i]);

        return columns;
    }

    private static string Unquote(string path, int lineNumber, string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            throw new GeneratorException($"'{path}' line {lineNumber}: value '{raw}' is not double-quoted.");

        return raw[1..^1].Replace("\"\"", "\"");
    }
}
