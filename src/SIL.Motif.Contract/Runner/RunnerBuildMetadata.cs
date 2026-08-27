using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Runner;

/// <summary>What one installed job runner build declares about itself: its version and the newest
/// database schema generation it can open.</summary>
/// <remarks>
/// Compatibility is grounded on the schema rather than on a protocol because there is no protocol: a
/// runner and a command coordinate through the paired database, so the only way a mismatched pair can
/// hurt a user is a database one of them cannot open. Selecting a runner therefore asks whether it
/// reaches the generation the database is at, not whether two version ranges overlap.
/// </remarks>
public sealed record RunnerBuildMetadata
{
    [JsonConstructor]
    public RunnerBuildMetadata(string productVersion, int supportedSchema)
    {
        if (string.IsNullOrWhiteSpace(productVersion))
            throw new ArgumentException("A product version is required.", nameof(productVersion));
        if (!Version.TryParse(productVersion, out _))
            throw new ArgumentException("The product version is not a version.", nameof(productVersion));
        if (supportedSchema < 1)
            throw new ArgumentOutOfRangeException(nameof(supportedSchema),
                "A runner must support at least the first schema generation.");
        ProductVersion = productVersion;
        SupportedSchema = supportedSchema;
    }

    [JsonPropertyOrder(0)] public string ProductVersion { get; }

    /// <summary>The newest schema generation this build can open; older generations it migrates.</summary>
    [JsonPropertyOrder(1)] public int SupportedSchema { get; }

    /// <summary>Renders the exact bytes an installed runner publishes beside its executable.</summary>
    public string ToCanonicalJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Reads published runner metadata, refusing anything it cannot fully validate.</summary>
    public static RunnerBuildMetadata Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Runner metadata is required.", nameof(json));
        RunnerBuildMetadata? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RunnerBuildMetadata>(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The runner metadata is not valid JSON.", nameof(json), exception);
        }
        return parsed ?? throw new ArgumentException("The runner metadata was empty.", nameof(json));
    }
}
