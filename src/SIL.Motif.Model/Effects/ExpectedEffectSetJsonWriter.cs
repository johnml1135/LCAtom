using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SIL.Motif.Model.Effects;

/// <summary>
/// Renders a set of <see cref="ExpectedEffect"/>s as (pre-canonicalization) JSON text: the preimage
/// for the effect digest — the Change Set contract's "Expected effects" rule 4: "the effect
/// digest is the RFC 8785 hash of the full set of <c>(canonicalId, field, before, after)</c> deltas".
/// </summary>
/// <remarks>
/// The effect set is semantically a *set*, not a sequence, but it is rendered as a JSON array, and
/// RFC 8785 canonicalization preserves array order rather than sorting it — so this writer must
/// presort deterministically itself, the same discipline
/// <see cref="SIL.Motif.Contract.Canonicalization.IntentProjectionWriter"/> applies to
/// <c>requires</c>/<c>dependsOn</c>. Effects sort by canonical id first (via <c>CanonicalId</c>'s
/// own byte-ordinal <see cref="IComparable{T}"/>), then by field name (ordinal), which is stable
/// even when one canonical id has several changed fields.
/// </remarks>
public static class ExpectedEffectSetJsonWriter
{
    public static string WriteJson(IEnumerable<ExpectedEffect> effects)
    {
        var ordered = effects
            .OrderBy(e => e.CanonicalId)
            .ThenBy(e => e.Field, StringComparer.Ordinal)
            .ToList();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var effect in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("canonicalId", effect.CanonicalId.Value);
                writer.WriteString("field", effect.Field);
                WriteAlternatives(writer, "before", effect.Before);
                WriteAlternatives(writer, "after", effect.After);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAlternatives(
        Utf8JsonWriter writer, string propertyName, IReadOnlyDictionary<string, string> alternatives)
    {
        writer.WriteStartObject(propertyName);
        foreach (var alternative in alternatives)
            writer.WriteString(alternative.Key, alternative.Value);
        writer.WriteEndObject();
    }
}
