using System.Text;

namespace SIL.Motif.Generator.Tsv;

/// <summary>
/// The one TSV dialect every checked-in manifest artifact in this repo uses: tab-separated, every value
/// double-quoted with <c>""</c> escaping, CRLF line endings, UTF-8 with no BOM
/// (the manifest README, "Companion files").
/// </summary>
/// <remarks>
/// <see cref="Manifest.ManifestTsvParser"/> and <see cref="Descriptions.KindDescriptionTsvParser"/> predate
/// this and keep their own hand-rolled readers, because each carries column-specific validation and a
/// row-shaped record that a generic reader cannot produce. This exists so the *next* artifact — the
/// description-harvest outputs — does not become a third and fourth copy of the same quoting rules.
/// </remarks>
public static class QuotedTsv
{
    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, header);
        foreach (var row in rows)
        {
            if (row.Count != header.Count)
            {
                throw new GeneratorException(
                    $"'{path}': a row has {row.Count} value(s) but the header declares {header.Count}.");
            }

            AppendRow(sb, row);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Reads a file written by <see cref="Write"/>, checking the header matches <paramref name="header"/>
    /// exactly. A silently-renamed or reordered column is the failure this guards: every consumer here
    /// reads by position, so a header drift would quietly shift every value one cell left.
    /// </summary>
    public static IReadOnlyList<string[]> Read(string path, IReadOnlyList<string> header)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not read '{path}': {ex.Message}", ex);
        }

        return ReadText(path, text, header);
    }

    /// <summary>Exposed for tests, which supply the content inline rather than on disk.</summary>
    public static IReadOnlyList<string[]> ReadText(string path, string text, IReadOnlyList<string> header)
    {
        var lines = text.Split("\r\n");
        var expectedHeader = Render(header);

        if (lines.Length == 0 || lines[0] != expectedHeader)
        {
            throw new GeneratorException(
                $"'{path}' does not start with the expected header row.{Environment.NewLine}" +
                $"  expected: {expectedHeader}{Environment.NewLine}" +
                $"  found   : {(lines.Length == 0 ? "(empty file)" : lines[0])}");
        }

        var rows = new List<string[]>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue; // trailing blank line after the final CRLF

            var raw = lines[i].Split('\t');
            if (raw.Length != header.Count)
            {
                throw new GeneratorException(
                    $"'{path}' line {i + 1}: expected {header.Count} columns, found {raw.Length}.");
            }

            var values = new string[raw.Length];
            for (var c = 0; c < raw.Length; c++)
                values[c] = Unquote(path, i + 1, raw[c]);

            rows.Add(values);
        }

        return rows;
    }

    private static string Render(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            AppendQuoted(sb, fields[i]);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        sb.Append(Render(fields));
        sb.Append("\r\n");
    }

    private static void AppendQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        sb.Append(value.Replace("\"", "\"\""));
        sb.Append('"');
    }

    private static string Unquote(string path, int lineNumber, string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            throw new GeneratorException($"'{path}' line {lineNumber}: value '{raw}' is not double-quoted.");

        return raw[1..^1].Replace("\"\"", "\"");
    }
}
