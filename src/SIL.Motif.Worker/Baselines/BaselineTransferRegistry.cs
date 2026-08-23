using System.Collections.Concurrent;
using System.Security.Cryptography;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker.Baselines;

internal sealed record VerifiedBinaryTransfer(
    string TransferId,
    string TemporaryPath,
    long ByteCount,
    string Sha256);

internal sealed class BaselineTransferRegistry : IAsyncDisposable
{
    private readonly string _transferRoot;
    private readonly string _rootPrefix;
    private readonly BinaryTransferServer _binary;
    private readonly IWorkerClock _clock;
    private readonly ConcurrentDictionary<string, Registration> _registrations =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public BaselineTransferRegistry(string transferRoot, BinaryTransferServer binary,
        IWorkerClock? clock = null)
    {
        _transferRoot = RequireTransferRoot(transferRoot);
        _rootPrefix = _transferRoot + Path.DirectorySeparatorChar;
        _binary = binary ?? throw new ArgumentNullException(nameof(binary));
        _clock = clock ?? new RegistryClock();
    }

    public BinaryTransferOffer CreateOffer(string connectionId, ProjectLocator project,
        long maximumBytes, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RequireConnection(connectionId);
        ArgumentNullException.ThrowIfNull(project);
        var offer = _binary.CreateOffer(maximumBytes, lifetime, cancellationToken);
        var registration = new Registration(connectionId, ProjectWorkspaceKey.Compute(project), offer);
        if (!_registrations.TryAdd(offer.TransferId, registration))
            throw new InvalidOperationException("A Baseline transfer identifier was unexpectedly reused.");
        if (cancellationToken.CanBeCanceled)
            registration.ExternalCancellation = cancellationToken.Register(
                () => _ = CancelRegistrationAsync(registration));
        _ = ExpireAsync(registration, lifetime);
        return offer;
    }

