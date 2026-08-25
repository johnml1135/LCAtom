using System;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Contract.Projects;

/// <summary>Freshness evidence reported by a host for its currently loaded project.</summary>
public sealed record LiveProjectObservation
{
    [JsonConstructor]
    public LiveProjectObservation(
        string hostSessionId,
        long editGeneration,
        bool hasUnsavedChanges,
        string savedSemanticDigest)
    {
        if (editGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(editGeneration), "Edit generation cannot be negative.");
        Sha256Value.RequireCanonical(savedSemanticDigest, nameof(savedSemanticDigest));

        HostSessionId = WorkerProtocolValidation.Identifier(hostSessionId, nameof(hostSessionId));
        EditGeneration = editGeneration;
        HasUnsavedChanges = hasUnsavedChanges;
        SavedSemanticDigest = savedSemanticDigest;
    }

    /// <summary>The opaque host session that observed the loaded project.</summary>
    public string HostSessionId { get; }

    /// <summary>The host's monotonically increasing edit generation.</summary>
    public long EditGeneration { get; }

    /// <summary>Whether the loaded project has edits not represented by its saved semantic digest.</summary>
    public bool HasUnsavedChanges { get; }

    /// <summary>The canonical digest of the project's last saved semantic state.</summary>
    public string SavedSemanticDigest { get; }
}
