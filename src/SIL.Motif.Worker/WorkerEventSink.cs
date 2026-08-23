using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker;

/// <summary>Sends correlated worker events to the live host for the addressed project.</summary>
public sealed class WorkerEventSink : IDisposable, IAsyncDisposable
{
    internal const int PendingCorrelationCapacity = 128;
    private readonly object _gate = new();
    private readonly ProjectRuntimeActivity _activity;
    private readonly IProjectHostRegistry _hosts;
    private readonly bool _ownsHosts;
    private readonly Dictionary<string, PendingEvent> _pending = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _activeWrites;
    private Task? _disposeTask;
    private TaskCompletionSource<bool>? _writesQuiesced;

    /// <summary>Creates a sink with its own project-keyed host registry.</summary>
    public WorkerEventSink() : this(new ProjectHostRegistry(), true) { }

    internal WorkerEventSink(IProjectHostRegistry hosts) : this(hosts, false) { }

    private WorkerEventSink(IProjectHostRegistry hosts, bool ownsHosts)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _activity = hosts is ProjectHostRegistry projectHosts
            ? projectHosts.Activity
            : new ProjectRuntimeActivity();
        _ownsHosts = ownsHosts;
    }

    internal void RegisterLiveHost(ProjectLocator project, string connectionId, string hostSessionId,
        Stream stream, int protocolVersion, SemaphoreSlim writeGate)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (writeGate is null) throw new ArgumentNullException(nameof(writeGate));
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            ThrowIfDisposed();
            _hosts.Register(project, new ProjectHostRegistration(project, connectionId, hostSessionId,
                protocolVersion, stream, writeGate));
        }
    }

    internal void UnregisterLiveHost(ProjectLocator project, string connectionId, string hostSessionId)
    {
        var key = ProjectWorkspaceKey.Compute(project);
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            if (_disposed) return;
            if (_hosts.Unregister(project, connectionId, hostSessionId))
                FaultPending(key, new IOException("The live host disconnected."));
        }
    }

    internal bool HasPendingEvents(string workspaceKey)
    {
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            foreach (var pending in _pending.Values)
                if (StringComparer.Ordinal.Equals(pending.WorkspaceKey, workspaceKey)) return true;
            return false;
        }
    }

    /// <summary>Sends an event to the live host for a specific project.</summary>
    public Task<WorkerEventResultEnvelope> SendAsync(ProjectLocator project, string eventName,
        JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var key = ProjectWorkspaceKey.Compute(project);
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_hosts.TryGet(project, out var registration))
                throw new InvalidOperationException("No live host is registered for this project.");
            return SendAsyncCore(key, registration, eventName, payload, cancellationToken);
        }
    }

    /// <summary>Sends a baseline refresh event for a project.</summary>
    public Task<WorkerEventResultEnvelope> RequestBaselineRefreshAsync(ProjectLocator project,
        JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(project, WorkerCommands.BaselineRefreshRequested, payload, cancellationToken);

    /// <summary>Sends an apply event for a project.</summary>
    public Task<WorkerEventResultEnvelope> RequestApplyAsync(ProjectLocator project, JsonElement payload,
        CancellationToken cancellationToken) => SendAsync(project, WorkerCommands.ApplyRequested, payload, cancellationToken);

    /// <summary>Sends a reconciliation event for a project.</summary>
    public Task<WorkerEventResultEnvelope> RequestReconciliationAsync(ProjectLocator project, JsonElement payload,
        CancellationToken cancellationToken) => SendAsync(project, WorkerCommands.ReconciliationRequested, payload, cancellationToken);

    /// <summary>Sends a cancellation event for a project.</summary>
    public Task<WorkerEventResultEnvelope> RequestCancellationAsync(ProjectLocator project, JsonElement payload,
        CancellationToken cancellationToken) => SendAsync(project, WorkerCommands.CancellationRequested, payload, cancellationToken);

    /// <summary>Records one event result by its correlation identifier.</summary>
    public void AcceptResult(WorkerEventResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            if (!_pending.TryGetValue(result.EventId, out var pending))
                throw new InvalidOperationException("The event result identifier is unknown or duplicated.");
            if (result.ProtocolVersion != pending.ProtocolVersion)
                throw new InvalidOperationException("The event result protocol is not negotiated.");
            _pending.Remove(result.EventId);
            pending.ActivityLease.Dispose();
            pending.Completion.TrySetResult(result);
        }
    }

    /// <summary>Faults pending events and releases the serialized writer.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Waits for active event writes before releasing an owned host registry.</summary>
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                FaultPending(null, new ObjectDisposedException(nameof(WorkerEventSink)));
                _writesQuiesced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_activeWrites == 0) _writesQuiesced.TrySetResult(true);
                _disposeTask = DisposeAfterWritesAsync(_writesQuiesced.Task);
            }
            disposeTask = _disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private Task<WorkerEventResultEnvelope> SendAsyncCore(string workspaceKey,
        ProjectHostRegistration registration, string eventName, JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!WorkerCommands.IsKnownEvent(eventName))
            throw new ArgumentException("Unknown worker event discriminator.", nameof(eventName));
        WorkerEventEnvelope envelope;
        TaskCompletionSource<WorkerEventResultEnvelope> completion;
        lock (_activity.SyncRoot)
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pending.Count >= PendingCorrelationCapacity)
                throw new InvalidOperationException("The live-host event correlation bound is full.");
            envelope = new WorkerEventEnvelope(Guid.NewGuid().ToString("N"), eventName, payload,
                registration.ProtocolVersion);
            completion = new TaskCompletionSource<WorkerEventResultEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(envelope.EventId, new PendingEvent(workspaceKey, registration.ProtocolVersion,
                completion, _activity.AcquirePendingEvent(workspaceKey)));
        }
        return SendCoreAsync(envelope, registration, completion, cancellationToken);
    }

    private async Task<WorkerEventResultEnvelope> SendCoreAsync(WorkerEventEnvelope envelope,
        ProjectHostRegistration registration, TaskCompletionSource<WorkerEventResultEnvelope> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate) _activeWrites++;
            await registration.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await WorkerWire.WriteAsync(registration.Stream, envelope, cancellationToken).ConfigureAwait(false); }
            finally { registration.WriteGate.Release(); }
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            IDisposable? activityLease = null;
            lock (_gate)
            {
                if (_pending.Remove(envelope.EventId, out var pending))
                    activityLease = pending.ActivityLease;
                _activeWrites--;
                if (_disposed && _activeWrites == 0) _writesQuiesced?.TrySetResult(true);
            }
            activityLease?.Dispose();
        }
    }

    private void FaultPending(string? workspaceKey, Exception exception)
    {
        foreach (var pair in new List<KeyValuePair<string, PendingEvent>>(_pending))
        {
            if (workspaceKey is not null && !StringComparer.Ordinal.Equals(pair.Value.WorkspaceKey, workspaceKey)) continue;
            _pending.Remove(pair.Key);
            pair.Value.ActivityLease.Dispose();
            pair.Value.Completion.TrySetException(exception);
        }
    }

    private async Task DisposeAfterWritesAsync(Task writesQuiesced)
    {
        await writesQuiesced.ConfigureAwait(false);
        if (_ownsHosts) _hosts.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerEventSink));
    }

    private sealed record PendingEvent(string WorkspaceKey, int ProtocolVersion,
        TaskCompletionSource<WorkerEventResultEnvelope> Completion, IDisposable ActivityLease);
}
