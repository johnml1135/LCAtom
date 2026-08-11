namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Every derived fact the basic-Integer-enum template needs for one field: a small closed-range enum
/// LibLCM stores as a plain <c>Integer</c>, authorable with the derived <c>set|clear</c> like every
/// other basic field, where <c>clear</c> writes the enum's zero member.
/// </summary>
/// <param name="TargetInterface">The LibLCM interface a resolved target is cast to: <c>"I" +
/// DeclaringClass</c>, matching <see cref="BasicFieldSpec"/>.</param>
/// <param name="SnapshotFieldConstant">The <c>SnapshotFields</c> constant name:
/// <c>DeclaringClass + FieldName</c>, matching <see cref="BasicFieldSpec"/>'s collision-resistance
/// rationale.</param>
/// <param name="EnumMembers">Every <c>value=Name</c> pair from the manifest's <c>EnumValues</c> column
/// (manifest/README.md), in ascending value order — e.g. <c>[(0, "Undecided"), (1, "Correct"),
/// (2, "Incorrect")]</c> for <c>WfiWordform.SpellingStatus</c>. Drives both the payload's range check
/// and the values named in a validation failure message — the point being that an out-of-range value
/// fails loudly rather than reaching LibLCM's own (unrelied-upon) validation.</param>
/// <param name="ZeroMemberName">
/// The name of the <c>0</c> member — <c>Undecided</c> for <c>WfiWordform.SpellingStatus</c> — which is
/// what <c>clear</c> writes, and which the generated documentation names so a caller reads what clearing
/// actually does rather than assuming it erases something. This shape requires every enum to have a zero
/// member: <c>clear</c> would otherwise have to write a value outside the field's own confirmed set,
/// which is exactly what this shape's range check exists to stop.
/// </param>
public sealed record IntegerEnumFieldSpec(
    string DeclaringClass,
    string FieldName,
    string Sig,
    string Group,
    string Construct,
    string SetKind,
    string ClearKind,
    string TargetInterface,
    string SnapshotFieldConstant,
    IReadOnlyList<(int Value, string Name)> EnumMembers,
    string ZeroMemberName);
