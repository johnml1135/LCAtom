using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SIL.Motif.Contract.Worker;

/// <summary>Closed command and event discriminator registry for the worker control protocol.</summary>
public static class WorkerCommands
{
    public const string Handshake = "handshake";
    public const string JobStatus = "job.status";
    public const string BaselineOffer = "baseline.offer";
    public const string BaselinePublish = "baseline.publish";
    public const string LiveHostRegister = "live-host.register";
    public const string LiveHostObservationUpdate = "live-host.observation.update";
    public const string LiveHostDisconnect = "live-host.disconnect";

    /// <summary>The publish-side metadata filename that binds an executable to its manifest.</summary>
    public const string BuildMetadataFileName = "worker.metadata.json";

    public const string BaselineRefreshRequested = "baseline.refresh.requested";
    public const string ApplyRequested = "apply.requested";
    public const string ReconciliationRequested = "reconciliation.requested";
    public const string CancellationRequested = "cancellation.requested";

    private static readonly IReadOnlyCollection<string> CommandValues =
        new ReadOnlyCollection<string>(new[]
        {
            Handshake, JobStatus, BaselineOffer, BaselinePublish, LiveHostRegister,
            LiveHostObservationUpdate, LiveHostDisconnect,
        });

    private static readonly IReadOnlyCollection<string> EventValues =
        new ReadOnlyCollection<string>(new[]
        {
            BaselineRefreshRequested, ApplyRequested, ReconciliationRequested, CancellationRequested,
        });

    private static readonly HashSet<string> KnownCommands = new(CommandValues, StringComparer.Ordinal);
    private static readonly HashSet<string> KnownEvents = new(EventValues, StringComparer.Ordinal);

    /// <summary>Every command accepted by the protocol.</summary>
    public static IReadOnlyCollection<string> All => CommandValues;

    /// <summary>Every event accepted by the protocol.</summary>
    public static IReadOnlyCollection<string> Events => EventValues;

    /// <summary>Returns whether a command discriminator is known.</summary>
    public static bool IsKnown(string? command) => command is not null && KnownCommands.Contains(command);

    /// <summary>Gets the capability required to dispatch a known command.</summary>
    public static string? RequiredCapability(string command) => command switch
    {
        Handshake => null,
        JobStatus => "jobs.v1",
        BaselineOffer or BaselinePublish => "baseline.v1",
        LiveHostRegister or LiveHostObservationUpdate or LiveHostDisconnect => "live-host.v1",
        _ => throw new ArgumentException("Unknown worker command discriminator.", nameof(command))
    };

    /// <summary>Returns whether an event discriminator is known.</summary>
    public static bool IsKnownEvent(string? @event) => @event is not null && KnownEvents.Contains(@event);

    internal static string RequireKnown(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !IsKnown(command))
            throw new ArgumentException("Unknown worker command discriminator.", nameof(command));
        return command;
    }

    internal static string RequireKnownEvent(string @event)
    {
        if (string.IsNullOrWhiteSpace(@event) || !IsKnownEvent(@event))
            throw new ArgumentException("Unknown worker event discriminator.", nameof(@event));
        return @event;
    }
}
