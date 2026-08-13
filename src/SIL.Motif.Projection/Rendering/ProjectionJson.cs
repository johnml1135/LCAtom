using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Projection.Rendering;

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
    };

    public static string Serialize<T>(T projection) => JsonSerializer.Serialize(projection, Options);
}
