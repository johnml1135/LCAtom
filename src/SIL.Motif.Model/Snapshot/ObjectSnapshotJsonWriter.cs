using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Model.Snapshot;

/// <summary>
/// Renders one or more <see cref="ObjectSnapshot"/>s as (pre-canonicalization) JSON text, keyed by
/// canonical id — the Change Set contract's "Canonical Semantic Snapshot": "an inspectable
/// deterministic projection ... rendered [to] its RFC 8785 canonical JSON form."
/// </summary>
/// <remarks>
/// Object member order here does not need to be pre-sorted: <see cref="SIL.Motif.Contract.Canonicalization.CanonicalJson"/>
/// (the RFC 8785 JCS implementation) normalizes JSON *object* member order on its own. Only JSON
/// *arrays* need explicit deterministic pre-sorting, because JCS preserves array element order —
/// this writer contains none, so no presort is needed here (contrast
/// <see cref="SIL.Motif.Model.Effects.ExpectedEffectSetJsonWriter"/>, which writes an array of
/// effects and does presort).
/// </remarks>
public static class ObjectSnapshotJsonWriter
{
    /// <summary>Writes a single snapshot as <c>{ "canonicalId": ..., "fields": { ... } }</c>.</summary>
    public static string WriteJson(ObjectSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("canonicalId", snapshot.CanonicalId.Value);
            writer.WritePropertyName("fields");
            WriteFields(writer, snapshot);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes several snapshots as one JSON object whose member names are the canonical ids
    /// themselves — the "keyed by canonical id" projection referenced above.
    /// </summary>
    public static string WriteJson(IEnumerable<ObjectSnapshot> snapshots)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var snapshot in snapshots)
            {
                writer.WritePropertyName(snapshot.CanonicalId.Value);
                WriteFields(writer, snapshot);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFields(Utf8JsonWriter writer, ObjectSnapshot snapshot)
    {
        writer.WriteStartObject();
        foreach (var field in snapshot.AlternativesFields)
        {
            writer.WriteStartObject(field.Key);
            foreach (var alternative in field.Value)
                writer.WriteString(alternative.Key, alternative.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
