using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Selects the basic-Integer-enum shape: a <c>basic Integer</c> field whose manifest <c>EnumValues</c>
/// column names a small closed set of legal values, in the <c>analysis</c> domain group (e.g.
/// <c>WfiWordform.SpellingStatus</c>). Verbs are the plain derived <c>set|clear</c>; nothing about this
/// shape departs from <see cref="Derivation.VerbDerivation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate shape at all,</b> when <see cref="Slice3FieldSelector"/> already emits basic
/// <c>set|clear</c>: the sigs it accepts are <c>MultiUnicode</c>/<c>MultiString</c>/<c>Boolean</c>, none
/// of which needs a range check. An <c>Integer</c> field standing in for an enum does: LibLCM's own
/// <c>WfiWordform.ValidateSpellingStatus</c> exists, but a caller must not depend on it to silently fix
/// an out-of-range value, so this shape's payload parser throws on one before any lowering call runs.
/// </para>
/// <para>
/// <b>Why the group filter, not the shape alone.</b> Twelve in-scope rows are basic <c>Integer</c> with a
/// confirmed <c>EnumValues</c> mapping; eleven of them (<c>CmPicture.LayoutPos</c>,
/// <c>LexEntryRef.RefType</c>, <c>PhSegmentRule.Direction</c>, ...) belong to families this selector
/// does not target. Filtering on domain group is the same discipline <see cref="Slice3FieldSelector"/>
/// applies with <c>HcReachable=yes</c>: the shape is general, and the group filter — not the template —
/// is what scopes it to one family. <c>analysis</c> is the family this shape targets (ADR 0025's
/// analysis-approval treatment), and <c>WfiWordform.SpellingStatus</c> is the only row in it with this
/// shape.
/// </para>
/// </remarks>
public static class Slice4FieldSelector
{
    public static IReadOnlyList<JoinedRow> SelectIntegerEnum(IReadOnlyList<JoinedRow> rows) =>
        rows.Where(row =>
                row.Manifest.Scope == "in" &&
                row.Manifest.Group == "analysis" &&
                row.Kind == FieldKind.Basic &&
                row.Sig == "Integer" &&
                row.Manifest.Verbs == "set|clear" &&
                HasConfirmedEnumValues(row))
            .ToList();

    /// <summary><c>unknown</c> marks unconfirmed, empty means plain integer; neither feeds a range check.</summary>
    private static bool HasConfirmedEnumValues(JoinedRow row) =>
        !string.IsNullOrWhiteSpace(row.Manifest.EnumValues) && row.Manifest.EnumValues != "unknown";
}
