using System.Text;

namespace SIL.Motif.Generator.Descriptions;

/// <summary>
/// Writes <see cref="KindDescription"/> rows in the exact dialect <see cref="KindDescriptionTsvParser"/>
/// reads: tab-separated, every value double-quoted, CRLF line endings, no BOM — matching
/// <c>SIL.Motif.Spikes.LabelHarvest</c>'s <c>LabelTsvWriter</c> so the whole manifest family shares one
/// convention.
/// </summary>
public static class KindDescriptionTsvWriter
{
    public static readonly string[] Header = ["Class", "Field", "Label", "Description", "Reviewed", "Source", "SourceDetail"];

    public static void Write(string path, IEnumerable<KindDescription> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);

        foreach (var row in rows)
            AppendRow(sb, [row.Class, row.Field, row.Label, row.Description, row.Reviewed, row.Source, row.SourceDetail]);

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
        sb.Append('"');
        sb.Append(value.Replace("\"", "\"\""));
        sb.Append('"');
    }
}
