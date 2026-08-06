using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Checks;

/// <summary>
/// ADR 0023 decision 5, as amended 2026-08-05: every emitted kind must carry a hand-written description, and
/// <b>the build fails if one is missing or if it merely restates the label.</b>
/// </summary>
/// <remarks>
/// <para>
/// The second half is the point. The original decision said only that a description was mandatory, and the
/// FieldWorks label harvest then showed why presence alone is not a bar: it produced 768 rows of labels but
/// <b>only 20 with any prose at all</b>. Everything else is a two-word label — <c>"Form"</c>,
/// <c>"IsAbstract"</c> — which satisfies a presence check and helps nobody. A mandatory field with no quality
/// bar gets filled with the label, and then the check is ceremony rather than infrastructure.
/// </para>
/// <para>
/// <b>Scoped to the kinds actually being emitted, not to the whole manifest.</b> Descriptions are written per
/// family as it ships, so at any moment most in-scope fields have none — 14 of roughly 480 today. Checking
/// every row would fail the build permanently and force someone to write 480 sentences before the generator
/// could emit anything, which is the opposite of what the ADR decided.
/// </para>
/// <para>
/// A <c>draft</c> description passes. The <c>Reviewed</c> column tracks linguist sign-off but does not gate:
/// unreviewed prose is a documentation risk, not a data-corruption risk, and blocking emission on a linguist's
/// availability would make the generator hostage to a review queue.
/// </para>
/// </remarks>
public static class DescriptionCheck
{
    /// <summary>
    /// Requires a usable description for every row in <paramref name="rowsBeingEmitted"/>, reporting every
    /// failure at once rather than the first — someone fixing a family's descriptions wants the whole list,
    /// not one round trip per row.
    /// </summary>
    public static void CheckEmittedKinds(
        IReadOnlyList<JoinedRow> rowsBeingEmitted,
        IReadOnlyList<KindDescription> descriptions)
    {
        var byKey = new Dictionary<string, KindDescription>(StringComparer.Ordinal);
        foreach (var description in descriptions)
            byKey[description.Key] = description;

        var failures = new List<string>();

        foreach (var row in rowsBeingEmitted)
        {
            var key = $"{row.Manifest.Class}.{row.Manifest.Field}";

            if (!byKey.TryGetValue(key, out var description))
            {
                failures.Add(
                    $"{key}: no description. Add one to manifest/kind-descriptions.tsv saying when an agent " +
                    "should reach for this operation (ADR 0023 decision 5).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(description.Description))
            {
                failures.Add($"{key}: description is empty.");
                continue;
            }

            // The bar: a description must say something the name and label do not. Compared on letters and
            // digits only, so "Is Abstract." cannot pass as a description of "IsAbstract".
            if (RestatesOnly(description.Description, description.Label) ||
                RestatesOnly(description.Description, row.Manifest.Field))
            {
                failures.Add(
                    $"{key}: description '{description.Description}' only restates the label or field name. " +
                    "Say when an agent should reach for this operation, not what it is called.");
            }
        }

        if (failures.Count > 0)
            throw new GeneratorException(
                $"{failures.Count} kind(s) lack a usable description:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", failures));
    }

    private static bool RestatesOnly(string description, string other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        return string.Equals(Squash(description), Squash(other), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Letters and digits only, so punctuation and spacing cannot disguise a restatement.</summary>
    private static string Squash(string value)
        => new(value.Where(char.IsLetterOrDigit).ToArray());
}
