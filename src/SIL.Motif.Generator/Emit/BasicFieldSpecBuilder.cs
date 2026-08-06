using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Assembles a <see cref="BasicFieldSpec"/> from one <see cref="JoinedRow"/>, calling
/// <see cref="GroupDerivation"/>/<see cref="ConstructDerivation"/>/<see cref="KindNameDerivation"/>
/// exactly as MOT-2's checks do — this is glue, not a fourth place that decides group/construct/kind.
/// </summary>
public static class BasicFieldSpecBuilder
{
    public static BasicFieldSpec Build(JoinedRow row)
    {
        var group = GroupDerivation.Derive(row.DeclaringClass);
        var construct = ConstructDerivation.Derive(row.DeclaringClass);
        var setKind = KindNameDerivation.DeriveOne(group, construct, "set", row.FieldName);
        var clearKind = KindNameDerivation.DeriveOne(group, construct, "clear", row.FieldName);

        return new BasicFieldSpec(
            DeclaringClass: row.DeclaringClass,
            FieldName: row.FieldName,
            Sig: row.Sig,
            Group: group,
            Construct: construct,
            SetKind: setKind,
            ClearKind: clearKind,
            TargetInterface: "I" + row.DeclaringClass,
            SnapshotFieldConstant: row.DeclaringClass + row.FieldName);
    }

    public static IReadOnlyList<BasicFieldSpec> BuildAll(IReadOnlyList<JoinedRow> rows) =>
        rows.Select(Build).ToList();
}
