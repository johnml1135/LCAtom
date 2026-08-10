using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Selects MOT-22's shape: a <c>basic Integer</c> field whose manifest <c>EnumValues</c> column names a
/// small closed set of legal values, in the <c>analysis</c> domain group — today exactly one field,
/// <c>WfiWordform.SpellingStatus</c>. Verbs are the plain derived <c>set|clear</c>; nothing about this
/// shape departs from <see cref="Derivation.VerbDerivation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate shape at all,</b> when <see cref="Slice3FieldSelector"/> already emits basic
/// <c>set|clear</c>: the sigs it accepts are <c>MultiUnicode</c>/<c>MultiString</c>/<c>Boolean</c>, none
/// of which needs a range check. An <c>Integer</c> field standing in for an enum does: LibLCM's own
/// <c>WfiWordform.ValidateSpellingStatus</c> exists, but a caller must not depend on it to silently fix
/// an out-of-range value, so this shape's payload parser rejects one before any lowering call runs.
/// </para>
/// <para>
/// <b>Why the group filter, not the shape alone.</b> Twelve in-scope rows are basic <c>Integer</c> with a
/// confirmed <c>EnumValues</c> mapping; eleven of them (<c>CmPicture.LayoutPos</c>,
/// <c>LexEntryRef.RefType</c>, <c>PhSegmentRule.Direction</c>, ...) belong to families no slice ships
/// yet. Filtering on domain group is the same discipline <see cref="Slice3FieldSelector"/> applies with
/// <c>HcReachable=yes</c>: the shape is general, the slice that uses it is scoped, and a later increment
/// widens the filter rather than the template. <c>analysis</c> is MOT-22's own family (ADR 0025's
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

    /// <summary>
    /// The manifest's own "we have not confirmed this field's legal values" marker is the literal
    /// <c>unknown</c> (manifest/README.md), and an empty cell means the field is a plain integer rather
    /// than an enum — neither can feed a range check, so neither is this shape.
    /// </summary>
    private static bool HasConfirmedEnumValues(JoinedRow row) =>
        !string.IsNullOrWhiteSpace(row.Manifest.EnumValues) && row.Manifest.EnumValues != "unknown";
}
