using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// Shared building blocks for every generated <c>*Payload</c> parser's closed-schema checks: reject
/// an <c>after</c> that is not a JSON object, reject any property not on the kind's own allow-list,
/// and require a named property of a specific JSON kind. Factored out so twenty generated payload
/// parsers do not each reimplement the same four checks (MOT-4) — <c>SetGlossPayload</c>'s pre-MOT-4
/// body inlined the string-property half of this directly; this generalizes it and adds the
/// "unknown property" rejection the task's closed-schema requirement adds.
/// </summary>
internal static class ClosedPayloadParsing
{
    public static void RequireObject(JsonElement after, string kind)
    {
        if (after.ValueKind != JsonValueKind.Object)
            throw new ContractParseException($"'{kind}' operation 'after' must be a JSON object.");
    }

    public static void RejectUnknownProperties(JsonElement after, IReadOnlyCollection<string> allowed, string kind)
    {
        foreach (var property in after.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new ContractParseException($"'{kind}' operation: unknown 'after' property '{property.Name}'.");
        }
    }

    public static string GetRequiredString(JsonElement after, string propertyName, string kind)
    {
        if (!after.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new ContractParseException(
                $"'{kind}' operation 'after.{propertyName}' is required and must be a string.");
        }

        return element.GetString()!;
    }

    public static bool GetRequiredBoolean(JsonElement after, string propertyName, string kind)
    {
        if (!after.TryGetProperty(propertyName, out var element) ||
            (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            throw new ContractParseException(
                $"'{kind}' operation 'after.{propertyName}' is required and must be a boolean.");
        }

        return element.GetBoolean();
    }

    /// <summary>
    /// MOT-4 slice 2: every reference-shaped payload (a <c>rel/atomic</c> <c>set</c>, an
    /// <c>addRef</c>/<c>removeRef</c> member, <c>LexEntry.LexemeForm</c>'s <c>morphType</c>) names a
    /// target by <see cref="CanonicalId"/> rather than a raw string, so this generalizes
    /// <see cref="GetRequiredString"/> with the one extra "does this parse as a canonical id" check.
    /// </summary>
    public static CanonicalId GetRequiredCanonicalId(JsonElement after, string propertyName, string kind)
    {
        var text = GetRequiredString(after, propertyName, kind);
        if (!CanonicalId.TryParse(text, out var id))
        {
            throw new ContractParseException(
                $"'{kind}' operation 'after.{propertyName}' ('{text}') is not a valid canonical id.");
        }

        return id;
    }
}
