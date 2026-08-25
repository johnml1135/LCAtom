using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Contract.Store;

/// <summary>
/// Requests that one CLI store location be taken into the worker-owned project database.
/// </summary>
/// <remarks>
/// The store directory is sent explicitly rather than derived by the worker, because it is the caller who
/// knows which location the user selected: a client defaulting to <c>.motif</c> beside its working directory
/// and a client given <c>--store</c> disagree, and the worker cannot tell which one the user meant.
/// </remarks>
public sealed record StoreCutoverRequest
{
    [JsonConstructor]
    public StoreCutoverRequest(ProjectLocator project, string storeDirectory)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        StoreDirectory = RequireStoreDirectory(storeDirectory);
    }

    [JsonPropertyOrder(0)] public ProjectLocator Project { get; }

    /// <summary>The exact store location the user selected.</summary>
    [JsonPropertyOrder(1)] public string StoreDirectory { get; }

    private static string RequireStoreDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A store directory is required.", nameof(value));
        if (value!.Any(char.IsControl))
            throw new ArgumentException("A store directory cannot contain control characters.", nameof(value));
        if (value.Length > 32767)
            throw new ArgumentException("The store directory exceeds its bound.", nameof(value));
        return value;
    }
}

/// <summary>Reports what one store cutover took, and what it still owes.</summary>
public sealed record StoreCutoverResponse
{
    [JsonConstructor]
    public StoreCutoverResponse(string projectKey, bool alreadyCutOver, int importedProposals,
        int importedLegacyRows, IReadOnlyList<string> archivedPaths, IReadOnlyList<string> unarchivedPaths)
    {
        ProjectKey = RequireNonBlank(projectKey, nameof(projectKey));
        AlreadyCutOver = alreadyCutOver;
        ImportedProposals = importedProposals;
        ImportedLegacyRows = importedLegacyRows;
        ArchivedPaths = archivedPaths ?? throw new ArgumentNullException(nameof(archivedPaths));
        UnarchivedPaths = unarchivedPaths ?? throw new ArgumentNullException(nameof(unarchivedPaths));
    }

    [JsonPropertyOrder(0)] public string ProjectKey { get; }

    /// <summary>Whether the ledger already recorded this store, making the import a no-op.</summary>
    [JsonPropertyOrder(1)] public bool AlreadyCutOver { get; }

    [JsonPropertyOrder(2)] public int ImportedProposals { get; }
    [JsonPropertyOrder(3)] public int ImportedLegacyRows { get; }
    [JsonPropertyOrder(4)] public IReadOnlyList<string> ArchivedPaths { get; }

    /// <summary>
    /// Sources that were imported but could not be moved aside. The database is authoritative regardless;
    /// these are files nothing reads any more, and a later cutover retries their archival without reimporting.
    /// </summary>
    [JsonPropertyOrder(5)]
    public IReadOnlyList<string> UnarchivedPaths { get; }

    private static string RequireNonBlank(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A nonblank value is required.", parameterName)
            : value!;
}
