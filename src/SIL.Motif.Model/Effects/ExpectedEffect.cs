using System.Collections.Generic;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Effects;

/// <summary>
/// One identity-keyed field transition: a delta of the Canonical Semantic Snapshot, scoped to the
/// change and read back from LibLCM — not a replay of the operation's intended writes. See
/// docs/change-set-contract.md, "Expected effects".
/// </summary>
/// <remarks>
/// Stage C's only field shape is a MultiUnicode alternatives map (ws tag -&gt; text), matching the
/// worked example in "Expected effects" exactly:
/// <code>
/// { "canonicalId": "...", "field": "lexical/sense/gloss",
///   "before": { "en": "run quickly on foot" }, "after": { "en": "move quickly on foot" } }
/// </code>
/// Both <see cref="Before"/> and <see cref="After"/> are always present (even when empty), because
/// rule 4 in "Expected effects" requires hashing the transition, not the destination: a changed
/// <c>before</c> on a touched field must move the digest even when <c>after</c> is unchanged.
/// </remarks>
public sealed record ExpectedEffect(
    CanonicalId CanonicalId,
    string Field,
    IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After);
