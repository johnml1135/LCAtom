using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Signals that the bounded connection event queue could not accept another worker event.</summary>
public sealed class WorkerEventQueueOverflowException : InvalidOperationException
{
    /// <summary>Creates an exception that reports the queue capacity that was exceeded.</summary>
    public WorkerEventQueueOverflowException(int capacity)
        : base("The worker event queue exceeded its capacity of " + capacity + " events.")
    {
        Capacity = capacity;
    }

    /// <summary>The maximum number of events retained by the connection.</summary>
    public int Capacity { get; }
}

/// <summary>Multiplexes correlated control requests and worker events over one pipe.</summary>
public sealed class WorkerConnection : IDisposable
{
    internal const int MaximumFrameBytes = 1024 * 1024;
    private const int EventQueueCapacity = 128;
    private readonly Stream _stream;
    private readonly WorkerHandshakeResult _negotiated;
    private readonly object _stateGate = new object();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _eventDispatchSignal = new SemaphoreSlim(0);
    private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _startup =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completionSource =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _writersQuiesced =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TaskCompletionSource<WorkerEnvelope>> _pending =
        new Dictionary<string, TaskCompletionSource<WorkerEnvelope>>(StringComparer.Ordinal);
    private readonly HashSet<string> _events = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _seenEvents = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<WorkerEventEnvelope> _eventQueue = new Queue<WorkerEventEnvelope>();
    private readonly HashSet<string> _usedTransfers = new HashSet<string>(StringComparer.Ordinal);
    private readonly Task _completion;
    private bool _closed;
    private bool _eventDispatchStopped;
    private int _activeWriters;
    private int _eventSignalCredits;
    private readonly Task _readLoop;
    private readonly Task _eventDispatchLoop;

    internal WorkerConnection(Stream stream, WorkerHandshakeResult negotiated)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _negotiated = negotiated ?? throw new ArgumentNullException(nameof(negotiated));
        _completion = _completionSource.Task;
        _completion.ContinueWith(
            task =>
            {
                var ignored = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _eventDispatchLoop = EventDispatchLoopAsync();
        _readLoop = ReadLoopAsync();
        _startup.TrySetResult(true);
    }

    /// <summary>The protocol generation and capabilities selected during connection setup.</summary>
    public WorkerHandshakeResult Negotiated => _negotiated;

    /// <summary>
    /// Raised in arrival order on the connection's background dispatch queue. Callers that own UI, LibLCM,
    /// or cache thread affinity must marshal work to the required context; this client does not capture one.
    /// Events are connection events, so queued events survive subscriber removal and are delivered to later subscribers.
    /// </summary>
    public event EventHandler<WorkerEventEnvelope>? EventReceived
    {
        add
        {
            var credits = 0;
            lock (_stateGate)
            {
                var wasInactive = _eventReceived is null;
                ThrowIfClosed();
                _eventReceived += value;
                if (wasInactive)
                {
                    credits = _eventQueue.Count - _eventSignalCredits;
                    if (credits > 0)
                        _eventSignalCredits += credits;
                }
            }
            if (credits > 0)
                _eventDispatchSignal.Release(credits);
        }
        remove
        {
            lock (_stateGate)
                _eventReceived -= value;
        }
    }

    private EventHandler<WorkerEventEnvelope>? _eventReceived;

    /// <summary>Completes when the connection is explicitly disposed or reaches a terminal peer or protocol state.</summary>
    public Task Completion => _completion;

    /// <summary>Sends a request and waits for exactly the response bearing its request identifier.</summary>
    public async Task<WorkerEnvelope> SendAsync(WorkerEnvelope request, CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.ProtocolVersion != _negotiated.ProtocolVersion)
            throw new InvalidOperationException("The request protocol does not match the negotiated protocol.");
        if (cancellationToken.IsCancellationRequested)
        {
            Close(new OperationCanceledException("The request was cancelled."));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var completion = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateGate)
        {
            ThrowIfClosed();
            if (_pending.ContainsKey(request.RequestId))
                throw new InvalidOperationException("A request with this identifier is already outstanding.");
            _pending.Add(request.RequestId, completion);
        }

        using var cancellation = cancellationToken.Register(() => CancelRequest(request.RequestId, completion));
        try
        {
            await WriteControlAsync(request, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_stateGate)
                _pending.Remove(request.RequestId);
            Close(exception);
            throw;
        }
    }

