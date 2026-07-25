using System;
using System.Collections.Generic;
using SIL.LCAtom.Contract.Ids;

namespace SIL.LCAtom.Model.Snapshot;

/// <summary>
/// A minimal deterministic semantic projection of one LibLCM object, keyed by its
/// <see cref="CanonicalId"/> rather than a raw storage GUID. See docs/change-set-contract.md,
/// "Canonical Semantic Snapshot".
/// </summary>
/// <remarks>
/// Stage C populates only <see cref="MultiUnicodeFields"/>, and only the one field the in-scope
/// operation touches (<see cref="SnapshotFields.LexSenseGloss"/>). The shape is deliberately a map
/// keyed by field name so later stages can add more MultiUnicode fields, and later still other
/// per-kind maps (plain strings, references, sequences) alongside this one, without breaking
/// existing snapshot consumers — "additive-stable" per docs/change-set-contract.md, "Canonical
/// Semantic Snapshot".
///
/// Per that section, an alternative that is empty is indistinguishable from absent and is omitted
/// entirely, both from an individual field's alternatives map and (were every alternative of a
/// field empty) from <see cref="MultiUnicodeFields"/> itself.
/// </remarks>
public sealed record ObjectSnapshot(
    CanonicalId CanonicalId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MultiUnicodeFields)
{
    public static ObjectSnapshot Empty(CanonicalId canonicalId) =>
        new(canonicalId, new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));
}
