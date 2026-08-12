using System.Text;

namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Writes <see cref="LabelRow"/>s in the exact TSV dialect <c>manifest/liblcm-inventory.tsv</c> uses:
/// tab-separated, every field double-quoted, CRLF line endings — so the same downstream tooling reads both.
/// </summary>
public static class LabelTsvWriter
{
    public static readonly string[] Header =
        ["Class", "Field", "Label", "Tooltip", "Source", "SourceDetail", "Confidence"];

    public static void Write(string path, IEnumerable<LabelRow> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);

        foreach (var row in rows)
        {
            AppendRow(sb, [row.Class, row.Field, row.Label, row.Tooltip, row.Source, row.SourceDetail, row.Confidence]);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append('\t');
            AppendQuoted(sb, fields[i]);
        }
        sb.Append("\r\n");
    }

    private static void AppendQuoted(StringBuilder sb, string value)
    {
        // Match liblcm-inventory.tsv's dialect: quote every field, and escape a quote by doubling it.
        sb.Append('"');
        sb.Append(value.Replace("\"", "\"\""));
        sb.Append('"');
    }
}
