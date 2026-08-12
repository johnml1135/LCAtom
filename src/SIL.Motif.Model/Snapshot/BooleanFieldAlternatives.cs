using System.Collections.Generic;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Represents a <c>basic Boolean</c> field's value as the same ws-tag -&gt; text alternatives shape
/// every MultiUnicode/MultiString field already uses in <see cref="ObjectSnapshot.AlternativesFields"/>
/// and <see cref="SIL.Motif.Model.Effects.ExpectedEffect.Before"/>/<c>After</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a map, not a new shape.</b> <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/>'s
/// <c>Before</c>/<c>After</c> properties are already shipped as
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c>, and existing tests
/// (<c>SnapshotAndEffectJsonTests</c>, <c>ProposalDryRunnerTests</c>) index them by writing-system
/// tag — <c>effect.Before[wsTag]</c> — so that type cannot change without breaking already-shipped
/// call sites a regeneration gate requires to keep compiling unmodified. Rather than widen
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/> to a discriminated value type, a scalar
/// Boolean borrows the identical map shape via one well-known key, <see cref="Key"/> — the
/// representation is "an alternatives map with exactly one entry," which costs nothing structurally
/// and keeps every digest/JSON-writing code path (<c>ExpectedEffectSetJsonWriter</c>,
/// <c>FootprintDigest</c>, <c>ObjectSnapshotJsonWriter</c>) untouched.
/// </para>
/// <para>
/// This is purely an internal projection convention — never part of the wire payload schema (see
/// the generated <c>*Payload</c> parsers, which use their own <c>"value"</c>-keyed JSON object for
/// the operation's <c>after</c>) and never hashed on its own; it only shapes what a generated
/// Boolean-field snapshotter/handler puts into <see cref="ObjectSnapshot"/> and
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/>.
/// </para>
/// </remarks>
public static class BooleanFieldAlternatives
{
    /// <summary>The single map key a Boolean field's value is stored under.</summary>
    public const string Key = "value";

    public static IReadOnlyDictionary<string, string> ToAlternatives(bool value) =>
        new Dictionary<string, string> { [Key] = value ? "true" : "false" };

    /// <summary>
    /// Inverse of <see cref="ToAlternatives"/>. A map with no <see cref="Key"/> entry (e.g. an
    /// <see cref="ObjectSnapshot.Empty"/> projection, or a field never populated) reads back as
    /// <c>false</c> — the same "absent means the type's default" convention
    /// <c>ObjectSnapshot</c>'s remarks describe for an omitted alternative.
    /// </summary>
    public static bool FromAlternatives(IReadOnlyDictionary<string, string> alternatives) =>
        alternatives.TryGetValue(Key, out var text) && text == "true";
}
