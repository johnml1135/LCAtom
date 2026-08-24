using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Projects;

/// <summary>Registers one connection as the authority for a loaded project and its current observation.</summary>
public sealed record LiveHostRegisterRequest
{
    [JsonConstructor]
    public LiveHostRegisterRequest(ProjectLocator project, LiveProjectObservation observation)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
    }

    public ProjectLocator Project { get; }
    public LiveProjectObservation Observation { get; }
}

/// <summary>Reports newer freshness evidence from the connection that owns a live-project epoch.</summary>
public sealed record LiveHostObservationUpdateRequest
{
    [JsonConstructor]
    public LiveHostObservationUpdateRequest(ProjectLocator project, LiveProjectObservation observation)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
    }

    public ProjectLocator Project { get; }
    public LiveProjectObservation Observation { get; }
}

/// <summary>Explicitly releases one connection's authority for a live-project epoch.</summary>
public sealed record LiveHostDisconnectRequest
{
    [JsonConstructor]
    public LiveHostDisconnectRequest(ProjectLocator project, string hostSessionId)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        HostSessionId = RequireIdentifier(hostSessionId, nameof(hostSessionId));
    }

    public ProjectLocator Project { get; }
    public string HostSessionId { get; }

    private static string RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("A bounded nonblank identifier is required.", parameterName);
        return value;
    }
}

/// <summary>Reports whether the addressed observation changed the current live-host authority.</summary>
public sealed record LiveHostObservationResponse
{
    [JsonConstructor]
    public LiveHostObservationResponse(string projectKey, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || projectKey!.Length > 256 || projectKey.Any(char.IsControl))
            throw new ArgumentException("A bounded project key is required.", nameof(projectKey));
        ProjectKey = projectKey;
        Accepted = accepted;
    }

    public string ProjectKey { get; }
    public bool Accepted { get; }
}