    /// <summary>Uploads one bounded binary offer and reports its digest after closing the data pipe.</summary>
    public Task UploadAsync(BinaryTransferOffer offer, Stream source, CancellationToken cancellationToken)
    {
        if (offer is null)
            throw new ArgumentNullException(nameof(offer));
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        lock (_stateGate)
        {
            if (_usedTransfers.Contains(offer.TransferId))
                throw new InvalidOperationException("A binary transfer offer may be used only once.");
            ThrowIfClosed();
            _usedTransfers.Add(offer.TransferId);
        }
        return BinaryTransferClient.UploadAsync(offer, source, cancellationToken, SendBinaryCompletionAsync);
    }

    /// <summary>Completes one known worker event exactly once.</summary>
    public async Task CompleteEventAsync(WorkerEventResultEnvelope result, CancellationToken cancellationToken)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));
        if (result.ProtocolVersion != _negotiated.ProtocolVersion)
            throw new InvalidOperationException("The event result protocol does not match the negotiated protocol.");
        lock (_stateGate)
        {
            if (!_events.Remove(result.EventId))
                throw new InvalidOperationException("The event identifier is unknown or already completed.");
            ThrowIfClosed();
        }
        try
        {
            await WriteControlAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Close(exception);
            throw;
        }
    }

    internal async Task SendBinaryCompletionAsync(
        BinaryTransferCompletion completion, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        try
        {
            await WriteControlAsync(completion, cancellationToken, expiresAt).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Close(exception);
            throw;
        }
    }

    /// <summary>Initiates a normal shutdown; await <see cref="Completion"/> to observe cleanup completion.</summary>
    public void Dispose() => Close(null);

    private async Task ReadLoopAsync()
    {
        try
        {
            await _startup.Task.ConfigureAwait(false);
            while (true)
            {
                var frame = await WorkerFrame.ReadAsync(_stream, _shutdownCancellation.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("EventId", out _))
                {
                    var eventEnvelope = WorkerFrame.Deserialize<WorkerEventEnvelope>(frame);
                    lock (_stateGate)
                    {
                        ThrowIfClosed();
                        if (!_seenEvents.Add(eventEnvelope.EventId) || !_events.Add(eventEnvelope.EventId))
                            throw new InvalidOperationException("The worker sent a duplicate event identifier.");
                    }
                    QueueEvent(eventEnvelope);
                    continue;
                }

                var response = WorkerFrame.Deserialize<WorkerEnvelope>(frame);
                TaskCompletionSource<WorkerEnvelope>? completion;
                lock (_stateGate)
                {
                    ThrowIfClosed();
                    if (!_pending.TryGetValue(response.RequestId, out completion))
                        throw new InvalidOperationException("The worker response identifier is not outstanding.");
                    _pending.Remove(response.RequestId);
                }
                completion.TrySetResult(response);
            }
        }
        catch (Exception exception)
        {
            Close(exception);
        }
    }

    private void CancelRequest(string requestId, TaskCompletionSource<WorkerEnvelope> completion)
    {
        lock (_stateGate)
            _pending.Remove(requestId);
        completion.TrySetCanceled();
        Close(new OperationCanceledException("The request was cancelled."));
    }

    private void QueueEvent(WorkerEventEnvelope eventEnvelope)
    {
        var signal = false;
        WorkerEventQueueOverflowException? overflow = null;
        lock (_stateGate)
        {
            ThrowIfClosed();
            if (_eventQueue.Count >= EventQueueCapacity)
            {
                overflow = new WorkerEventQueueOverflowException(EventQueueCapacity);
            }
            else
            {
                _eventQueue.Enqueue(eventEnvelope);
                signal = _eventReceived is not null;
                if (signal)
                    _eventSignalCredits++;
            }
        }
        if (overflow is not null)
        {
            Close(overflow);
            throw overflow;
        }
        if (signal)
            _eventDispatchSignal.Release();
    }

    private async Task EventDispatchLoopAsync()
    {
        await _startup.Task.ConfigureAwait(false);
        while (true)
        {
            await _eventDispatchSignal.WaitAsync().ConfigureAwait(false);
            WorkerEventEnvelope? eventEnvelope;
            EventHandler<WorkerEventEnvelope>? handler;
            lock (_stateGate)
            {
                if (_eventDispatchStopped)
                    return;
                if (_eventSignalCredits > 0)
                    _eventSignalCredits--;
                if (_eventQueue.Count == 0 || _eventReceived is null)
                    continue;
                eventEnvelope = _eventQueue.Dequeue();
                handler = _eventReceived;
            }
            var failures = new List<Exception>();
            foreach (EventHandler<WorkerEventEnvelope> subscriber in handler!.GetInvocationList())
            {
                try
                {
                    subscriber(this, eventEnvelope);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count != 0)
                Close(failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }

    private void Close(Exception? reason)
    {
        TaskCompletionSource<WorkerEnvelope>[] pending;
        TaskCompletionSource<bool>? writersToComplete = null;
        lock (_stateGate)
        {
            if (_closed)
                return;
            _closed = true;
            _eventDispatchStopped = true;
            pending = new List<TaskCompletionSource<WorkerEnvelope>>(_pending.Values).ToArray();
            _pending.Clear();
            if (_activeWriters == 0)
                writersToComplete = _writersQuiesced;
        }
        _shutdownCancellation.Cancel();
        _stream.Dispose();
        _eventDispatchSignal.Release();
        writersToComplete?.TrySetResult(true);
        foreach (var item in pending)
        {
            if (reason is null)
                item.TrySetException(new ObjectDisposedException(nameof(WorkerConnection)));
            else
                item.TrySetException(reason);
        }
        _ = CleanupAsync(reason);
    }

    private async Task WriteControlAsync(
        object value, CancellationToken cancellationToken, DateTimeOffset? deadline = null)
    {
        lock (_stateGate)
        {
            ThrowIfClosed();
            _activeWriters++;
        }
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (deadline.HasValue && DateTimeOffset.UtcNow >= deadline.Value)
                    throw new InvalidOperationException("The binary transfer offer expired before completion.");
                await WorkerFrame.WriteAsync(_stream, value, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            lock (_stateGate)
            {
                _activeWriters--;
                if (_closed && _activeWriters == 0)
                    _writersQuiesced.TrySetResult(true);
            }
        }
    }

    private async Task CleanupAsync(Exception? reason)
    {
        try
        {
            await Task.WhenAll(_readLoop, _eventDispatchLoop).ConfigureAwait(false);
            await _writersQuiesced.Task.ConfigureAwait(false);
            _writeGate.Dispose();
            _eventDispatchSignal.Dispose();
            _shutdownCancellation.Dispose();
            if (reason is null)
                _completionSource.TrySetResult(true);
            else
                _completionSource.TrySetException(reason);
        }
        catch (Exception exception)
        {
            _completionSource.TrySetException(exception);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(WorkerConnection));
    }
}

internal static class WorkerFrame
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static async Task ConnectAsync(NamedPipeClientStream stream, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(stream.Dispose);
        await stream.ConnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > WorkerConnection.MaximumFrameBytes)
            throw new InvalidDataException("The control frame exceeds the maximum size.");
        var prefix = new byte[4];
        WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, 0, prefix.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > WorkerConnection.MaximumFrameBytes)
            throw new InvalidDataException("The control frame length is outside the allowed bound.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static T Deserialize<T>(byte[] payload)
    {
        var value = JsonSerializer.Deserialize<T>(payload, JsonOptions);
        if (value is null)
            throw new InvalidDataException("The control frame was empty.");
        return value;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException("The control pipe closed mid-frame.");
            offset += count;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static int ReadInt32LittleEndian(byte[] bytes)
    {
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }

    private static void WriteInt32LittleEndian(byte[] bytes, int value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
    }
}
