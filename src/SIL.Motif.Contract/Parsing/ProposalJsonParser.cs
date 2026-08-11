using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Contract.Parsing;

/// <summary>
/// Strict, closed JSON parser for a Proposal document. Unknown operation kinds and unknown
/// top-level properties (on the Proposal, an operation, or a placement) are hard errors — this
/// is a closed schema, not a permissive one that silently ignores what it does not recognize. Only
/// the explicit <c>extensions</c> object is an open escape hatch, and it never enters the intent
/// digest.
/// </summary>
public static class ProposalJsonParser
{
    private static readonly HashSet<string> ProposalProperties = new(StringComparer.Ordinal)
    {
        "contractVersions", "proposalId", "requires", "operations", "extensions",
    };

    private static readonly HashSet<string> OperationProperties = new(StringComparer.Ordinal)
    {
        "operationId", "kind", "entityId", "target", "after", "placement",
        "dependsOn", "storageIdOverride", "rationale", "confidence", "provenance", "extensions",
    };

    private static readonly HashSet<string> PlacementProperties = new(StringComparer.Ordinal)
    {
        "after", "before",
    };

    public static Proposal Parse(string json)
    {
        if (json is null)
            throw new ArgumentNullException(nameof(json));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new ContractParseException("A Proposal must be a JSON object.");

        RejectUnknownProperties(root, ProposalProperties, "the Proposal");

        var contractVersions = ParseContractVersions(root);
        var proposalId = ParseRequiredId(root, "proposalId");
        var requires = ParseRequires(root, proposalId);
        var operations = ParseOperations(root);

        ValidateContractVersionsCoverGroups(contractVersions, operations);

        var extensions = ParseExtensions(root, "the Proposal");

        return new Proposal(contractVersions, proposalId, requires, operations, extensions);
    }

    private static Dictionary<string, string> ParseContractVersions(JsonElement root)
    {
        if (!root.TryGetProperty("contractVersions", out var contractVersionsEl) ||
            contractVersionsEl.ValueKind != JsonValueKind.Object)
        {
            throw new ContractParseException("'contractVersions' is required and must be an object.");
        }

        var contractVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in contractVersionsEl.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new ContractParseException($"'contractVersions.{property.Name}' must be a string.");
            contractVersions[property.Name] = property.Value.GetString()!;
        }

