using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Checks;

/// <summary>
/// Keeps <see cref="DescriptionExemptions"/> honest in both directions: no row may claim
/// <c>no-source-exists</c> without being in the table, no entry may sit in the table without a row that
/// uses it, and an entry whose evidence is a rule must have that rule still hold.
/// </summary>
/// <remarks>
/// The failure this exists to prevent is the one an exemption list always eventually produces: an allowance
/// granted for a real reason, kept long after the reason stopped being true, and read by the next person as
/// evidence that somebody checked.
/// </remarks>
public static class DescriptionExemptionCheck
{
    public static void Check(IReadOnlyList<JoinedRow> allRows, IReadOnlyList<KindDescription> descriptions)
    {
        var failures = new List<string>();
        var byKey = descriptions.ToDictionary(d => (d.Class, d.Field));

        foreach (var description in descriptions)
        {
            if (description.Reviewed != DescriptionExemptions.ReviewedValue) continue;

            if (!DescriptionExemptions.ByField.ContainsKey((description.Class, description.Field)))
            {
                failures.Add(
                    $"{description.Key}: Reviewed is '{DescriptionExemptions.ReviewedValue}' but the field " +
                    "is not in DescriptionExemptions.Entries. A row claiming no source exists has to say " +
                    "where it was searched for, in code, where the next person will find it.");
                continue;
            }

            if (description.Source != DescriptionExemptions.SourceValue ||
                string.IsNullOrWhiteSpace(description.SourceDetail))
            {
                failures.Add(
                    $"{description.Key}: an exempt row must carry Source='{DescriptionExemptions.SourceValue}' " +
                    $"and the evidence of absence in SourceDetail; found Source='{description.Source}'.");
            }
        }

        foreach (var entry in DescriptionExemptions.Entries)
        {
            if (!byKey.TryGetValue((entry.Class, entry.Field), out var description))
            {
                failures.Add(
                    $"{entry.Key}: exempted in DescriptionExemptions.Entries but has no row in " +
                    "manifest/kind-descriptions.tsv. Remove the exemption or restore the row.");
                continue;
            }

            if (description.Reviewed != DescriptionExemptions.ReviewedValue)
            {
                failures.Add(
                    $"{entry.Key}: exempted in DescriptionExemptions.Entries, but its row says " +
                    $"Reviewed='{description.Reviewed}'. If a source was found, delete the exemption — a " +
                    "stale allowance reads as evidence that somebody checked.");
            }

            if (entry.Rule == DescriptionExemptions.DerivedRule)
            {
                var broken = DescriptionExemptions.AbstractDeclarationOnly(allRows, entry.Class, entry.Field);
                if (broken.Length > 0)
                    failures.Add($"{entry.Key}: the '{entry.Rule}' rule no longer holds — {broken}");
            }
        }

        if (failures.Count > 0)
            throw new GeneratorException(
                $"{failures.Count} description exemption problem(s):{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", failures));
    }
}