    public async Task CompleteAsync(string connectionId, BinaryTransferCompletion completion,
        CancellationToken cancellationToken)
    {
        RequireConnection(connectionId);
        ArgumentNullException.ThrowIfNull(completion);
        if (!_registrations.TryGetValue(completion.TransferId, out var registration))
            throw new InvalidOperationException("The Baseline transfer is unknown or already used.");
        lock (registration.Gate)
        {
            if (!StringComparer.Ordinal.Equals(registration.ConnectionId, connectionId))
                throw new InvalidOperationException("The Baseline transfer belongs to another connection.");
            if (registration.State != RegistrationState.Offered)
                throw new InvalidOperationException("The Baseline transfer cannot be completed in its current state.");
            registration.State = RegistrationState.Completing;
        }

        string readyPath;
        try
        {
            readyPath = await _binary.CompleteAsync(completion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _registrations.TryRemove(completion.TransferId, out _);
            lock (registration.Gate)
                registration.State = RegistrationState.Cancelled;
            StopMonitoring(registration);
            throw;
        }

        lock (registration.Gate)
        {
            if (registration.State == RegistrationState.Cancelled)
            {
                DeleteOwnedFile(readyPath);
                throw new InvalidOperationException("The Baseline transfer was cancelled.");
            }
            registration.ReadyPath = readyPath;
            registration.ByteCount = completion.ByteCount;
            registration.Sha256 = completion.Sha256.ToLowerInvariant();
            registration.State = RegistrationState.Ready;
        }
    }

    public VerifiedBinaryTransfer Claim(string connectionId, ProjectLocator project, string transferId)
    {
        ThrowIfDisposed();
        RequireConnection(connectionId);
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(transferId) ||
            !_registrations.TryGetValue(transferId, out var registration))
            throw new InvalidOperationException("The Baseline transfer is unknown or already used.");
        string readyPath;
        long byteCount;
        string sha256;
        lock (registration.Gate)
        {
            if (!StringComparer.Ordinal.Equals(registration.ConnectionId, connectionId))
                throw new InvalidOperationException("The Baseline transfer belongs to another connection.");
            if (!StringComparer.Ordinal.Equals(registration.ProjectKey, ProjectWorkspaceKey.Compute(project)))
                throw new InvalidOperationException("The Baseline transfer belongs to another project.");
            if (registration.State != RegistrationState.Ready || registration.ReadyPath is null ||
                registration.Sha256 is null)
                throw new InvalidOperationException("The Baseline transfer is not ready.");
            registration.State = RegistrationState.Claimed;
            readyPath = registration.ReadyPath;
            byteCount = registration.ByteCount;
            sha256 = registration.Sha256;
            _registrations.TryRemove(transferId, out _);
        }
        StopMonitoring(registration);

        try
        {
            if (_clock.UtcNow >= registration.Offer.ExpiresAt)
                throw new InvalidOperationException("The Baseline transfer offer has expired.");
            VerifyOwnedReadyPath(transferId, readyPath);
            using var stream = new FileStream(readyPath, FileMode.Open, FileAccess.Read, FileShare.None);
            if ((File.GetAttributes(readyPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A reparse-point Baseline transfer is refused.");
            using var digest = SHA256.Create();
            var actualSha256 = Convert.ToHexString(digest.ComputeHash(stream)).ToLowerInvariant();
            if (stream.Length != byteCount || !StringComparer.Ordinal.Equals(actualSha256, sha256))
                throw new InvalidOperationException("The Baseline transfer no longer matches its verified bytes.");
            return new VerifiedBinaryTransfer(transferId, readyPath, byteCount, sha256);
        }
        catch
        {
            DeleteOwnedFile(readyPath);
            throw;
        }
    }

    public async Task ReleaseConnectionAsync(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        var owned = _registrations.Values
            .Where(item => StringComparer.Ordinal.Equals(item.ConnectionId, connectionId)).ToArray();
        foreach (var registration in owned)
        {
            if (!_registrations.TryRemove(registration.Offer.TransferId, out _)) continue;
            string? readyPath;
            lock (registration.Gate)
            {
                registration.State = RegistrationState.Cancelled;
                readyPath = registration.ReadyPath;
            }
            StopMonitoring(registration);
            await _binary.CancelAsync(registration.Offer.TransferId).ConfigureAwait(false);
            if (readyPath is not null)
                DeleteOwnedFile(readyPath);
        }
    }

    public static void CleanupStartup(string transferRoot)
    {
        var root = RequireTransferRoot(transferRoot);
        var prefix = root + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(full), root) ||
                (!full.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                 !full.EndsWith(".ready", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(full))
                continue;
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            File.Delete(full);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var connections = _registrations.Values.Select(item => item.ConnectionId)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var connection in connections)
            await ReleaseConnectionAsync(connection).ConfigureAwait(false);
    }

    private static string RequireTransferRoot(string transferRoot)
    {
        if (string.IsNullOrWhiteSpace(transferRoot))
            throw new ArgumentException("A dedicated transfer root is required.", nameof(transferRoot));
        var root = Path.GetFullPath(transferRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(root))
            throw new InvalidOperationException("The transfer root cannot be a file.");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A reparse-point transfer root is refused.");
        Directory.CreateDirectory(root);
        return root;
    }

    private void VerifyOwnedReadyPath(string transferId, string path)
    {
        var full = Path.GetFullPath(path);
        var expected = Path.Combine(_transferRoot, transferId + ".ready");
        if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !StringComparer.OrdinalIgnoreCase.Equals(full, expected) || !File.Exists(full))
            throw new InvalidOperationException("The Baseline transfer path is outside its dedicated root.");
        if ((File.GetAttributes(_transferRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A reparse-point transfer root is refused.");
    }

    private async Task ExpireAsync(Registration registration, TimeSpan lifetime)
    {
        try
        {
            await _clock.DelayAsync(lifetime, registration.ExpiryCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (registration.ExpiryCancellation.IsCancellationRequested)
        {
            return;
        }
        await CancelRegistrationAsync(registration).ConfigureAwait(false);
    }

    private async Task CancelRegistrationAsync(Registration registration)
    {
        if (!_registrations.TryGetValue(registration.Offer.TransferId, out var current) ||
            !ReferenceEquals(current, registration))
            return;
        lock (registration.Gate)
        {
            if (registration.State is RegistrationState.Claimed or RegistrationState.Cancelled)
                return;
            registration.State = RegistrationState.Cancelled;
            if (registration.ReadyPath is not null)
                DeleteOwnedFile(registration.ReadyPath);
        }
        registration.ExpiryCancellation.Cancel();
        await Task.Yield();
        await _binary.CancelAsync(registration.Offer.TransferId).ConfigureAwait(false);
        _registrations.TryRemove(registration.Offer.TransferId, out _);
        registration.ExternalCancellation.Dispose();
    }

    private static void StopMonitoring(Registration registration)
    {
        registration.ExpiryCancellation.Cancel();
        registration.ExternalCancellation.Dispose();
    }

    private void DeleteOwnedFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(full), _transferRoot))
            return;
        try
        {
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                File.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RequireConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A worker connection identity is required.", nameof(connectionId));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BaselineTransferRegistry));
    }

    private sealed class Registration
    {
        public Registration(string connectionId, string projectKey, BinaryTransferOffer offer)
        {
            ConnectionId = connectionId;
            ProjectKey = projectKey;
            Offer = offer;
        }

        public object Gate { get; } = new();
        public string ConnectionId { get; }
        public string ProjectKey { get; }
        public BinaryTransferOffer Offer { get; }
        public RegistrationState State { get; set; }
        public string? ReadyPath { get; set; }
        public long ByteCount { get; set; }
        public string? Sha256 { get; set; }
        public CancellationTokenSource ExpiryCancellation { get; } = new();
        public CancellationTokenRegistration ExternalCancellation { get; set; }
    }

    private enum RegistrationState { Offered, Completing, Ready, Claimed, Cancelled }

    private sealed class RegistryClock : IWorkerClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSpan MonotonicNow => TimeSpan.Zero;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
