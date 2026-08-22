using System;
using System.Collections.Generic;

namespace SIL.Motif.Contract.Worker;

/// <summary>Closed command and event discriminator registry for the worker control protocol.</summary>
public static class WorkerCommands
{
    public const string Handshake = "handshake";
    public const string GetStatus = "status";
    public const string GetJob = "job.get";
    public const string WaitForJob = "job.wait";
    public const string CancelJob = "job.cancel";
    public const string RetryJob = "job.retry";
    public const string RefreshBaseline = "baseline.refresh";
    public const string DryRun = "proposal.dry-run";
    public const string Apply = "proposal.apply";
    public const string RegisterLiveHost = "host.register";
    public const string UnregisterLiveHost = "host.unregister";
    public const string Reconcile = "host.reconcile";

    public const string BaselineRefreshRequested = "baseline.refresh.requested";
    public const string ApplyRequested = "apply.requested";
    public const string ReconciliationRequested = "reconciliation.requested";
    public const string CancellationRequested = "cancellation.requested";

    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        Handshake, GetStatus, GetJob, WaitForJob, CancelJob, RetryJob, RefreshBaseline, DryRun, Apply,
        RegisterLiveHost, UnregisterLiveHost, Reconcile,
    };

    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        BaselineRefreshRequested, ApplyRequested, ReconciliationRequested, CancellationRequested,
    };

    /// <summary>Every command accepted by the protocol.</summary>
    public static IReadOnlyCollection<string> All => KnownCommands;

    /// <summary>Every event accepted by the protocol.</summary>
    public static IReadOnlyCollection<string> Events => KnownEvents;

    /// <summary>Returns whether a command discriminator is known.</summary>
    public static bool IsKnown(string? command) => command is not null && KnownCommands.Contains(command);

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
