namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Turns raw harvested facts into output rows: merges repeated occurrences of the same
/// <c>(class, field, label)</c> into one row (per the deliverable's "one row per distinct label, not one
/// per field" rule), then resolves <c>Confidence</c> by checking whether any other, disagreeing label
/// exists for the same <c>(class, field)</c> pair.
/// </summary>
public static class LabelResolver
{
    /// <summary>Occurrences of the same (class, field, label) beyond this count collapse into a "+N more"
    /// note in <c>SourceDetail</c> rather than listing every one — some generic labels like "Name" recur
    /// dozens of times across unrelated layouts.</summary>
    private const int MaxSourceDetailExamples = 5;

    public static IReadOnlyList<LabelRow> Resolve(IEnumerable<RawLabel> raw)
    {
        var merged = raw
            .GroupBy(r => (r.Class, r.Field, r.Label))
            .Select(MergeGroup)
            .ToList();

        var byPair = merged
            .GroupBy(r => (r.Class, r.Field))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Label).Distinct().Count());

        var resolved = merged
            .Select(r =>
            {
                var distinctLabels = byPair[(r.Class, r.Field)];
                var confidence = distinctLabels > 1
                    ? "ambiguous"
                    : r.Field.Length > 0 ? "exact" : "class-only";
                return new LabelRow(r.Class, r.Field, r.Label, r.Tooltip, r.Source, r.SourceDetail, confidence);
            })
            .OrderBy(r => r.Class, StringComparer.Ordinal)
            .ThenBy(r => r.Field, StringComparer.Ordinal)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ToList();

        return resolved;
    }

    private static (string Class, string Field, string Label, string Tooltip, string Source, string SourceDetail) MergeGroup(
        IGrouping<(string Class, string Field, string Label), RawLabel> group)
    {
        var (cls, field, label) = group.Key;
        var tooltip = group.Select(r => r.Tooltip).FirstOrDefault(t => t.Length > 0) ?? "";
        var sources = group.Select(r => r.Source).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var details = group.Select(r => r.SourceDetail).Distinct(StringComparer.Ordinal).ToList();

        var detailText = details.Count <= MaxSourceDetailExamples
            ? string.Join(" | ", details)
            : string.Join(" | ", details.Take(MaxSourceDetailExamples)) + $" | +{details.Count - MaxSourceDetailExamples} more";

        return (cls, field, label, tooltip, string.Join("+", sources), detailText);
    }
}
