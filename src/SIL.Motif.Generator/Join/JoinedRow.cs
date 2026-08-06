using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Join;

/// <summary>
/// One successfully joined (Class, Field) pair: the structural facts from
/// <c>MasterLCModel.xml</c> — <see cref="Kind"/>, <see cref="Sig"/>, <see cref="Card"/> — carried
/// directly from the model rather than re-read off <see cref="Manifest"/>, because ADR 0022 makes the
/// model authoritative for structure and the manifest authoritative only for the policy columns
/// still living on <see cref="ManifestRow"/> (<c>Scope</c>, <c>Construct</c>, its <c>Group</c>
/// column, which is domain per ADR 0024).
/// </summary>
public sealed record JoinedRow(
    string DeclaringClass,
    string FieldName,
    FieldKind Kind,
    string Sig,
    FieldCard? Card,
    ManifestRow Manifest)
{
    public FieldKey Key => new(DeclaringClass, FieldName);
}
