namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>The columns of <c>manifest/liblcm-inventory.tsv</c> this tool needs — read-only, for computing
/// coverage against the harvested labels. Never writes back to the manifest.</summary>
public sealed record ManifestRow(string Class, string Field, string Scope);

/// <summary>
/// Reads <c>manifest/liblcm-inventory.tsv</c>'s dialect: tab-separated, every field double-quoted, CRLF
/// line endings, header row first. Only the columns needed for coverage reporting are extracted.
/// </summary>
public static class ManifestReader
{
    public static IReadOnlyList<ManifestRow> Read(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];

        var header = SplitRow(lines[0]);
        var classIndex = Array.IndexOf(header, "Class");
        var fieldIndex = Array.IndexOf(header, "Field");
        var scopeIndex = Array.IndexOf(header, "Scope");
        if (classIndex < 0 || fieldIndex < 0 || scopeIndex < 0)
            throw new InvalidOperationException($"{path}: expected Class, Field, and Scope columns in the header");

        var rows = new List<ManifestRow>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var fields = SplitRow(lines[i]);
            rows.Add(new ManifestRow(fields[classIndex], fields[fieldIndex], fields[scopeIndex]));
        }

        return rows;
    }

    private static string[] SplitRow(string line) =>
        line.Split('\t').Select(Unquote).ToArray();

    private static string Unquote(string field)
    {
        var trimmed = field.Trim('\r', '\n');
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        return trimmed.Replace("\"\"", "\"");
    }
}
