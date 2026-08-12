namespace SIL.Motif.Generator.Model;

/// <summary>
/// Cardinality of an <c>&lt;owning&gt;</c>/<c>&lt;rel&gt;</c> field's <c>card</c> attribute.
/// <c>&lt;basic&gt;</c> fields carry no <c>card</c> at all, which is why
/// <see cref="ModelField.Card"/> is nullable rather than defaulting to a member of this enum.
/// </summary>
public enum FieldCard
{
    Atomic,
    Col,
    Seq,
}
