using System;
using System.Globalization;
using System.IO;

namespace SIL.Motif.Worker;

/// <summary>What one job runner process was told about where to work and how long to hold a job.</summary>
/// <remarks>
/// Read from the environment rather than the command line so that a parent which starts a runner does not
/// have to reconstruct its arguments, following the same convention as the other Motif overrides.
/// </remarks>
public sealed record RunnerOptions
{
    /// <summary>Relocates everything the runner owns. An operator needs this to run two installations.</summary>
    public const string RootVariable = "MOTIF_WORKER_ROOT";

    /// <summary>How long a claimed job is held before another runner may take it back.</summary>
    public const string LeaseVariable = "MOTIF_RUNNER_LEASE_SECONDS";

    /// <summary>
    /// Isolates this runner's owner mutex. <b>Test-only.</b>
    /// </summary>
    /// <remarks>
    /// It exists because a runner started by a test would otherwise contend for the same per-user mutex as
    /// the developer's real runner, and two concurrent test runs would contend with each other. No
    /// operator has a reason to set it: two real installations are separated by
    /// <see cref="RootVariable"/>, which is about where work happens rather than who may do it.
    /// </remarks>
    public const string NamespaceVariable = "MOTIF_RUNNER_NAMESPACE";

    public string Root { get; init; } = ResolveRoot();

    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    public string? OwnerNamespace { get; init; }

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Reads the environment and the one argument the runner still takes.</summary>
    public static RunnerOptions Read(string[] args) => new()
    {
        Root = ResolveRoot(),
        Lease = Seconds(Value(LeaseVariable)) ?? TimeSpan.FromMinutes(5),
        OwnerNamespace = Value(NamespaceVariable),
        IdleTimeout = IdleFrom(args) ?? TimeSpan.FromMinutes(5),
    };

    /// <summary>The worker root any process (runner or CLI) uses: <see cref="RootVariable"/>, or the per-user default.</summary>
    public static string ResolveRoot() => Value(RootVariable) ?? DefaultRoot();

    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIL", "Motif");

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static TimeSpan? Seconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static TimeSpan? IdleFrom(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], "--idle-ms", StringComparison.Ordinal) &&
                int.TryParse(args[index + 1], out var milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        return null;
    }
}
