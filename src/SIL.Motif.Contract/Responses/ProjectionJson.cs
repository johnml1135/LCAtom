using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Responses;

/// <summary>
/// Serializes any projection record to JSON — the other renderer over the same object
/// <see cref="CommandTextRenderer"/> turns into text (ADR 0021 decision 2). Structured emission is
/// part of a report's definition of done, not a later flag, so every projection uses this rather
/// than a bespoke writer per surface.
/// </summary>
public static class ProjectionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Names not ordinals, domain converters first so JobStatus stays "queued" rather than "Queued".
        Converters =
        {
            new SIL.Motif.Contract.Jobs.JobStatusJsonConverter(),
            new SIL.Motif.Contract.Jobs.JobFailureCategoryJsonConverter(),
            new JsonStringEnumConverter(),
        },
    };

    public static string Serialize<T>(T projection) => JsonSerializer.Serialize(projection, Options);

    /// <summary>Reads a response back with the same conventions it was written with.</summary>
    /// <remarks>
    /// A consumer that reconstructs the options itself has to know the naming policy and that reasons are
    /// written as names — two facts nothing was telling it. Reading through here makes the round trip one
    /// module's business rather than every caller's.
    /// </remarks>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
