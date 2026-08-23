using System;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Contract.Jobs;

/// <summary>Requests the durable status of one job in one identified project workspace.</summary>
public sealed record JobStatusRequest
{
    [JsonConstructor]
    public JobStatusRequest(ProjectLocator project, string jobId)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        JobId = RequireJobId(jobId);
    }

    [JsonPropertyOrder(0)] public ProjectLocator Project { get; }
    [JsonPropertyOrder(1)] public string JobId { get; }

    private static string RequireJobId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A valid job id is required.", nameof(value));
        if (value!.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new ArgumentException("A job id cannot contain control characters.", nameof(value));
        if (value.Length > 256)
            throw new ArgumentException("The job id exceeds its bound.", nameof(value));
        return value;
    }
}

/// <summary>Reports one durable job fact without implying a live project host or mutation.</summary>
public sealed record JobStatusResponse
{
    [JsonConstructor]
    public JobStatusResponse(string jobId, string projectKey, bool found, string? kind,
        JobStatus? status, int? attempt, string? updatedUtc, bool? cancellationRequested,
        JobFailureCategory? failureCategory, long? version)
    {
        JobId = RequireNonBlank(jobId, nameof(jobId));
        ProjectKey = RequireNonBlank(projectKey, nameof(projectKey));
        Found = found;
        Kind = kind;
        Status = status;
        Attempt = attempt;
        UpdatedUtc = updatedUtc;
        CancellationRequested = cancellationRequested;
        FailureCategory = failureCategory;
        Version = version;
    }

    [JsonPropertyOrder(0)] public string JobId { get; }
    [JsonPropertyOrder(1)] public string ProjectKey { get; }
    [JsonPropertyOrder(2)] public bool Found { get; }
    [JsonPropertyOrder(3)] public string? Kind { get; }
    [JsonPropertyOrder(4)] public JobStatus? Status { get; }
    [JsonPropertyOrder(5)] public int? Attempt { get; }
    [JsonPropertyOrder(6)] public string? UpdatedUtc { get; }
    [JsonPropertyOrder(7)] public bool? CancellationRequested { get; }
    [JsonPropertyOrder(8)] public JobFailureCategory? FailureCategory { get; }
    [JsonPropertyOrder(9)] public long? Version { get; }

    private static string RequireNonBlank(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A nonblank value is required.", parameterName)
            : value!;
}
