using System.Collections.Generic;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Represents a single-valued reference slot's value — "which one entity, if any, does this slot
/// currently hold" — as the same alternatives-map shape every other field already uses in
/// <see cref="ObjectSnapshot.AlternativesFields"/> and
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect.Before"/>/<c>After</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes need exactly this: a <c>rel/atomic</c> field
/// (<c>MoForm.MorphType</c>, <c>set</c>/<c>clear</c>) and an <c>owning/atomic</c> slot's own occupant
/// identity (<c>LexEntry.LexemeForm</c>, <c>create</c>/<c>delete</c>) — both are "at most one
/// <see cref="CanonicalId"/>," the reference analogue of the scalar Boolean
/// <see cref="BooleanFieldAlternatives"/> already borrows this same map shape for. Reusing the map
/// (rather than widening <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/> to a discriminated value
/// type) keeps every digest/JSON-writing code path untouched — see that type's remarks for the fuller
/// argument, which applies unchanged here.
/// </para>
/// <para>
/// Purely an internal projection convention: never part of a wire payload schema, and never hashed on
/// its own.
/// </para>
/// </remarks>
public static class ReferenceFieldAlternatives
{
    /// <summary>The single map key a reference field's value is stored under.</summary>
    public const string Key = "ref";

    public static IReadOnlyDictionary<string, string> ToAlternatives(CanonicalId? id) =>
        id is { } value
            ? new Dictionary<string, string> { [Key] = value.Value }
            : new Dictionary<string, string>();

    /// <summary>
    /// Inverse of <see cref="ToAlternatives"/>. A map with no <see cref="Key"/> entry (the field is
    /// absent/cleared) reads back as <c>null</c> — the same "absent means no value" convention
    /// <see cref="ObjectSnapshot"/>'s remarks describe for an omitted alternative.
    /// </summary>
    public static CanonicalId? FromAlternatives(IReadOnlyDictionary<string, string> alternatives) =>
        alternatives.TryGetValue(Key, out var text) ? CanonicalId.Parse(text) : (CanonicalId?)null;
}
