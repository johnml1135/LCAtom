using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Every derived fact MOT-4 slice 2's <c>rel/atomic</c> <c>set|clear</c> templates need for one field,
/// mirroring <see cref="BasicFieldSpec"/>'s role for slice 1's basic fields but for a single-valued
/// reference instead of a scalar/textual value.
/// </summary>
/// <param name="TargetInterface">The LibLCM interface a resolved target is cast to: <c>"I" +
/// DeclaringClass</c> — mechanical, matching <see cref="BasicFieldSpec"/>.</param>
/// <param name="RefInterface">The LibLCM interface the *referenced* object is cast to: <c>"I" +
/// Sig</c> — e.g. <c>IMoMorphType</c> for <c>MoForm.MorphType</c>.</param>
/// <param name="AccessorPropertyName">LibLCM's own suffix convention for a reference accessor
/// property: <c>{FieldName}RA</c> for <c>rel/atomic</c> — e.g. <c>MorphTypeRA</c>, not the manifest's
/// bare <c>MorphType</c> field name. Verified against the real <c>IMoForm</c> interface, not assumed
/// (the same lesson <see cref="ReferenceCollectionFieldSpec.AccessorPropertyName"/>'s RC/RS suffix
/// already encodes for the other two cardinalities).</param>
/// <param name="SnapshotFieldConstant">The <c>SnapshotFields</c> constant name:
/// <c>DeclaringClass + FieldName</c>, matching <see cref="BasicFieldSpec"/>'s collision-resistance
/// rationale.</param>
public sealed record ReferenceAtomicFieldSpec(
    string DeclaringClass,
    string FieldName,
    string Sig,
    string Group,
    string Construct,
    string SetKind,
    string ClearKind,
    string TargetInterface,
    string RefInterface,
    string AccessorPropertyName,
    string SnapshotFieldConstant);

/// <summary>
/// Every derived fact MOT-4 slice 2's <c>addRef|removeRef</c> templates need for one <c>rel/col</c> or
/// <c>rel/seq</c> field. The two cardinalities share one emitter because both LibLCM accessor shapes
/// (<c>ILcmReferenceCollection&lt;T&gt;</c>, <c>ILcmReferenceSequence&lt;T&gt;</c>) implement the
/// ordinary <c>ICollection&lt;T&gt;</c> — verified by reflection against the pinned <c>SIL.LCModel</c>
/// package — so <c>addRef</c>/<c>removeRef</c> lower identically regardless of which one a field uses.
/// <c>move</c> is deliberately not represented here: MOT-4 slice 2 defers it for <c>rel/seq</c> fields
/// (docs/plan-motif.md, MOT-4; see <c>LexEntryDialectLabelsEmitter</c>'s remarks for why).
/// </summary>
/// <param name="AccessorPropertyName">LibLCM's own suffix convention for a reference accessor property:
/// <c>{FieldName}RC</c> for <c>rel/col</c>, <c>{FieldName}RS</c> for <c>rel/seq</c> — e.g.
/// <c>DoNotPublishInRC</c>, <c>DialectLabelsRS</c>. Verified against the real <c>ILexEntry</c>
/// interface, not assumed.</param>
public sealed record ReferenceCollectionFieldSpec(
    string DeclaringClass,
    string FieldName,
    string Sig,
    FieldCard Card,
    string Group,
    string Construct,
    string AddRefKind,
    string RemoveRefKind,
    string TargetInterface,
    string RefInterface,
    string AccessorPropertyName,
    string SnapshotFieldConstant);
