using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker;

/// <summary>Owns the user-scoped worker endpoint and validates control traffic before dispatch.</summary>
public sealed class WorkerServer : IAsyncDisposable, IWorkerWorkTracker
{
    private readonly string _userNamespace;
    private readonly string _userSid;
    private readonly WorkerMutexOwner _ownerMutex;
    private readonly WorkerHandshakeOffer _offer;
    private readonly IWorkerWorkTracker? _workTracker;
    private readonly ProjectHostRegistry _hostRegistry;
    private readonly WorkerEventSink _eventSink;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly ConcurrentDictionary<string, WorkerControlConnection> _connections =
        new ConcurrentDictionary<string, WorkerControlConnection>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly object _gate = new object();
    private bool _started;
    private bool _disposed;
    private Task? _acceptLoop;

    /// <summary>Creates a server using the actual Windows user SID.</summary>
    public WorkerServer(IWorkerWorkTracker? workTracker = null)
        : this(CurrentSid(), workTracker)
    {
    }

    internal WorkerServer(string userNamespace, IWorkerWorkTracker? workTracker = null)
    {
        _userSid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";
        _userNamespace = string.IsNullOrWhiteSpace(userNamespace) ? _userSid : userNamespace;
        EndpointName = GetControlPipeNameForNamespace(_userNamespace);
        OwnerName = GetOwnerMutexNameForNamespace(_userNamespace);
        _ownerMutex = new WorkerMutexOwner(OwnerName);
        _offer = WorkerBuildMetadataProvider.Current.ToHandshakeOffer();
        _workTracker = workTracker ?? new WorkerWorkTracker();
        _hostRegistry = new ProjectHostRegistry();
        _eventSink = new WorkerEventSink(_hostRegistry);
    }

    /// <summary>Creates an isolated server identity for protocol tests only.</summary>
    internal static WorkerServer CreateForTests(string userNamespace) =>
        new WorkerServer(userNamespace, null);

    /// <summary>The predictable control endpoint for clients in this user namespace.</summary>
    public string EndpointName { get; }

    /// <summary>The named mutex which serializes process ownership.</summary>
    public string OwnerName { get; }

    /// <summary>Whether this process currently owns the user-scoped worker mutex.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>Whether a queued, running, or waiting request is keeping the worker alive.</summary>
    public bool HasQueuedRunningOrWaitingWork => _workTracker!.HasQueuedRunningOrWaitingWork;

    /// <summary>The duplex event sink attached to the explicitly registered live host.</summary>
    public WorkerEventSink EventSink => _eventSink;

    /// <summary>Registers a connection as the live host for one project workspace.</summary>
    public void RegisterLiveHost(ProjectLocator project, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A server connection identity is required.", nameof(connectionId));
        if (!_connections.TryGetValue(connectionId, out var connection))
            throw new InvalidOperationException("The server connection identity is unknown.");
        connection.RegisterLiveHost(project);
    }

    /// <summary>Removes a project host registration only when it belongs to this connection.</summary>
    public void UnregisterLiveHost(ProjectLocator project, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        if (_connections.TryGetValue(connectionId, out var connection))
            connection.UnregisterLiveHost(project);
    }

