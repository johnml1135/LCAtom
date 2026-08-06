namespace SIL.Motif.Generator.Model;

/// <summary>
/// One <c>&lt;basic&gt;</c>/<c>&lt;owning&gt;</c>/<c>&lt;rel&gt;</c> declaration from
/// <c>MasterLCModel.xml</c>, keyed for the join by (<see cref="DeclaringClass"/>,
/// <see cref="FieldName"/>) — the exact pair MOT-2 joins against the manifest's (Class, Field)
/// columns.
/// </summary>
/// <param name="DeclaringClass">The enclosing <c>&lt;class id="..."&gt;</c>, never the target of an
/// owning/rel field. ADR 0023 decision 2 names this "where the field is declared," distinct from
/// <paramref name="Sig"/>, which names what the field points at or contains.</param>
/// <param name="FieldName">The property name, e.g. <c>Gloss</c>.</param>
/// <param name="Kind">Which of the three XML elements declared it.</param>
/// <param name="Sig">Value type for <see cref="FieldKind.Basic"/>; destination class for
/// <see cref="FieldKind.Owning"/>/<see cref="FieldKind.Rel"/>.</param>
/// <param name="Card">Null for <see cref="FieldKind.Basic"/>; required for the other two.</param>
public sealed record ModelField(string DeclaringClass, string FieldName, FieldKind Kind, string Sig, FieldCard? Card);
