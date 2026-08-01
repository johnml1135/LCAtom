using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract.Canonicalization;

namespace SIL.Motif.Model.Effects;

/// <summary>
/// Computes the footprint digest: a hash of only the pre-mutation ("before") half of a set of
/// <see cref="ExpectedEffect"/>s — the live baseline an DryRun read, or Apply's pre-flight
/// re-read of that same baseline. See docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
/// decision 3, and <see cref="SIL.Motif.Model.DryRun.BoundDryRunAnchor.FootprintDigest"/>.
/// </summary>
/// <remarks>
/// Deliberately excludes <see cref="ExpectedEffect.After"/>: the footprint is "what the project
/// looked like right before this operation touched it", not the operation's own effect. Two
/// DryRuns of the *same* baseline against *different* proposed content therefore share one
/// footprint digest even though their effect digests differ — exactly the property Apply's drift
/// check needs (it recomputes the current "before" independent of what any particular Proposal
/// proposes, and compares only that against the anchor).
/// </remarks>
public static class FootprintDigest
{
    public static string Compute(IEnumerable<ExpectedEffect> effects)
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
                writer.WriteStartObject("before");
                foreach (var alternative in effect.Before)
                    writer.WriteString(alternative.Key, alternative.Value);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        var bytes = CanonicalJson.CanonicalizeToUtf8(json);
        return IntentDigest.Sha256Of(bytes);
    }
}
