using System.Text;
using SIL.Motif.Generator.ModelSource;

namespace SIL.Motif.Generator.Coverage;

/// <summary>
/// The generator's model coverage report: a plain data record plus a printable form, not a side
/// effect, so a test can assert on the numbers directly instead of scraping text.
/// </summary>
public sealed record ModelCoverageReport(
    string ModelFilePath,
    ModelPathSource ModelFilePathSource,
    string ModelVersion,
    int TotalFieldCount,
    int InScopeCount,
    int OutOfScopeCount,
    IReadOnlyDictionary<string, int> CountsByKindAndCard,
    IReadOnlyDictionary<string, int> KindsPerGroup)
{
    /// <summary>Human-readable form for CI logs. Every number here is also on the record itself, so
    /// nothing a test needs is available only by parsing this string.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Motif generator model coverage report");
        sb.AppendLine($"  MasterLCModel.xml path : {ModelFilePath}");
        sb.AppendLine($"  path source            : {ModelFilePathSource}");
        sb.AppendLine($"  model version          : {ModelVersion}");
        sb.AppendLine($"  total (Class, Field)   : {TotalFieldCount}");
        sb.AppendLine($"  in-scope               : {InScopeCount}");
        sb.AppendLine($"  out of scope           : {OutOfScopeCount}");

        sb.AppendLine("  counts by (Kind, Card):");
        foreach (var (key, count) in CountsByKindAndCard.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"    {key,-16} {count}");

        sb.AppendLine("  kinds that would be emitted, per group:");
        foreach (var (group, count) in KindsPerGroup.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"    {group,-16} {count}");

        return sb.ToString();
    }
}
