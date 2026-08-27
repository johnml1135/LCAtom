using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Contract;

/// <summary>Creates the JSON options Motif's durable payloads and responses are written with.</summary>
/// <remarks>
/// Job input, result, and progress are stored as JSON in the paired database and read back by a different
/// process than wrote them, so the enum and property conventions have to be pinned in one place rather
/// than left to each caller's defaults.
/// </remarks>
public static class MotifJson
{
    /// <summary>Creates fresh options with Motif's established enum and property conventions.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JobStatusJsonConverter());
        options.Converters.Add(new JobFailureCategoryJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
