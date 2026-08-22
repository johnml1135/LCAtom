using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Sends one correlated worker event at a time to the registered live host.</summary>
public sealed class WorkerEventSink : IDisposable, IAsyncDisposable
{
    internal const int PendingCorrelationCapacity = 128;
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<WorkerEventResultEnvelope>> _pending =
        new Dictionary<string, TaskCompletionSource<WorkerEventResultEnvelope>>(StringComparer.Ordinal);
    private Stream? _stream;
    private SemaphoreSlim? _sharedWriteGate;
    private int _protocolVersion;
    private bool _disposed;
    private int _activeWrites;
    private Task? _disposeTask;
    private TaskCompletionSource<bool>? _writesQuiesced;

    /// <summary>Registers the sole live-host control stream.</summary>
    public void RegisterLiveHost(Stream stream, int protocolVersion)
        => RegisterLiveHost(stream, protocolVersion, null);

    internal void RegisterLiveHost(Stream stream, int protocolVersion, SemaphoreSlim? sharedWriteGate)
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
            _sharedWriteGate = sharedWriteGate;
        }
    }

    /// <summary>Clears the registration and completes pending event requests exceptionally.</summary>
    public void UnregisterLiveHost(Stream stream)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!ReferenceEquals(_stream, stream))
                return;
            _stream = null;
            _sharedWriteGate = null;
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
            if (_pending.Count >= PendingCorrelationCapacity)
                throw new InvalidOperationException("The live-host event correlation bound is full.");
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
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Waits for active event writes before releasing the sink-owned semaphore.</summary>
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _stream = null;
                _sharedWriteGate = null;
                foreach (var pending in _pending.Values)
                    pending.TrySetException(new ObjectDisposedException(nameof(WorkerEventSink)));
                _pending.Clear();
                _writesQuiesced = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (_activeWrites == 0)
                    _writesQuiesced.TrySetResult(true);
                _disposeTask = DisposeAfterWritesAsync(_writesQuiesced.Task);
            }
            disposeTask = _disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private async Task<WorkerEventResultEnvelope> SendCoreAsync(
        WorkerEventEnvelope envelope,
        TaskCompletionSource<WorkerEventResultEnvelope> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            Stream stream;
            SemaphoreSlim writeGate;
            lock (_gate)
            {
                stream = _stream ?? throw new InvalidOperationException("No live host is registered.");
                writeGate = _sharedWriteGate ?? _writeGate;
                _activeWrites++;
            }
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WorkerWire.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(envelope.EventId);
                _activeWrites--;
                if (_disposed && _activeWrites == 0)
                    _writesQuiesced?.TrySetResult(true);
            }
        }
    }

    private async Task DisposeAfterWritesAsync(Task writesQuiesced)
    {
        await writesQuiesced.ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerEventSink));
    }
}
