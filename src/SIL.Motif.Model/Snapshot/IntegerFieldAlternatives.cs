using System.Collections.Generic;
using System.Globalization;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Represents a <c>basic Integer</c> field's value as the same ws-tag -&gt; text alternatives shape
/// <see cref="BooleanFieldAlternatives"/> already established for a scalar Boolean field — see that
/// type's remarks for why a map, not a new shape on <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/>.
/// </summary>
/// <remarks>
/// <para>
/// First user: <c>WfiWordform.SpellingStatus</c> — a small closed-range enum
/// (<c>0=Undecided;1=Correct;2=Incorrect</c>) that LibLCM stores as a plain <c>Integer</c>. The map
/// shape costs nothing here for the same reason it cost nothing for Boolean: one well-known key,
/// <see cref="Key"/>, and every digest/JSON-writing code path that already handles
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffect"/>'s <c>IReadOnlyDictionary&lt;string, string&gt;</c>
/// shape stays untouched.
/// </para>
/// <para>
/// This is purely an internal projection convention — never part of the wire payload schema (the
/// generated <c>*Payload</c> parser uses its own <c>"value"</c>-keyed JSON object for the operation's
/// <c>after</c>, and additionally range-checks it against the field's enum members) and never hashed
/// on its own.
/// </para>
/// </remarks>
public static class IntegerFieldAlternatives
{
    /// <summary>The single map key an Integer field's value is stored under.</summary>
    public const string Key = "value";

    public static IReadOnlyDictionary<string, string> ToAlternatives(int value) =>
        new Dictionary<string, string> { [Key] = value.ToString(CultureInfo.InvariantCulture) };

    /// <summary>
    /// Inverse of <see cref="ToAlternatives"/>. A map with no <see cref="Key"/> entry (e.g. an
    /// <see cref="ObjectSnapshot.Empty"/> projection) reads back as <c>0</c> — the CLR default for
    /// <c>int</c>, the same "absent means the type's default" convention
    /// <see cref="BooleanFieldAlternatives.FromAlternatives"/> uses for <c>false</c>.
    /// </summary>
    public static int FromAlternatives(IReadOnlyDictionary<string, string> alternatives) =>
        alternatives.TryGetValue(Key, out var text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
