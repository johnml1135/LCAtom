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
    private const string LegacyWorkspaceKey = "legacy-live-host";
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IProjectHostRegistry? _hosts;
    private readonly Dictionary<string, PendingEvent> _pending = new(StringComparer.Ordinal);
    private ProjectHostRegistration? _legacy;
    private bool _disposed;
    private int _activeWrites;
    private Task? _disposeTask;
    private TaskCompletionSource<bool>? _writesQuiesced;

    /// <summary>Creates a sink with the compatibility registration used by older clients.</summary>
    public WorkerEventSink() { }

    internal WorkerEventSink(IProjectHostRegistry hosts) => _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));

    /// <summary>Registers a compatibility live-host stream without a project route.</summary>
    public void RegisterLiveHost(Stream stream, int protocolVersion)
        => RegisterLiveHost(stream, protocolVersion, null);

    internal void RegisterLiveHost(Stream stream, int protocolVersion, SemaphoreSlim? sharedWriteGate)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (protocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_legacy is not null) throw new InvalidOperationException("A live host is already registered.");
            _legacy = new ProjectHostRegistration(Guid.NewGuid().ToString("N"), string.Empty,
                protocolVersion, stream, sharedWriteGate ?? _writeGate);
        }
    }

    internal void RegisterLiveHost(ProjectLocator project, string connectionId, string hostSessionId,
        Stream stream, int protocolVersion, SemaphoreSlim writeGate)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (writeGate is null) throw new ArgumentNullException(nameof(writeGate));
        _hosts?.Register(project, new ProjectHostRegistration(connectionId, hostSessionId,
            protocolVersion, stream, writeGate));
    }

    /// <summary>Clears a compatibility registration and faults its pending events.</summary>
    public void UnregisterLiveHost(Stream stream)
    {
        lock (_gate)
        {
            if (_disposed || _legacy is null || !ReferenceEquals(_legacy.Stream, stream)) return;
            _legacy = null;
            FaultPending(LegacyWorkspaceKey, new IOException("The live host disconnected."));
        }
    }

    internal void UnregisterLiveHost(ProjectLocator project, string connectionId)
    {
        if (_hosts is null) return;
        var key = ProjectWorkspaceKey.Compute(project);
        _hosts.Unregister(project, connectionId);
        lock (_gate) FaultPending(key, new IOException("The live host disconnected."));
    }

    internal bool HasPendingEvents(string workspaceKey)
    {
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
        if (_hosts is null || !_hosts.TryGet(project, out var registration))
            throw new InvalidOperationException("No live host is registered for this project.");
        return SendAsyncCore(key, registration, eventName, payload, cancellationToken);
    }

    /// <summary>Sends an event using the compatibility live-host registration.</summary>
    public Task<WorkerEventResultEnvelope> SendAsync(string eventName, JsonElement payload,
        CancellationToken cancellationToken)
    {
        ProjectHostRegistration registration;
        lock (_gate)
        {
            ThrowIfDisposed();
            registration = _legacy ?? throw new InvalidOperationException("No live host is registered.");
        }
        return SendAsyncCore(LegacyWorkspaceKey, registration, eventName, payload, cancellationToken);
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

    public Task<WorkerEventResultEnvelope> RequestBaselineRefreshAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.BaselineRefreshRequested, payload, cancellationToken);

    public Task<WorkerEventResultEnvelope> RequestApplyAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.ApplyRequested, payload, cancellationToken);

    public Task<WorkerEventResultEnvelope> RequestReconciliationAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.ReconciliationRequested, payload, cancellationToken);

    public Task<WorkerEventResultEnvelope> RequestCancellationAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.CancellationRequested, payload, cancellationToken);

    /// <summary>Records one event result by its correlation identifier.</summary>
    public void AcceptResult(WorkerEventResultEnvelope result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!_pending.Remove(result.EventId, out var pending))
                throw new InvalidOperationException("The event result identifier is unknown or duplicated.");
            if (result.ProtocolVersion != pending.ProtocolVersion)
                throw new InvalidOperationException("The event result protocol is not negotiated.");
            pending.Completion.TrySetResult(result);
        }
    }

    /// <summary>Faults pending events and releases the serialized writer.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Waits for active event writes before releasing the sink-owned semaphore.</summary>
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _legacy = null;
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
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pending.Count >= PendingCorrelationCapacity)
                throw new InvalidOperationException("The live-host event correlation bound is full.");
            envelope = new WorkerEventEnvelope(Guid.NewGuid().ToString("N"), eventName, payload,
                registration.ProtocolVersion);
            completion = new TaskCompletionSource<WorkerEventResultEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(envelope.EventId, new PendingEvent(workspaceKey, registration.ProtocolVersion, completion));
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
            lock (_gate)
            {
                _pending.Remove(envelope.EventId);
                _activeWrites--;
                if (_disposed && _activeWrites == 0) _writesQuiesced?.TrySetResult(true);
            }
        }
    }

    private void FaultPending(string? workspaceKey, Exception exception)
    {
        foreach (var pair in new List<KeyValuePair<string, PendingEvent>>(_pending))
        {
            if (workspaceKey is not null && !StringComparer.Ordinal.Equals(pair.Value.WorkspaceKey, workspaceKey)) continue;
            _pending.Remove(pair.Key);
            pair.Value.Completion.TrySetException(exception);
        }
    }

    private async Task DisposeAfterWritesAsync(Task writesQuiesced)
    {
        await writesQuiesced.ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerEventSink));
    }

    private sealed record PendingEvent(string WorkspaceKey, int ProtocolVersion,
        TaskCompletionSource<WorkerEventResultEnvelope> Completion);
}
