using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Represents an unordered reference collection's full membership as the same alternatives-map shape
/// every other field already uses in <see cref="ObjectSnapshot.AlternativesFields"/> and
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect.Before"/>/<c>After</c>.
/// </summary>
/// <remarks>
/// <para>
/// MOT-4 slice 2's <c>rel/col</c>/<c>rel/seq</c> <c>addRef</c>/<c>removeRef</c> fields
/// (<c>LexEntry.DialectLabels</c>, <c>.DoNotPublishIn</c>, <c>.DoNotShowMainEntryIn</c>) need "the set
/// of members this field currently holds," which has no natural single string value the way a scalar
/// or single reference does. A map keyed by each member's own <see cref="CanonicalId"/> — with the id
/// repeated as its own value, since <c>IReadOnlyDictionary&lt;string,string&gt;</c> needs some value
/// and the id is the only fact being recorded — reuses the identical shape
/// <see cref="ReferenceFieldAlternatives"/> and <see cref="BooleanFieldAlternatives"/> already borrow
/// for their own single-value cases, for the same reason: it costs nothing structurally and keeps every
/// digest/JSON-writing code path untouched.
/// </para>
/// <para>
/// Deliberately the field's <em>full</em> membership before and after, not a delta of the one member an
/// operation added or removed — the same "before/after is the whole current value" convention every
/// other generated field's snapshot already follows (see <c>MultiAlternativesFieldSnapshotting</c>,
/// <c>GlossFieldEmitter</c>'s <c>ReadGloss</c> helper).
/// </para>
/// <para>
/// Purely an internal projection convention: never part of a wire payload schema, and never hashed on
/// its own.
/// </para>
/// </remarks>
public static class ReferenceCollectionAlternatives
{
    public static IReadOnlyDictionary<string, string> ToAlternatives(IEnumerable<CanonicalId> members)
    {
        if (members is null) throw new ArgumentNullException(nameof(members));

        var alternatives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in members)
            alternatives[member.Value] = member.Value;

        return alternatives;
    }

    /// <summary>Inverse of <see cref="ToAlternatives"/>: every key parsed back into a <see cref="CanonicalId"/>.</summary>
    public static IReadOnlyList<CanonicalId> FromAlternatives(IReadOnlyDictionary<string, string> alternatives) =>
        alternatives.Keys.Select(CanonicalId.Parse).ToList();
}
