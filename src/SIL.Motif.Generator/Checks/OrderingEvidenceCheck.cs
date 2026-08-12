using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Ordering;

namespace SIL.Motif.Generator.Checks;

/// <summary>
/// Keeps <c>manifest/ordering-evidence.tsv</c> honest: it covers exactly the rows that claim order carries
/// meaning, and a row that quotes the model actually cites and digests what it quoted.
/// </summary>
/// <remarks>
/// <para>
/// This does <b>not</b> require every row to have evidence — 32 of the 64 have none, and saying so is the
/// point. What it forbids is the file drifting out of step with the manifest, which is the way a review
/// artifact quietly stops describing the thing it was written about: a new <c>seq</c> field ships,
/// <c>ComparisonClass</c> derives <c>positional</c>, and nobody ever looks at whether that is true, because
/// the evidence file still lists yesterday's rows and looks complete.
/// </para>
/// </remarks>
public static class OrderingEvidenceCheck
{
    public static void Check(IReadOnlyList<JoinedRow> allRows, IReadOnlyList<OrderingEvidence> evidence)
    {
        var failures = new List<string>();

        var expected = OrderingEvidenceHarvester.RowsNeedingEvidence(allRows)
            .Select(r => $"{r.DeclaringClass}.{r.FieldName}")
            .ToHashSet(StringComparer.Ordinal);
        var actual = evidence.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var missing in expected.Except(actual).OrderBy(k => k, StringComparer.Ordinal))
        {
            failures.Add(
                $"{missing}: claims order carries meaning but has no row in ordering-evidence.tsv. Re-run " +
                "`dotnet run --project src/SIL.Motif.Generator -- harvest-ordering-evidence`.");
        }

        foreach (var stale in actual.Except(expected).OrderBy(k => k, StringComparer.Ordinal))
        {
            failures.Add(
                $"{stale}: has a row in ordering-evidence.tsv but no longer claims order carries meaning. " +
                "Re-harvest, so the file cannot describe a manifest that no longer exists.");
        }

        foreach (var row in evidence)
        {
            if (row.HasStatement)
            {
                if (string.IsNullOrWhiteSpace(row.Source) || string.IsNullOrWhiteSpace(row.SourceDetail))
                    failures.Add($"{row.Key}: quotes the model but records no citation.");

                if (row.SourceHash != Descriptions.Harvest.SourceDigest.OfText(row.Statement))
                    failures.Add($"{row.Key}: SourceHash does not match the statement it is stored beside.");

                if (string.IsNullOrWhiteSpace(row.MatchedTerms))
                    failures.Add($"{row.Key}: quotes the model but records no matched term, so the selection cannot be audited.");
            }
            else if (row.Source.Length > 0 || row.SourceDetail.Length > 0 || row.SourceHash.Length > 0)
            {
                failures.Add($"{row.Key}: has no statement but carries a citation or digest.");
            }
        }

        if (failures.Count > 0)
            throw new GeneratorException(
                $"{failures.Count} ordering-evidence problem(s):{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", failures));
    }
}
