using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Owns the user-scoped worker endpoint and validates control traffic before dispatch.</summary>
public sealed class WorkerServer : IAsyncDisposable, IWorkerWorkTracker
{
    private readonly string _userNamespace;
    private readonly string _userSid;
    private readonly Mutex _ownerMutex;
    private readonly WorkerHandshakeOffer _offer;
    private readonly IWorkerWorkTracker? _workTracker;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly ConcurrentDictionary<string, WorkerControlConnection> _connections =
        new ConcurrentDictionary<string, WorkerControlConnection>(StringComparer.Ordinal);
    private readonly object _gate = new object();
    private bool _started;
    private bool _disposed;
    private Task? _acceptLoop;

    /// <summary>Creates a server using the actual Windows user SID unless a test namespace is supplied.</summary>
    public WorkerServer(string? userNamespace = null, string productVersion = "0.0.0",
        IWorkerWorkTracker? workTracker = null)
    {
        _userSid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";
        _userNamespace = string.IsNullOrWhiteSpace(userNamespace) ? _userSid : userNamespace;
        EndpointName = GetControlPipeName(_userNamespace);
        OwnerName = GetOwnerMutexName(_userNamespace);
        _ownerMutex = new Mutex(false, OwnerName, out _);
        _offer = new WorkerHandshakeOffer(productVersion, new ProtocolRange(1, 1), Array.Empty<string>());
        _workTracker = workTracker;
    }

    /// <summary>The predictable control endpoint for clients in this user namespace.</summary>
    public string EndpointName { get; }

    /// <summary>The named mutex which serializes process ownership.</summary>
    public string OwnerName { get; }

    /// <summary>Whether this process currently owns the user-scoped worker mutex.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>Whether a queued, running, or waiting request is keeping the worker alive.</summary>
    public bool HasQueuedRunningOrWaitingWork => _workTracker?.HasQueuedRunningOrWaitingWork ?? false;

    /// <summary>Derives a stable control pipe name from an injected namespace or the current SID.</summary>
    public static string GetControlPipeName(string? userNamespace = null) =>
        "motif-worker-" + (string.IsNullOrWhiteSpace(userNamespace) ? CurrentSid() : userNamespace);

    /// <summary>Derives the process owner mutex name from an injected namespace or the current SID.</summary>
    public static string GetOwnerMutexName(string? userNamespace = null) =>
        "Global\\MotifWorkerOwner-" + (string.IsNullOrWhiteSpace(userNamespace) ? CurrentSid() : userNamespace);

    /// <summary>Creates the explicit ACL shared by control and binary worker pipes.</summary>
    public static PipeSecurity CreatePipeSecurity(string? userSid = null) =>
        PipeSecurityFactory.Create(userSid ?? CurrentSid());

    /// <summary>Attempts to become the one process owner without opening a database.</summary>
    public bool TryAcquireOwnership()
    {
        lock (_gate)
        {
            if (IsOwner)
                return true;
            try
            {
                IsOwner = _ownerMutex.WaitOne(TimeSpan.Zero);
                return IsOwner;
            }
            catch (AbandonedMutexException)
            {
                IsOwner = true;
                return true;
            }
        }
    }

    /// <summary>Starts accepting control connections after acquiring process ownership.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!TryAcquireOwnership())
                return Task.CompletedTask;
            if (_started)
                return _acceptLoop ?? Task.CompletedTask;
            _started = true;
            _acceptLoop = AcceptLoopAsync(cancellationToken);
            return _acceptLoop;
        }
    }

    /// <summary>Stops accepting connections and releases process ownership.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        foreach (var connection in _connections.Values)
            await connection.DisposeAsync().ConfigureAwait(false);
        if (IsOwner)
        {
            try { _ownerMutex.ReleaseMutex(); } catch { }
        }
        _ownerMutex.Dispose();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (!linked.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateControlPipe();
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            _ = HandleConnectionAsync(pipe, linked.Token);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                var frame = await WorkerWire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                var handshake = WorkerWire.Deserialize<WorkerHandshakeRequest>(frame);
                var negotiated = WorkerHandshake.Negotiate(handshake, _offer);
                await WorkerWire.WriteAsync(pipe, _offer, cancellationToken).ConfigureAwait(false);
                var connection = new WorkerControlConnection(pipe, negotiated.ProtocolVersion);
                _connections[connection.Id] = connection;
                await connection.RunAsync(cancellationToken).ConfigureAwait(false);
                _connections.TryRemove(connection.Id, out _);
            }
            catch
            {
                // A failed handshake closes before any command or database work is considered.
            }
        }
    }

    private NamedPipeServerStream CreateControlPipe() =>
        NamedPipeServerStreamAcl.Create(EndpointName, PipeDirection.InOut, 254, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, WorkerWire.MaximumFrameBytes, WorkerWire.MaximumFrameBytes,
            PipeSecurityFactory.Create(_userSid), HandleInheritability.None, (PipeAccessRights)0);

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerServer));
    }

    private sealed class WorkerControlConnection : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly int _protocolVersion;

        public WorkerControlConnection(Stream stream, int protocolVersion)
        {
            _stream = stream;
            _protocolVersion = protocolVersion;
            Id = Guid.NewGuid().ToString("N");
        }

        public string Id { get; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await WorkerWire.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame);
                if (!document.RootElement.TryGetProperty("Command", out var command))
                {
                    if (document.RootElement.TryGetProperty("EventId", out _))
                        throw new InvalidDataException("A worker event result is not a client command.");
                    throw new InvalidDataException("A worker request command is required.");
                }
                var request = WorkerWire.Deserialize<WorkerEnvelope>(frame);
                if (!string.Equals(request.Command, WorkerCommands.Handshake, StringComparison.Ordinal))
                    throw new InvalidDataException("The command discriminator is not registered.");
                if (request.ProtocolVersion != _protocolVersion)
                    throw new InvalidDataException("The request protocol does not match negotiation.");
                await WorkerWire.WriteAsync(_stream, new WorkerEnvelope(
                    request.RequestId, WorkerCommands.Handshake, EmptyPayload(), _protocolVersion), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        private static JsonElement EmptyPayload() =>
            JsonDocument.Parse("{}").RootElement.Clone();
    }
}

internal static class WorkerWire
{
    public const int MaximumFrameBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (payload.Length <= 0 || payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("The control frame exceeds its bound.");
        var prefix = new byte[4];
        prefix[0] = (byte)payload.Length;
        prefix[1] = (byte)(payload.Length >> 8);
        prefix[2] = (byte)(payload.Length >> 16);
        prefix[3] = (byte)(payload.Length >> 24);
        await stream.WriteAsync(prefix, 0, prefix.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = prefix[0] | (prefix[1] << 8) | (prefix[2] << 16) | (prefix[3] << 24);
        if (length <= 0 || length > MaximumFrameBytes)
            throw new InvalidDataException("The control frame length is outside its bound.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static T Deserialize<T>(byte[] payload)
    {
        var value = JsonSerializer.Deserialize<T>(payload, Options);
        return value ?? throw new InvalidDataException("The control frame has no value.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The control pipe closed mid-frame.");
            offset += read;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
