using SIL.Motif.Generator.Tsv;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads and writes <c>manifest/fieldworks-help-descriptions.tsv</c> — the seam that keeps the compiled
/// help file out of everything except the one dev-time command that opens it.
/// </summary>
/// <remarks>
/// <c>harvest-help</c> writes this file on a Windows machine with a FieldWorks checkout;
/// <c>refresh-descriptions</c>, the build, and the tests only ever read it. That is what makes the
/// <c>.chm</c> dependency a personal-machine concern rather than a build dependency, and it is why the
/// harvested text is checked in rather than re-extracted on demand.
/// </remarks>
public static class FieldWorksHelpDescriptionTsv
{
    public static readonly string[] Header =
        ["Class", "Field", "PageTitle", "Description", "HelpPage", "Confidence", "VerifiedAgainst"];

    public static void Write(string path, IEnumerable<HarvestedHelpDescription> rows) =>
        QuotedTsv.Write(path, Header, rows.Select(r => new[]
        {
            r.Class, r.Field, r.PageTitle, r.Description, r.HelpPage, r.Confidence, r.VerifiedAgainst,
        }));

    public static IReadOnlyList<HarvestedHelpDescription> Read(string path) =>
        QuotedTsv.Read(path, Header)
            .Select(c => new HarvestedHelpDescription(c[0], c[1], c[2], c[3], c[4], c[5], c[6]))
            .ToList();

    /// <summary>
    /// Keyed for <see cref="KindDescriptionRefresher"/>, which looks a field up rather than scanning.
    /// A duplicate (Class, Field) is a defect in the harvest, not something to resolve by read order.
    /// </summary>
    public static IReadOnlyDictionary<(string Class, string Field), HarvestedHelpDescription> ByField(
        IReadOnlyList<HarvestedHelpDescription> rows)
    {
        var byField = new Dictionary<(string, string), HarvestedHelpDescription>();
        foreach (var row in rows)
        {
            if (!byField.TryAdd((row.Class, row.Field), row))
                throw new GeneratorException($"'{row.Key}' appears twice in the harvested help descriptions.");
        }

        return byField;
    }
}
