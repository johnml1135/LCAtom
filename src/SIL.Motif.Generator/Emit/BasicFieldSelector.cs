using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Selects MOT-4 slice 1's exact field list from the joined model — "derive that list from the
/// manifest and the model; do not hardcode it" (docs/plan-motif.md, MOT-4). Two rules, neither of
/// which hardcodes a field name:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>The class filter.</b> Every <c>Scope=in</c>, <c>Kind=basic</c>, <c>Verbs=set|clear</c>
/// row declared on <see cref="LexEntryAndMoFormClasses"/> — the two classes docs/plan-motif.md's
/// <c>MOT-4</c> section names as this slice's family (<c>lexEntry</c>'s authorable rows, "plus ...
/// the <c>MoForm</c> rows that bring an entry into existence"). This mechanically yields exactly
/// nine rows today (seven <c>LexEntry</c>, two <c>MoForm</c>) and would pick up any new field a
/// LibLCM upgrade adds to either class with this same shape, with no further hand-editing.</item>
/// <item><b>The one named exception.</b> <c>LexSense.Gloss</c> is added explicitly, by
/// <see cref="FieldKey"/>, not by widening the class filter to <c>LexSense</c> — <c>LexSense</c>
/// has eighteen other <c>Scope=in</c> basic <c>set|clear</c> rows (<c>AnthroNote</c>,
/// <c>Definition</c>, ...) that are deliberately out of this slice. Gloss is singled out because it
/// is the pre-existing hand-written kind MOT-4's gate requires to be regenerated, not because it
/// belongs to the same construct-scope decision as rule 1.</item>
/// </list>
/// </remarks>
public static class BasicFieldSelector
{
    private static readonly string[] LexEntryAndMoFormClasses = { "LexEntry", "MoForm" };
    private static readonly FieldKey GlossKey = new("LexSense", "Gloss");

    public static IReadOnlyList<JoinedRow> SelectSlice1BasicFields(IReadOnlyList<JoinedRow> rows)
    {
        var selected = new List<JoinedRow>();

        foreach (var row in rows)
        {
            if (row.Manifest.Scope != "in") continue;
            if (row.Kind != FieldKind.Basic) continue;
            if (row.Manifest.Verbs != "set|clear") continue;

            var isClassFilterMatch = Array.IndexOf(LexEntryAndMoFormClasses, row.DeclaringClass) >= 0;
            var isTheNamedGlossException = row.Key.Equals(GlossKey);

            if (isClassFilterMatch || isTheNamedGlossException)
                selected.Add(row);
        }

        return selected;
    }
}
