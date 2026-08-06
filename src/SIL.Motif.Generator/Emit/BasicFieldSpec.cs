namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Every derived fact MOT-4's basic <c>set|clear</c> templates need for one field, computed from a
/// <see cref="Join.JoinedRow"/> via the same derivations <c>MOT-2</c>/<c>ADR 0023</c>/<c>ADR 0024</c>
/// already established — nothing here is re-decided, only assembled.
/// </summary>
/// <param name="TargetInterface">The LibLCM interface a resolved target is cast to:
/// <c>"I" + DeclaringClass</c> — <c>ILexEntry</c>, <c>ILexSense</c>, <c>IMoForm</c>. Mechanical for
/// every class this slice touches; not asserted as a universal rule for the other ~90 in-scope
/// classes.</param>
/// <param name="SnapshotFieldConstant">The <c>SnapshotFields</c> constant name:
/// <c>DeclaringClass + FieldName</c> (e.g. <c>LexEntryCitationForm</c>) — collision-resistant should
/// a later slice add a same-named field on a different class (LexSense also has a <c>Restrictions</c>
/// field, out of this slice).</param>
public sealed record BasicFieldSpec(
    string DeclaringClass,
    string FieldName,
    string Sig,
    string Group,
    string Construct,
    string SetKind,
    string ClearKind,
    string TargetInterface,
    string SnapshotFieldConstant);
