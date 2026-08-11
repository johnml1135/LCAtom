using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Selects every remaining in-scope row beyond the lexEntry/moForm family
/// that already fits one of the three generic shapes slices 1/2 built — basic
/// <c>MultiUnicode</c>/<c>MultiString</c>/<c>Boolean</c> <c>set|clear</c>, <c>rel/atomic</c>
/// <c>set|clear</c>, and <c>rel/col</c>/<c>rel/seq</c> <c>addRef|removeRef(|move)</c> — across every
/// declaring class, not just <c>LexEntry</c>/<c>MoForm</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why widening the class filter is safe.</b> Unlike <see cref="BasicFieldSelector"/>/
/// <see cref="RelationFieldSelector"/>, whose class filter exists only to scope the lexical-entry
/// family to <c>LexEntry</c>/<c>MoForm</c>, the templates those two
/// selectors feed — <see cref="AlternativesFieldEmitter"/>/<see cref="BooleanFieldEmitter"/>/
/// <see cref="ReferenceAtomicFieldEmitter"/>/<see cref="ReferenceCollectionFieldEmitter"/> — never
/// hardcode a class name; they read <c>DeclaringClass</c>/<c>FieldName</c>/<c>Sig</c> off the row and
/// build <c>"I" + DeclaringClass</c>/<c>"I" + Sig</c> interface names and
/// <c>{FieldName}RA</c>/<c>RC</c>/<c>RS</c> accessor names by LibLCM's own generated-interface
/// convention. That convention was checked by reflection against the real pinned
/// <c>SIL.LCModel</c> assembly for every row this selector yields (not merely assumed) before this
/// file was written: for each candidate <c>(Class, Field, Sig)</c>, a throwaway console program
/// confirmed <c>"I" + Class</c> exists, carries the expected <c>{Field}</c>/<c>{Field}RA</c>/
/// <c>RC</c>/<c>RS</c> property, and (for reference fields) that <c>"I" + Sig</c> exists too. That
/// harness was scratch work, not checked in; the 225-row candidate list it validated is a superset of
/// what this selector actually yields, since it also covered rows outside <c>HcReachable=yes</c> that
/// this selector does not yield.
/// </para>
/// <para>
/// <b>Why <c>HcReachable=yes</c> is the filter, not "all 239 shape-matching rows".</b> ADR 0025 is
/// the parser-first slice's own authority: the 150 rows it marks <c>HcReachable=yes</c> are exactly
/// "everything the parser touches, plus the material you judge it against" — the stated priority
/// for what this generator builds next. Of those 150, 4 already have a generated kind (<c>MoForm.Form</c>,
/// <c>MoForm.IsAbstract</c>, <c>MoForm.MorphType</c>, <c>LexSense.Gloss</c> — all class-filtered into
/// slice 1/2 already) and the rest are of shapes this generator does not support yet (owning/atomic
/// beyond the one hand-written field, owning/col, owning/seq). What is left — 78 rows — is exactly
/// what this selector yields. The other ~147 in-scope rows that share these three shapes but are not
/// <c>HcReachable=yes</c> are deliberately excluded here, not blocked by a missing
/// shape: see <see cref="Slice3CatalogWriter"/>'s remarks for the accounting.
/// </para>
/// </remarks>
public static class Slice3FieldSelector
{
    /// <summary>Fields slice 1/2 already emit that also happen to be <c>HcReachable=yes</c>, named explicitly so this selector never double-emits a kind slice 1/2 already owns.</summary>
    private static readonly HashSet<FieldKey> AlreadyEmittedElsewhere = new()
    {
        new FieldKey("MoForm", "Form"),
        new FieldKey("MoForm", "IsAbstract"),
        new FieldKey("MoForm", "MorphType"),
        new FieldKey("LexSense", "Gloss"),
    };

    private static readonly string[] BasicOkSigs = { "MultiUnicode", "MultiString", "Boolean" };

    public static IReadOnlyList<JoinedRow> SelectBasicSetClear(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Basic &&
            row.Manifest.Verbs == "set|clear" &&
            Array.IndexOf(BasicOkSigs, row.Sig) >= 0);

    public static IReadOnlyList<JoinedRow> SelectAtomicSetClear(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Rel &&
            row.Card == FieldCard.Atomic &&
            row.Manifest.Verbs == "set|clear");

    public static IReadOnlyList<JoinedRow> SelectCollectionAddRemove(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Rel &&
            (row.Card == FieldCard.Col || row.Card == FieldCard.Seq) &&
            (row.Manifest.Verbs == "addRef|removeRef" || row.Manifest.Verbs == "addRef|removeRef|move"));

    private static IReadOnlyList<JoinedRow> Filter(IReadOnlyList<JoinedRow> rows, Func<JoinedRow, bool> shapeFilter)
    {
        var selected = new List<JoinedRow>();

        foreach (var row in rows)
        {
            if (row.Manifest.Scope != "in") continue;
            if (row.Manifest.HcReachable != "yes") continue;
            if (AlreadyEmittedElsewhere.Contains(row.Key)) continue;
            if (!shapeFilter(row)) continue;

            selected.Add(row);
        }

        return selected;
    }
}
