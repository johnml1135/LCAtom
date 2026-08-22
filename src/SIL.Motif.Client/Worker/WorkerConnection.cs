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

/// <summary>Multiplexes correlated control requests and worker events over one pipe.</summary>
public sealed class WorkerConnection : IDisposable
{
    internal const int MaximumFrameBytes = 1024 * 1024;
    private readonly Stream _stream;
    private readonly WorkerHandshakeResult _negotiated;
    private readonly object _stateGate = new object();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _eventDispatchSignal = new SemaphoreSlim(0);
    private readonly Dictionary<string, TaskCompletionSource<WorkerEnvelope>> _pending =
        new Dictionary<string, TaskCompletionSource<WorkerEnvelope>>(StringComparer.Ordinal);
    private readonly HashSet<string> _events = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _seenEvents = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<WorkerEventEnvelope> _eventBuffer = new Queue<WorkerEventEnvelope>();
    private readonly Queue<WorkerEventEnvelope> _eventDispatchQueue = new Queue<WorkerEventEnvelope>();
    private readonly HashSet<string> _usedTransfers = new HashSet<string>(StringComparer.Ordinal);
    private bool _closed;
    private bool _eventDispatchStopped;
    private readonly Task _readLoop;
    private readonly Task _eventDispatchLoop;

    internal WorkerConnection(Stream stream, WorkerHandshakeResult negotiated)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _negotiated = negotiated ?? throw new ArgumentNullException(nameof(negotiated));
        _readLoop = ReadLoopAsync();
        _eventDispatchLoop = EventDispatchLoopAsync();
    }

    /// <summary>The protocol generation and capabilities selected during connection setup.</summary>
    public WorkerHandshakeResult Negotiated => _negotiated;

    /// <summary>Raised on a serialized dispatch queue when the worker sends an unsolicited event.</summary>
    public event EventHandler<WorkerEventEnvelope>? EventReceived
    {
        add
        {
            WorkerEventEnvelope[] buffered;
            lock (_stateGate)
            {
                _eventReceived += value;
                buffered = _eventBuffer.ToArray();
                _eventBuffer.Clear();
            }
            foreach (var item in buffered)
            {
                lock (_stateGate)
                    _eventDispatchQueue.Enqueue(item);
                _eventDispatchSignal.Release();
            }
        }
        remove
        {
            lock (_stateGate)
                _eventReceived -= value;
        }
    }

    private EventHandler<WorkerEventEnvelope>? _eventReceived;

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
        catch
        {
            lock (_stateGate)
                _pending.Remove(request.RequestId);
            Close(new IOException("The request could not be sent."));
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
        catch
        {
            Close(new IOException("The event result could not be sent."));
            throw;
        }
    }

    internal async Task SendBinaryCompletionAsync(BinaryTransferCompletion completion, CancellationToken cancellationToken)
    {
        try
        {
            await WriteControlAsync(completion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Close(new IOException("The binary transfer completion could not be sent."));
            throw;
        }
    }

    /// <summary>Closes the control pipe and fails all outstanding requests.</summary>
    public void Dispose() => Close(new ObjectDisposedException(nameof(WorkerConnection)));

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await WorkerFrame.ReadAsync(_stream, CancellationToken.None).ConfigureAwait(false);
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
                    RaiseEvent(eventEnvelope);
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

    private void RaiseEvent(WorkerEventEnvelope eventEnvelope)
    {
        var queued = false;
        lock (_stateGate)
        {
            if (_eventReceived is null)
            {
                _eventBuffer.Enqueue(eventEnvelope);
            }
            else
            {
                _eventDispatchQueue.Enqueue(eventEnvelope);
                queued = true;
            }
        }
        if (queued)
            _eventDispatchSignal.Release();
    }

    private async Task EventDispatchLoopAsync()
    {
        while (true)
        {
            await _eventDispatchSignal.WaitAsync().ConfigureAwait(false);
            WorkerEventEnvelope? eventEnvelope;
            EventHandler<WorkerEventEnvelope>? handler;
            lock (_stateGate)
            {
                if (_eventDispatchStopped)
                    return;
                if (_eventDispatchQueue.Count == 0)
                    continue;
                eventEnvelope = _eventDispatchQueue.Dequeue();
                handler = _eventReceived;
            }
            if (handler is null)
                continue;
            try
            {
                handler(this, eventEnvelope);
            }
            catch (Exception)
            {
            }
        }
    }

    private void Close(Exception reason)
    {
        TaskCompletionSource<WorkerEnvelope>[] pending;
        lock (_stateGate)
        {
            if (_closed)
                return;
            _closed = true;
            _eventDispatchStopped = true;
            pending = new List<TaskCompletionSource<WorkerEnvelope>>(_pending.Values).ToArray();
            _pending.Clear();
        }
        _stream.Dispose();
        _eventDispatchSignal.Release();
        foreach (var item in pending)
            item.TrySetException(reason);
    }

    private async Task WriteControlAsync(object value, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WorkerFrame.WriteAsync(_stream, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
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
