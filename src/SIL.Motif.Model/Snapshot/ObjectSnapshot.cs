using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// A minimal deterministic semantic projection of one LibLCM object, keyed by its
/// <see cref="CanonicalId"/> rather than a raw storage GUID. See docs/change-set-contract.md,
/// "Canonical Semantic Snapshot".
/// </summary>
/// <remarks>
/// <para>
/// Stage C populated only what was then called <c>MultiUnicodeFields</c>, and only one field
/// (<see cref="SnapshotFields.LexSenseGloss"/>). MOT-4 widens the same map — renamed
/// <see cref="AlternativesFields"/> because it is no longer MultiUnicode-only — to every
/// <c>basic</c> <c>set|clear</c> field in its slice, covering three LibLCM sigs with one shape:
/// </para>
/// <list type="bullet">
/// <item>MultiUnicode/MultiString: the natural ws-tag -&gt; text alternatives map.</item>
/// <item>Boolean: a single-entry map under the well-known key
/// <see cref="BooleanFieldAlternatives.Key"/> (see that type's remarks) — a representational
/// choice, not a new shape, because <see cref="Effects.ExpectedEffect.Before"/>/<c>After</c> are
/// already shipped as <c>IReadOnlyDictionary&lt;string, string&gt;</c> and widening that type would
/// break already-compiled call sites (docs/plan-motif.md MOT-4's regeneration gate).</item>
/// </list>
/// <para>
/// The shape is deliberately a map keyed by field name so later stages can add more fields, and
/// later still other per-kind maps (references, sequences) alongside this one, without breaking
/// existing snapshot consumers — "additive-stable" per docs/change-set-contract.md, "Canonical
/// Semantic Snapshot".
/// </para>
/// <para>
/// Per that section, an alternative that is empty is indistinguishable from absent and is omitted
/// entirely, both from an individual field's alternatives map and (were every alternative of a
/// field empty) from <see cref="AlternativesFields"/> itself.
/// </para>
/// </remarks>
public sealed record ObjectSnapshot(
    CanonicalId CanonicalId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AlternativesFields)
{
    public static ObjectSnapshot Empty(CanonicalId canonicalId) =>
        new(canonicalId, new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
}
