using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Assembles a <see cref="ReferenceAtomicFieldSpec"/>/<see cref="ReferenceCollectionFieldSpec"/> from
/// one <see cref="JoinedRow"/>, calling the same <see cref="GroupDerivation"/>/
/// <see cref="ConstructDerivation"/>/<see cref="KindNameDerivation"/> <see cref="BasicFieldSpecBuilder"/>
/// calls for slice 1 — glue, not a second place that decides group/construct/kind.
/// </summary>
public static class RelationFieldSpecBuilder
{
    public static ReferenceAtomicFieldSpec BuildAtomic(JoinedRow row)
    {
        var group = GroupDerivation.Derive(row.DeclaringClass);
        var construct = ConstructDerivation.Derive(row.DeclaringClass);

        return new ReferenceAtomicFieldSpec(
            DeclaringClass: row.DeclaringClass,
            FieldName: row.FieldName,
            Sig: row.Sig,
            Group: group,
            Construct: construct,
            SetKind: KindNameDerivation.DeriveOne(group, construct, "set", row.FieldName),
            ClearKind: KindNameDerivation.DeriveOne(group, construct, "clear", row.FieldName),
            TargetInterface: "I" + row.DeclaringClass,
            RefInterface: "I" + row.Sig,
            // LibLCM's own accessor-property suffix convention (SIL.LCModel's IMoForm): "RA" for a reference atomic.
            AccessorPropertyName: row.FieldName + "RA",
            SnapshotFieldConstant: row.DeclaringClass + row.FieldName);
    }

    public static ReferenceCollectionFieldSpec BuildCollection(JoinedRow row)
    {
        var group = GroupDerivation.Derive(row.DeclaringClass);
        var construct = ConstructDerivation.Derive(row.DeclaringClass);

        if (row.Card is not { } card)
        {
            throw new GeneratorException(
                $"{row.DeclaringClass}.{row.FieldName}: a rel/col or rel/seq field must carry a Card.");
        }

        // LibLCM's accessor suffixes (SIL.LCModel's ILexEntry): RC reference collection, RS reference sequence.
        var accessorSuffix = card == FieldCard.Seq ? "RS" : "RC";

        return new ReferenceCollectionFieldSpec(
            DeclaringClass: row.DeclaringClass,
            FieldName: row.FieldName,
            Sig: row.Sig,
            Card: card,
            Group: group,
            Construct: construct,
            AddRefKind: KindNameDerivation.DeriveOne(group, construct, "addRef", row.FieldName),
            RemoveRefKind: KindNameDerivation.DeriveOne(group, construct, "removeRef", row.FieldName),
            TargetInterface: "I" + row.DeclaringClass,
            RefInterface: "I" + row.Sig,
            AccessorPropertyName: row.FieldName + accessorSuffix,
            SnapshotFieldConstant: row.DeclaringClass + row.FieldName);
    }

    public static IReadOnlyList<ReferenceAtomicFieldSpec> BuildAllAtomic(IReadOnlyList<JoinedRow> rows) =>
        rows.Select(BuildAtomic).ToList();

    public static IReadOnlyList<ReferenceCollectionFieldSpec> BuildAllCollection(IReadOnlyList<JoinedRow> rows) =>
        rows.Select(BuildCollection).ToList();
}
