using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Selects the remaining field lists from the joined model — the same "derive the list, do not
/// hardcode it" discipline <see cref="BasicFieldSelector"/> established for slice 1, extended to the
/// three remaining structural shapes for this family:
/// <c>rel/atomic</c> (<c>MoForm.MorphType</c>), <c>rel/col</c>/<c>rel/seq</c>
/// (<c>LexEntry.DialectLabels</c>, <c>.DoNotPublishIn</c>, <c>.DoNotShowMainEntryIn</c>), and
/// <c>owning/atomic</c> <c>create|delete</c> (<c>LexEntry.LexemeForm</c>).
/// </summary>
/// <remarks>
/// One class filter, shared with <see cref="BasicFieldSelector"/>: <c>LexEntry</c>/<c>MoForm</c>, the
/// two classes that make up this family. Each of the three selectors
/// below additionally filters by <c>(Kind, Card, Verbs)</c>, which mechanically yields exactly the
/// intended rows — checked against the real manifest in
/// <c>RelationFieldSelectorTests</c> — with no field name hardcoded anywhere in the filter itself.
/// </remarks>
public static class RelationFieldSelector
{
    private static readonly string[] LexEntryAndMoFormClasses = { "LexEntry", "MoForm" };

    public static IReadOnlyList<JoinedRow> SelectAtomicSetClear(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Rel &&
            row.Card == FieldCard.Atomic &&
            row.Manifest.Verbs == "set|clear");

    /// <summary>
    /// <c>rel/col</c> fields carry <c>Verbs=addRef|removeRef</c>; <c>rel/seq</c> fields carry
    /// <c>Verbs=addRef|removeRef|move</c> (<c>VerbDerivation</c>). Both are selected here — the
    /// <c>move</c> verb a <c>rel/seq</c> row's <see cref="Join.JoinedRow.Manifest"/> carries is simply
    /// never asked for by <see cref="RelationFieldSpecBuilder"/>, which derives only the two kinds this
    /// slice emits.
    /// </summary>
    public static IReadOnlyList<JoinedRow> SelectCollectionAddRemove(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Rel &&
            (row.Card == FieldCard.Col || row.Card == FieldCard.Seq) &&
            (row.Manifest.Verbs == "addRef|removeRef" || row.Manifest.Verbs == "addRef|removeRef|move"));

    public static IReadOnlyList<JoinedRow> SelectOwningAtomicCreateDelete(IReadOnlyList<JoinedRow> rows) =>
        Filter(rows, row =>
            row.Kind == FieldKind.Owning &&
            row.Card == FieldCard.Atomic &&
            row.Manifest.Verbs == "create|delete");

    private static IReadOnlyList<JoinedRow> Filter(IReadOnlyList<JoinedRow> rows, Func<JoinedRow, bool> shapeFilter)
    {
        var selected = new List<JoinedRow>();

        foreach (var row in rows)
        {
            if (row.Manifest.Scope != "in") continue;
            if (Array.IndexOf(LexEntryAndMoFormClasses, row.DeclaringClass) < 0) continue;
            if (!shapeFilter(row)) continue;

            selected.Add(row);
        }

        return selected;
    }
}