        return contractVersions;
    }

    private static CanonicalId ParseRequiredId(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new ContractParseException($"'{propertyName}' is required and must be a string.");

        if (!CanonicalId.TryParse(idEl.GetString(), out var id, out var error))
            throw new ContractParseException($"'{propertyName}': {error}");

        return id;
    }

    private static List<CanonicalId> ParseRequires(JsonElement root, CanonicalId proposalId)
    {
        var requires = new List<CanonicalId>();

        if (!root.TryGetProperty("requires", out var requiresEl))
            return requires;

        if (requiresEl.ValueKind != JsonValueKind.Array)
            throw new ContractParseException("'requires' must be an array of Proposal canonical ids.");

        foreach (var item in requiresEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ContractParseException("Each 'requires' entry must be a string canonical id.");

            if (!CanonicalId.TryParse(item.GetString(), out var requiredId, out var error))
                throw new ContractParseException($"'requires' entry invalid: {error}");

            if (requiredId.Equals(proposalId))
                throw new ContractParseException("A Proposal cannot list its own proposalId in 'requires'.");

            if (requires.Contains(requiredId))
                throw new ContractParseException($"Duplicate 'requires' entry: '{requiredId.Value}'.");

            requires.Add(requiredId);
        }

        return requires;
    }

    private static List<OperationEnvelope> ParseOperations(JsonElement root)
    {
        if (!root.TryGetProperty("operations", out var operationsEl) || operationsEl.ValueKind != JsonValueKind.Array)
            throw new ContractParseException("'operations' is required and must be an array.");

        var operations = new List<OperationEnvelope>();
        var seenOperationIds = new HashSet<CanonicalId>();
        var seenEntityIds = new HashSet<CanonicalId>();

        foreach (var operationEl in operationsEl.EnumerateArray())
        {
            var operation = ParseOperation(operationEl);

            if (!seenOperationIds.Add(operation.OperationId))
            {
                throw new ContractParseException(
                    $"Duplicate operationId '{operation.OperationId.Value}' within a Proposal. " +
                    "Operation ids must be unique within one Proposal.");
            }

            if (operation.EntityId is { } entityId && !seenEntityIds.Add(entityId))
            {
                throw new ContractParseException(
                    $"entityId '{entityId.Value}' has more than one creator operation in this " +
                    "Proposal. A proposed entity id may have only one creator operation.");
            }

            operations.Add(operation);
        }

        return operations;
    }

    private static void ValidateContractVersionsCoverGroups(
        Dictionary<string, string> contractVersions,
        List<OperationEnvelope> operations)
    {
        var usedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
            usedGroups.Add(OperationKind.GetGroup(operation.Kind));

        foreach (var group in usedGroups)
        {
            if (!contractVersions.ContainsKey(group))
            {
                throw new ContractParseException(
                    $"'contractVersions' is missing an entry for group '{group}', which the " +
                    "operations array uses.");
            }
        }

        foreach (var declaredGroup in contractVersions.Keys)
        {
            if (!usedGroups.Contains(declaredGroup))
            {
                throw new ContractParseException(
                    $"'contractVersions' declares group '{declaredGroup}', which no operation uses. " +
                    "The map must name exactly the groups the operation array uses.");
            }
        }
    }

    private static OperationEnvelope ParseOperation(JsonElement operationEl)
    {
        if (operationEl.ValueKind != JsonValueKind.Object)
            throw new ContractParseException("Each operation must be a JSON object.");

        RejectUnknownProperties(operationEl, OperationProperties, "an operation");

        var operationId = ParseRequiredId(operationEl, "operationId");

        if (!operationEl.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
            throw new ContractParseException("'kind' is required and must be a string.");
        var kind = kindEl.GetString()!;

        try
        {
            _ = OperationKind.GetGroup(kind);
        }
        catch (FormatException ex)
        {
            throw new ContractParseException(ex.Message, ex);
        }

        if (!OperationKindRegistry.IsKnown(kind))
            throw new ContractParseException($"Unknown operation kind '{kind}'.");

        var entityId = ParseOptionalId(operationEl, "entityId");
        var target = ParseOptionalId(operationEl, "target");

        JsonElement? after = null;
        if (operationEl.TryGetProperty("after", out var afterEl))
        {
            JsonNumberPolicy.RejectFloats(afterEl, "after");
            after = afterEl.Clone();
        }

        Placement? placement = null;
        if (operationEl.TryGetProperty("placement", out var placementEl))
            placement = ParsePlacement(placementEl);

        var dependsOn = ParseDependsOn(operationEl);
        var storageIdOverride = ParseOptionalId(operationEl, "storageIdOverride");

        string? rationale = null;
        if (operationEl.TryGetProperty("rationale", out var rationaleEl))
        {
            if (rationaleEl.ValueKind != JsonValueKind.String)
                throw new ContractParseException("'rationale' must be a string.");
            rationale = rationaleEl.GetString();
        }

        double? confidence = null;
        if (operationEl.TryGetProperty("confidence", out var confidenceEl))
        {
            if (confidenceEl.ValueKind != JsonValueKind.Number)
                throw new ContractParseException("'confidence' must be a number.");
            confidence = confidenceEl.GetDouble();
        }

        var provenance = new List<JsonElement>();
        if (operationEl.TryGetProperty("provenance", out var provenanceEl))
        {
            if (provenanceEl.ValueKind != JsonValueKind.Array)
                throw new ContractParseException("'provenance' must be an array.");
            foreach (var item in provenanceEl.EnumerateArray())
                provenance.Add(item.Clone());
        }

        var extensions = ParseExtensions(operationEl, "an operation");

        return new OperationEnvelope(
            operationId,
            kind,
            entityId,
            target,
            after,
            placement,
            dependsOn,
            storageIdOverride,
            rationale,
            confidence,
            provenance,
            extensions);
    }

    private static CanonicalId? ParseOptionalId(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var idEl))
            return null;

        if (idEl.ValueKind != JsonValueKind.String)
            throw new ContractParseException($"'{propertyName}' must be a string.");

        if (!CanonicalId.TryParse(idEl.GetString(), out var id, out var error))
            throw new ContractParseException($"'{propertyName}': {error}");

        return id;
    }

    private static List<OperationDependency> ParseDependsOn(JsonElement operationEl)
    {
        var dependsOn = new List<OperationDependency>();

        if (!operationEl.TryGetProperty("dependsOn", out var dependsOnEl))
            return dependsOn;

        if (dependsOnEl.ValueKind != JsonValueKind.Array)
            throw new ContractParseException("'dependsOn' must be an array of operation ids.");

        var seen = new HashSet<CanonicalId>();
        foreach (var item in dependsOnEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ContractParseException("Each 'dependsOn' entry must be a string operation id.");

            if (!CanonicalId.TryParse(item.GetString(), out var dependencyId, out var error))
                throw new ContractParseException($"'dependsOn' entry invalid: {error}");

            if (!seen.Add(dependencyId))
                throw new ContractParseException($"Duplicate 'dependsOn' entry: '{dependencyId.Value}'.");

            dependsOn.Add(new OperationDependency(dependencyId));
        }

        return dependsOn;
    }

    private static Placement ParsePlacement(JsonElement placementEl)
    {
        if (placementEl.ValueKind != JsonValueKind.Object)
            throw new ContractParseException("'placement' must be a JSON object.");

        RejectUnknownProperties(placementEl, PlacementProperties, "a placement");

        var after = ParseOptionalId(placementEl, "after");
        var before = ParseOptionalId(placementEl, "before");

        try
        {
            return new Placement(after, before);
        }
        catch (ArgumentException ex)
        {
            throw new ContractParseException(ex.Message, ex);
        }
    }

    private static JsonElement? ParseExtensions(JsonElement obj, string context)
    {
        if (!obj.TryGetProperty("extensions", out var extensionsEl))
            return null;

        if (extensionsEl.ValueKind != JsonValueKind.Object)
            throw new ContractParseException($"'extensions' on {context} must be a JSON object.");

        return extensionsEl.Clone();
    }

    private static void RejectUnknownProperties(JsonElement obj, HashSet<string> allowed, string context)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new ContractParseException($"Unknown property '{property.Name}' on {context}.");
        }
    }
}
