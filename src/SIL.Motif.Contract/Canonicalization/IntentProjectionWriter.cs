using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Contract.Canonicalization;

/// <summary>
/// Renders a <see cref="Proposal"/>'s intent projection as (pre-canonicalization) JSON
/// text: exactly the fields docs/change-set-contract.md, "Canonical JSON and hashes" lists as
/// included, with unordered collections presorted per
/// docs/adr/0007-cross-language-digest-determinism.md decision 2. The output still needs RFC 8785
/// canonicalization (member-name sort order, ES6 number formatting, escaping) — that is
/// <see cref="IntentDigest"/>'s job via the JCS reference implementation.
/// </summary>
/// <remarks>
/// Excluded fields are simply never written: <c>proposalId</c>, <c>rationale</c>,
/// <c>confidence</c>, <c>provenance</c>, and every <c>extensions</c> object. An absent optional
/// field is omitted entirely rather than written as JSON <c>null</c>, consistent with "omission
/// always means leave untouched" — the projection must not manufacture a distinction the Change
/// Set did not author.
/// </remarks>
public static class IntentProjectionWriter
{
    public static string WriteProjectionJson(Proposal proposal)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("contractVersions");
            foreach (var entry in proposal.ContractVersions)
                writer.WriteString(entry.Key, entry.Value);
            writer.WriteEndObject();

            if (proposal.Requires.Count > 0)
            {
                writer.WriteStartArray("requires");
                foreach (var id in proposal.Requires.OrderBy(x => x))
                    writer.WriteStringValue(id.Value);
                writer.WriteEndArray();
            }

            writer.WriteStartArray("operations");
            foreach (var operation in proposal.Operations)
                WriteOperation(writer, operation);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOperation(Utf8JsonWriter writer, OperationEnvelope operation)
    {
        writer.WriteStartObject();

        writer.WriteString("operationId", operation.OperationId.Value);
        writer.WriteString("kind", operation.Kind);

        if (operation.EntityId is { } entityId)
            writer.WriteString("entityId", entityId.Value);

        if (operation.Target is { } target)
            writer.WriteString("target", target.Value);

        if (operation.After is { } after)
        {
            writer.WritePropertyName("after");
            after.WriteTo(writer);
        }

        if (operation.Placement is { } placement)
        {
            writer.WriteStartObject("placement");
            if (placement.After is { } placementAfter)
                writer.WriteString("after", placementAfter.Value);
            if (placement.Before is { } placementBefore)
                writer.WriteString("before", placementBefore.Value);
            writer.WriteEndObject();
        }

        if (operation.DependsOn.Count > 0)
        {
            writer.WriteStartArray("dependsOn");
            foreach (var id in operation.DependsOn.Select(d => d.OperationId).OrderBy(x => x))
                writer.WriteStringValue(id.Value);
            writer.WriteEndArray();
        }

        if (operation.StorageIdOverride is { } storageIdOverride)
            writer.WriteString("storageIdOverride", storageIdOverride.Value);

        writer.WriteEndObject();
    }
}
