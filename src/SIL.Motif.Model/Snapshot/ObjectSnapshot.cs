using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// A minimal deterministic semantic projection of one LibLCM object, keyed by its
/// <see cref="CanonicalId"/> rather than a raw storage GUID — the Change Set contract's "Canonical
/// Semantic Snapshot".
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AlternativesFields"/> holds one alternatives map per in-scope <c>basic</c>
/// <c>set|clear</c> field, covering three LibLCM sigs with one shape:
/// </para>
/// <list type="bullet">
/// <item>MultiUnicode/MultiString: the natural ws-tag -&gt; text alternatives map.</item>
/// <item>Boolean: a single-entry map under the well-known key
/// <see cref="BooleanFieldAlternatives.Key"/> (see that type's remarks) — a representational
/// choice, not a new shape, because <see cref="Effects.ExpectedEffect.Before"/>/<c>After</c> are
/// already shipped as <c>IReadOnlyDictionary&lt;string, string&gt;</c> and widening that type would
/// break already-compiled call sites a regeneration gate requires to keep compiling unmodified.</item>
/// </list>
/// <para>
/// The shape is deliberately a map keyed by field name so later stages can add more fields, and
/// later still other per-kind maps (references, sequences) alongside this one, without breaking
/// existing snapshot consumers — the projection is "additive-stable": a member semantically
/// indistinguishable from absent is omitted entirely, so classifying a newly shipped LibLCM member
/// leaves the digest of an unpopulated model unchanged.
/// </para>
/// <para>
/// Consistent with that, an alternative that is empty is indistinguishable from absent and is
/// omitted entirely, both from an individual field's alternatives map and (were every alternative of
/// a field empty) from <see cref="AlternativesFields"/> itself.
/// </para>
/// </remarks>
public sealed record ObjectSnapshot(
    CanonicalId CanonicalId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AlternativesFields)
{
    public static ObjectSnapshot Empty(CanonicalId canonicalId) =>
        new(canonicalId, new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
}