    /// <summary>Creates a runtime registry bound to this server's host and event activity.</summary>
    internal ProjectRuntimeRegistry CreateRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        Func<DateTimeOffset>? now = null)
    {
        if (_workTracker is not WorkerWorkTracker work)
            throw new InvalidOperationException("Runtime composition requires the worker work tracker.");
        return new ProjectRuntimeRegistry(catalog, recoveryFactory, work, now, _hostRegistry);
    }

    /// <summary>Derives the stable control pipe name for the current Windows user.</summary>
    public static string GetControlPipeName() => GetControlPipeNameForNamespace(CurrentSid());

    /// <summary>Derives the process owner mutex name for the current Windows user.</summary>
    public static string GetOwnerMutexName() => GetOwnerMutexNameForNamespace(CurrentSid());

    internal static string GetControlPipeNameForNamespace(string userNamespace) =>
        WorkerEndpointNames.ControlPipe(userNamespace);

    internal static string GetOwnerMutexNameForNamespace(string userNamespace) =>
        WorkerEndpointNames.OwnerMutex(userNamespace);

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
            IsOwner = _ownerMutex.TryAcquire();
            return IsOwner;
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
        while (!_handlers.IsEmpty)
        {
            var handlers = _handlers.Keys.ToArray();
            try { await Task.WhenAll(handlers).ConfigureAwait(false); }
            catch { }
        }
        await _eventSink.DisposeAsync().ConfigureAwait(false);
        _hostRegistry.Dispose();
        Exception? ownershipFailure = null;
        if (IsOwner)
        {
            try { _ownerMutex.Release(); }
            catch (Exception exception) { ownershipFailure = exception; }
        }
        try { _ownerMutex.Dispose(); }
        catch (Exception exception) { ownershipFailure ??= exception; }
        _shutdown.Dispose();
        if (ownershipFailure is not null)
            throw new InvalidOperationException("The worker owner mutex could not be released.", ownershipFailure);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (!linked.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateControlPipe();
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                pipe?.Dispose();
                return;
            }
            var handler = HandleConnectionAsync(pipe!, _shutdown.Token);
            _handlers.TryAdd(handler, 0);
            _ = handler.ContinueWith(completed => _handlers.TryRemove(completed, out _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            WorkerControlConnection? connection = null;
            try
            {
                var connectionId = Guid.NewGuid().ToString("N");
                var frame = await WorkerWire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                var handshake = WorkerWire.Deserialize<WorkerHandshakeRequest>(frame);
                var negotiated = WorkerHandshake.Negotiate(handshake, _offer);
                connection = new WorkerControlConnection(pipe, negotiated.ProtocolVersion, _eventSink, connectionId);
                var offer = new WorkerHandshakeOffer(_offer.ProductVersion, _offer.Protocols,
                    _offer.Capabilities, connectionId);
                _connections[connection.Id] = connection;
                await connection.WriteAsync(offer, cancellationToken).ConfigureAwait(false);
                await connection.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed handshake closes before any command or database work is considered.
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
                if (connection is not null)
                    _connections.TryRemove(connection.Id, out _);
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
        private readonly WorkerEventSink _eventSink;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private readonly object _hostSync = new();
        private readonly Dictionary<string, (ProjectLocator Project, string SessionId)> _hostProjects =
            new(StringComparer.Ordinal);
        private bool _disposed;

        public WorkerControlConnection(Stream stream, int protocolVersion, WorkerEventSink eventSink,
            string connectionId)
        {
            _stream = stream;
            _protocolVersion = protocolVersion;
            _eventSink = eventSink;
            Id = connectionId;
        }

        public string Id { get; }

        public void RegisterLiveHost(ProjectLocator project)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var key = ProjectWorkspaceKey.Compute(project);
            lock (_hostSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WorkerControlConnection));
                _eventSink.RegisterLiveHost(project, Id, sessionId, _stream,
                    _protocolVersion, _writeGate);
                _hostProjects[key] = (project, sessionId);
            }
        }

        public void UnregisterLiveHost()
        {
            lock (_hostSync)
            {
                foreach (var registration in _hostProjects.Values)
                    _eventSink.UnregisterLiveHost(registration.Project, Id, registration.SessionId);
                _hostProjects.Clear();
            }
        }

        public void UnregisterLiveHost(ProjectLocator project)
        {
            var key = ProjectWorkspaceKey.Compute(project);
            lock (_hostSync)
            {
                if (!_hostProjects.Remove(key, out var registration)) return;
                _eventSink.UnregisterLiveHost(registration.Project, Id, registration.SessionId);
            }
        }

        public Task WriteAsync(object value, CancellationToken cancellationToken) =>
            WriteSerializedAsync(value, cancellationToken);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await WorkerWire.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("EventId", out _) &&
                    document.RootElement.TryGetProperty("Outcome", out _))
                {
                    _eventSink.AcceptResult(WorkerWire.Deserialize<WorkerEventResultEnvelope>(frame));
                    continue;
                }
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
                await WriteSerializedAsync(new WorkerEnvelope(
                    request.RequestId, WorkerCommands.Handshake, EmptyPayload(), _protocolVersion), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_hostSync)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
            }
            UnregisterLiveHost();
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task WriteSerializedAsync(object value, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WorkerWire.WriteAsync(_stream, value, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
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
