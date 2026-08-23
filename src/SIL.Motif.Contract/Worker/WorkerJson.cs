using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Contract.Worker;

/// <summary>Creates the independent JSON options used by worker control frames and typed payloads.</summary>
public static class WorkerJson
{
    /// <summary>Creates fresh options with the worker's established enum and property conventions.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JobStatusJsonConverter());
        options.Converters.Add(new JobFailureCategoryJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
