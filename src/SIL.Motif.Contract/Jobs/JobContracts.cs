using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Jobs;

/// <summary>Durable states for worker-owned long-running jobs.</summary>
[JsonConverter(typeof(JobStatusJsonConverter))]
public enum JobStatus
{
    Queued,
    WaitingForBaseline,
    WaitingForProjectHost,
    Running,
    Completed,
    CompletedDryRunOnly,
    CompletedWithAssessmentFailure,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>Canonical JSON spellings for the closed job status set.</summary>
public static class JobStatusJson
{
    public static string ToWire(JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.WaitingForBaseline => "waiting-for-baseline",
        JobStatus.WaitingForProjectHost => "waiting-for-project-host",
        JobStatus.Running => "running",
        JobStatus.Completed => "completed",
        JobStatus.CompletedDryRunOnly => "completed-dry-run-only",
        JobStatus.CompletedWithAssessmentFailure => "completed-with-assessment-failure",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static JobStatus Parse(string value) => value switch
    {
        "queued" => JobStatus.Queued,
        "waiting-for-baseline" => JobStatus.WaitingForBaseline,
        "waiting-for-project-host" => JobStatus.WaitingForProjectHost,
        "running" => JobStatus.Running,
        "completed" => JobStatus.Completed,
        "completed-dry-run-only" => JobStatus.CompletedDryRunOnly,
        "completed-with-assessment-failure" => JobStatus.CompletedWithAssessmentFailure,
        "failed" => JobStatus.Failed,
        "cancelled" => JobStatus.Cancelled,
        "interrupted" => JobStatus.Interrupted,
        _ => throw new JsonException($"Unknown job status '{value}'.")
    };
}

/// <summary>Serializes the status contract as closed JSON strings, never enum integers.</summary>
public sealed class JobStatusJsonConverter : JsonConverter<JobStatus>
{
    public override JobStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Job status must be a JSON string.");
        return JobStatusJson.Parse(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, JobStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JobStatusJson.ToWire(value));
}

/// <summary>Limits structured job documents to one worker control frame.</summary>
public static class JobJson
{
    /// <summary>The UTF-8 bound for input, result, and progress JSON documents.</summary>
    public const int MaxStructuredJsonUtf8Bytes = 1024 * 1024;

    public static void ValidateStructured(string json, string fieldName)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        if (Encoding.UTF8.GetByteCount(json) > MaxStructuredJsonUtf8Bytes)
            throw new ArgumentException($"{fieldName} exceeds the structured JSON size limit.", fieldName);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new ArgumentException($"{fieldName} must be a JSON object or array.", fieldName);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{fieldName} is not valid structured JSON.", fieldName, exception);
        }
    }
}

/// <summary>One durable worker attempt and its bounded workflow documents.</summary>
public sealed record JobRecord(
    string JobId,
    string ProjectKey,
    string Kind,
    JobStatus Status,
    int Attempt,
    string InputJson,
    string? ResultJson,
    string CreatedUtc,
    string UpdatedUtc,
    string? ProgressJson = null,
    string? LineageId = null,
    bool CancellationRequested = false,
    long Version = 0,
    bool DryRunPublished = false)
{
    public string LogicalJobId => LineageId ?? JobId;
}

/// <summary>Injectable UTC clock used to make durable transitions deterministic.</summary>
public interface IJobClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>System clock used by production job repositories.</summary>
public sealed class SystemJobClock : IJobClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Names of kinds that have special lifecycle policy.</summary>
public static class JobKinds
{
    public const string Apply = "apply";
}

