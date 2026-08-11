namespace SIL.Motif.Generator.Manifest;

/// <summary>
/// Parses <c>manifest/liblcm-inventory.tsv</c>: 18 columns, tab-separated, every value
/// double-quoted, CRLF line endings (manifest/README.md). This file is
/// read-only to the generator — never modify it, per manifest/README.md's own instruction — so this
/// class only ever reads.
/// </summary>
public static class ManifestTsvParser
{
    private const int ColumnCount = 18;

    public static IReadOnlyList<ManifestRow> Parse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not read manifest '{path}': {ex.Message}", ex);
        }

        // Splitting on "\r\n" rather than agnostically keeps an LF/CRLF drift visible instead of tolerated.
        var lines = text.Split("\r\n");

        if (lines.Length == 0 || !lines[0].StartsWith("\"Class\"", StringComparison.Ordinal))
            throw new GeneratorException($"'{path}' does not start with the expected header row.");

        var rows = new List<ManifestRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue; // trailing blank line after the final CRLF

            var columns = SplitRow(path, i + 1, line);
            rows.Add(new ManifestRow(
                Class: columns[0],
                Base: columns[1],
                Abstract: columns[2],
                Scope: columns[3],
                ScopeReason: columns[4],
                Field: columns[5],
                Kind: columns[6],
                Sig: columns[7],
                Card: columns[8],
                HcReferenced: columns[9],
                Construct: columns[10],
                Group: columns[11],
                Classification: columns[12],
                ComparisonClass: columns[13],
                Verbs: columns[14],
                HcReachable: columns[15],
                EnumValues: columns[16],
                Rationale: columns[17]));
        }

        return rows;
    }

    private static string[] SplitRow(string path, int lineNumber, string line)
    {
        // A bare tab-split is safe only while no quoted field embeds a literal tab; "" unescapes to ".
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
