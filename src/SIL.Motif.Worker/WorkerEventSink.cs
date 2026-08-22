using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Sends one correlated worker event at a time to the registered live host.</summary>
public sealed class WorkerEventSink : IDisposable
{
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<WorkerEventResultEnvelope>> _pending =
        new Dictionary<string, TaskCompletionSource<WorkerEventResultEnvelope>>(StringComparer.Ordinal);
    private Stream? _stream;
    private int _protocolVersion;
    private bool _disposed;

    /// <summary>Registers the sole live-host control stream.</summary>
    public void RegisterLiveHost(Stream stream, int protocolVersion)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));
        if (protocolVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream is not null)
                throw new InvalidOperationException("A live host is already registered.");
            _stream = stream;
            _protocolVersion = protocolVersion;
        }
    }

    /// <summary>Clears the registration and completes pending event requests exceptionally.</summary>
    public void UnregisterLiveHost(Stream stream)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_stream, stream))
                return;
            _stream = null;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("The live host disconnected."));
            _pending.Clear();
        }
    }

    /// <summary>Sends a settled event and waits for exactly one matching result envelope.</summary>
    public Task<WorkerEventResultEnvelope> SendAsync(
        string eventName, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!WorkerCommands.IsKnownEvent(eventName))
            throw new ArgumentException("Unknown worker event discriminator.", nameof(eventName));
        WorkerEventEnvelope envelope;
        TaskCompletionSource<WorkerEventResultEnvelope> completion;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream is null)
                throw new InvalidOperationException("No live host is registered.");
            envelope = new WorkerEventEnvelope(Guid.NewGuid().ToString("N"), eventName, payload, _protocolVersion);
            completion = new TaskCompletionSource<WorkerEventResultEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(envelope.EventId, completion);
        }
        return SendCoreAsync(envelope, completion, cancellationToken);
    }

    /// <summary>Sends a baseline refresh event to the registered live host.</summary>
    public Task<WorkerEventResultEnvelope> RequestBaselineRefreshAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.BaselineRefreshRequested, payload, cancellationToken);

    /// <summary>Sends an apply event to the registered live host.</summary>
    public Task<WorkerEventResultEnvelope> RequestApplyAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.ApplyRequested, payload, cancellationToken);

    /// <summary>Sends a reconciliation event to the registered live host.</summary>
    public Task<WorkerEventResultEnvelope> RequestReconciliationAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.ReconciliationRequested, payload, cancellationToken);

    /// <summary>Sends a cancellation event to the registered live host.</summary>
    public Task<WorkerEventResultEnvelope> RequestCancellationAsync(JsonElement payload, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.CancellationRequested, payload, cancellationToken);

    /// <summary>Records one event result by its correlation identifier.</summary>
    public void AcceptResult(WorkerEventResultEnvelope result)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));
        lock (_gate)
        {
            if (result.ProtocolVersion != _protocolVersion)
                throw new InvalidOperationException("The event result protocol is not negotiated.");
            if (!_pending.Remove(result.EventId, out var completion))
                throw new InvalidOperationException("The event result identifier is unknown or duplicated.");
            completion.TrySetResult(result);
        }
    }

    /// <summary>Faults pending events and releases the serialized writer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stream = null;
            foreach (var pending in _pending.Values)
                pending.TrySetException(new ObjectDisposedException(nameof(WorkerEventSink)));
            _pending.Clear();
        }
        _writeGate.Dispose();
    }

    private async Task<WorkerEventResultEnvelope> SendCoreAsync(
        WorkerEventEnvelope envelope,
        TaskCompletionSource<WorkerEventResultEnvelope> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            Stream stream;
            lock (_gate)
                stream = _stream ?? throw new InvalidOperationException("No live host is registered.");
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WorkerWire.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _pending.Remove(envelope.EventId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerEventSink));
    }
}
