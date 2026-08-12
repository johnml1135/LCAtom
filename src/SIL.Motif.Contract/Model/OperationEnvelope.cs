using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Contract.Model;

/// <summary>
/// One operation inside a Proposal's authoritative <c>operations</c> array — the Change Set
/// contract's "Document shape" and "Operation vocabulary".
/// </summary>
/// <remarks>
/// This is the envelope common to every operation kind. The full v1 vocabulary of kinds and their
/// per-kind value schemas is produced elsewhere; the contract kernel fixes the envelope shape, the
/// strict-closed-parsing discipline, and which parts of an operation are semantic (participate in
/// the intent digest) versus metadata (do not).
///
/// Semantic (participate in the intent digest): <see cref="OperationId"/>, <see cref="Kind"/>,
/// <see cref="EntityId"/>, <see cref="Target"/>, <see cref="After"/>, <see cref="Placement"/>,
/// <see cref="DependsOn"/>, <see cref="StorageIdOverride"/>.
///
/// Non-semantic (excluded from the intent digest): <see cref="Rationale"/>,
/// <see cref="Confidence"/>, <see cref="Provenance"/>, <see cref="Extensions"/>.
/// </remarks>
public sealed record OperationEnvelope
{
    public OperationEnvelope(
        CanonicalId operationId,
        string kind,
        CanonicalId? entityId = null,
        CanonicalId? target = null,
        JsonElement? after = null,
        Placement? placement = null,
        IReadOnlyList<OperationDependency>? dependsOn = null,
        CanonicalId? storageIdOverride = null,
        string? rationale = null,
        double? confidence = null,
        IReadOnlyList<JsonElement>? provenance = null,
        JsonElement? extensions = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Operation kind must not be empty.", nameof(kind));

        // Validates the 'group/...' shape; throws FormatException otherwise.
        _ = OperationKind.GetGroup(kind);

        OperationId = operationId;
        Kind = kind;
        EntityId = entityId;
        Target = target;
        After = after;
        Placement = placement;
        DependsOn = dependsOn ?? Array.Empty<OperationDependency>();
        StorageIdOverride = storageIdOverride;
        Rationale = rationale;
        Confidence = confidence;
        Provenance = provenance ?? Array.Empty<JsonElement>();
        Extensions = extensions;
    }

    /// <summary>Unique (within this Proposal) canonical id for this operation.</summary>
    public CanonicalId OperationId { get; }

    /// <summary>The operation kind, of the form <c>group/...</c>.</summary>
    public string Kind { get; }

    /// <summary>The canonical id of a newly proposed entity, for creating operations.</summary>
    public CanonicalId? EntityId { get; }

    /// <summary>The canonical id of the existing entity or item this operation targets.</summary>
    public CanonicalId? Target { get; }

    /// <summary>Desired values, references, clears, or other kind-specific payload.</summary>
    public JsonElement? After { get; }

    /// <summary>Identity-relative ordering anchor, for sequence operations.</summary>
    public Placement? Placement { get; }

    /// <summary>Explicit intra-Change-Set predecessor operations (see <see cref="OperationDependency"/>).</summary>
    public IReadOnlyList<OperationDependency> DependsOn { get; }

    /// <summary>
    /// An explicit authored override of the storage GUID this operation would otherwise realize.
    /// Changes storage realization, not canonical identity, but is authored intent and therefore
    /// hashed.
    /// </summary>
    public CanonicalId? StorageIdOverride { get; }

    /// <summary>Free-text authoring rationale. Review metadata; excluded from the intent digest.</summary>
    public string? Rationale { get; }

    /// <summary>Authoring confidence. Review metadata; excluded from the intent digest (may be a float).</summary>
    public double? Confidence { get; }

    /// <summary>Supporting evidence entries. Review metadata; excluded from the intent digest.</summary>
    public IReadOnlyList<JsonElement> Provenance { get; }

    /// <summary>Opaque, non-semantic tool data. Excluded from the intent digest.</summary>
    public JsonElement? Extensions { get; }
}
