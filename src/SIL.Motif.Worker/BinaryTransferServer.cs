using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Receives one-use bounded binary offers and publishes only after a matching completion.</summary>
public sealed class BinaryTransferServer : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    /// <summary>Default simultaneous unpublished-offer envelope.</summary>
    public const int DefaultMaximumActiveOffers = 128;
    /// <summary>Default aggregate bytes reserved by unpublished offers.</summary>
    public const long DefaultMaximumReservedBytes = 1024L * 1024 * 1024;
    private readonly string _tempDirectory;
    private readonly IWorkerClock _clock;
    private readonly string? _userSid;
    private readonly int _maximumActiveOffers;
    private readonly long _maximumReservedBytes;
    private readonly object _lifecycleGate = new object();
    private Action? _onOfferRegistered;
    private Action? _onDisposed;
    private readonly ConcurrentDictionary<string, Transfer> _transfers =
        new ConcurrentDictionary<string, Transfer>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cleanupFailures =
        new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private long _reservedBytes;

    /// <summary>Creates a server that owns temporary files under the supplied directory.</summary>
    public BinaryTransferServer(string tempDirectory, IWorkerClock? clock = null, string? userSid = null,
        int maximumActiveOffers = DefaultMaximumActiveOffers,
        long maximumReservedBytes = DefaultMaximumReservedBytes)
        : this(tempDirectory, clock, userSid, maximumActiveOffers, maximumReservedBytes, null, null)
    {
    }

    private BinaryTransferServer(string tempDirectory, IWorkerClock? clock, string? userSid,
        int maximumActiveOffers, long maximumReservedBytes,
        Action? onOfferRegistered, Action? onDisposed)
    {
        if (maximumActiveOffers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumActiveOffers));
        if (maximumReservedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumReservedBytes));
        _tempDirectory = Path.GetFullPath(tempDirectory ?? throw new ArgumentNullException(nameof(tempDirectory)));
        Directory.CreateDirectory(_tempDirectory);
        _clock = clock ?? new SystemClock();
        _userSid = userSid;
        _maximumActiveOffers = maximumActiveOffers;
        _maximumReservedBytes = maximumReservedBytes;
        _onOfferRegistered = onOfferRegistered;
        _onDisposed = onDisposed;
    }

    internal static BinaryTransferServer CreateWithLifecycleProbes(string tempDirectory,
        Action? onOfferRegistered = null, Action? onDisposed = null) =>
        new BinaryTransferServer(tempDirectory, null, null, DefaultMaximumActiveOffers,
            DefaultMaximumReservedBytes, onOfferRegistered, onDisposed);

    /// <summary>Exposes the same restricted ACL used by every binary pipe.</summary>
    public static PipeSecurity CreatePipeSecurity(string? userSid = null) =>
        PipeSecurityFactory.Create(userSid ?? CurrentSid());

    /// <summary>Returns temporary paths whose cleanup failed and need an explicit retry.</summary>
    public IReadOnlyCollection<string> CleanupFailures => _cleanupFailures.Keys.ToArray();

    /// <summary>Retries deletion of temporary paths recorded after a cleanup failure.</summary>
    public void RetryCleanupFailures()
    {
        foreach (var path in _cleanupFailures.Keys)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (!File.Exists(path) && !Directory.Exists(path))
                    _cleanupFailures.TryRemove(path, out _);
            }
            catch
            {
            }
        }
    }

    /// <summary>Creates an unpredictable one-use offer and starts accepting its upload.</summary>
    public BinaryTransferOffer CreateOffer(long maximumBytes, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (_transfers.Count >= _maximumActiveOffers)
                throw new InvalidOperationException("The active binary-offer envelope is full.");
            if (maximumBytes > _maximumReservedBytes - _reservedBytes)
                throw new InvalidOperationException("The reserved binary-byte envelope is full.");
            var id = Guid.NewGuid().ToString("N");
            var offer = new BinaryTransferOffer(id, "upload",
                "motif-transfer-" + Guid.NewGuid().ToString("N"), maximumBytes,
                _clock.UtcNow + lifetime);
            var transfer = new Transfer(offer, Path.Combine(_tempDirectory, id + ".tmp"),
                _clock.MonotonicNow + lifetime);
            if (!_transfers.TryAdd(id, transfer))
                throw new InvalidOperationException("A transfer identifier was unexpectedly reused.");
            _reservedBytes += maximumBytes;
            try
            {
                _onOfferRegistered?.Invoke();
                transfer.AcceptTask = AcceptAsync(transfer, cancellationToken);
                _ = RemoveAfterFailureAsync(transfer);
                return offer;
            }
            catch
            {
                _transfers.TryRemove(id, out _);
                ReleaseCapacity(transfer);
                throw;
            }
        }
    }

    /// <summary>Completes and atomically publishes an offer after checking its count and digest.</summary>
    public async Task<string> CompleteAsync(BinaryTransferCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (completion is null)
            throw new ArgumentNullException(nameof(completion));
        if (!_transfers.TryGetValue(completion.TransferId, out var transfer))
            throw new InvalidOperationException("The binary transfer identifier is unknown or already used.");
        await transfer.AcceptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (transfer.Gate)
        {
            if (transfer.State != TransferState.Received)
                throw new InvalidOperationException("The binary transfer is no longer publishable.");
            transfer.State = TransferState.Completing;
        }
        if (_clock.MonotonicNow >= transfer.MonotonicDeadline)
            return Reject(transfer, "The binary transfer offer has expired.");
        if (completion.ByteCount != transfer.ByteCount ||
            !string.Equals(completion.Sha256, transfer.Sha256, StringComparison.OrdinalIgnoreCase))
            return Reject(transfer, "The binary transfer completion does not match received bytes.");
        var readyPath = Path.Combine(_tempDirectory, transfer.Offer.TransferId + ".ready");
        try
        {
            File.Move(transfer.TempPath, readyPath);
        }
        catch
        {
            RemoveAndDelete(transfer);
            throw;
        }
        lock (transfer.Gate)
            transfer.State = TransferState.Published;
        _transfers.TryRemove(transfer.Offer.TransferId, out _);
        ReleaseCapacity(transfer);
        return readyPath;
    }

    /// <summary>Removes all unpublished temporary files owned by this server.</summary>
    public async ValueTask DisposeAsync()
    {
        Transfer[] transfers;
        lock (_lifecycleGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            transfers = _transfers.Values.ToArray();
            _onDisposed?.Invoke();
        }
        foreach (var transfer in transfers)
        {
            transfer.Cancel.Cancel();
            try { await transfer.AcceptTask.ConfigureAwait(false); } catch { }
            RemoveAndDelete(transfer);
        }
        _transfers.Clear();
    }

    private async Task AcceptAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        using var deadlineCancellation = new CancellationTokenSource();
        try
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(transfer.Offer.PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferSize, BufferSize,
                PipeSecurityFactory.Create(_userSid), HandleInheritability.None, (PipeAccessRights)0);
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Cancel.Token);
            var deadline = CancelAtDeadlineAsync(
                _clock.DelayAsync(DelayUntil(transfer.MonotonicDeadline), deadlineCancellation.Token), expiry);
            var waitForConnection = pipe.WaitForConnectionAsync(expiry.Token);
            if (await Task.WhenAny(waitForConnection, deadline).ConfigureAwait(false) != waitForConnection)
                throw new InvalidOperationException("The binary transfer offer has expired.");
            await waitForConnection.ConfigureAwait(false);
            using var output = new FileStream(transfer.TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var digest = SHA256.Create();
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await pipe.ReadAsync(buffer, 0, buffer.Length, expiry.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (transfer.ByteCount > transfer.Offer.MaximumBytes - read)
                    throw new InvalidDataException("The binary transfer exceeds its offered length.");
                await output.WriteAsync(buffer, 0, read, expiry.Token).ConfigureAwait(false);
                digest.TransformBlock(buffer, 0, read, null, 0);
                transfer.ByteCount += read;
            }
            digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            transfer.Sha256 = Convert.ToHexString(digest.Hash!).ToLowerInvariant();
            if (_clock.MonotonicNow >= transfer.MonotonicDeadline)
                throw new InvalidOperationException("The binary transfer offer has expired.");
            lock (transfer.Gate)
                transfer.State = TransferState.Received;
        }
        catch
        {
            RemoveAndDelete(transfer);
            throw;
        }
        finally
        {
            deadlineCancellation.Cancel();
        }
    }

    private static async Task CancelAtDeadlineAsync(Task deadline, CancellationTokenSource expiry)
    {
        try
        {
            await deadline.ConfigureAwait(false);
            expiry.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RemoveAfterFailureAsync(Transfer transfer)
    {
        try { await transfer.AcceptTask.ConfigureAwait(false); }
        catch { RemoveAndDelete(transfer); }
    }

    private string Reject(Transfer transfer, string message)
    {
        RemoveAndDelete(transfer);
        throw new InvalidOperationException(message);
    }

    private void RemoveAndDelete(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            if (transfer.State == TransferState.Published)
                return;
            transfer.State = TransferState.Rejected;
            _transfers.TryRemove(transfer.Offer.TransferId, out _);
        }
        ReleaseCapacity(transfer);
        try
        {
            if (File.Exists(transfer.TempPath))
                File.Delete(transfer.TempPath);
            else if (Directory.Exists(transfer.TempPath))
                throw new IOException("The transfer temporary path is a directory.");
            _cleanupFailures.TryRemove(transfer.TempPath, out _);
        }
        catch
        {
            _cleanupFailures.TryAdd(transfer.TempPath, 0);
        }
    }

    private void ReleaseCapacity(Transfer transfer)
    {
        lock (transfer.Gate)
        {
            if (transfer.CapacityReleased)
                return;
            transfer.CapacityReleased = true;
        }
        lock (_lifecycleGate)
            _reservedBytes -= transfer.Offer.MaximumBytes;
    }

    private TimeSpan DelayUntil(TimeSpan deadline)
    {
        var delay = deadline - _clock.MonotonicNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BinaryTransferServer));
    }

    private sealed class Transfer
    {
        public Transfer(BinaryTransferOffer offer, string tempPath, TimeSpan monotonicDeadline)
        {
            Offer = offer;
            TempPath = tempPath;
            MonotonicDeadline = monotonicDeadline;
        }
        public object Gate { get; } = new object();
        public BinaryTransferOffer Offer { get; }
        public string TempPath { get; }
        public TimeSpan MonotonicDeadline { get; }
        public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();
        public Task AcceptTask { get; set; } = Task.CompletedTask;
        public long ByteCount { get; set; }
        public string? Sha256 { get; set; }
        public TransferState State { get; set; }
        public bool CapacityReleased { get; set; }
    }

    private enum TransferState { Receiving, Received, Completing, Published, Rejected }

    private sealed class SystemClock : IWorkerClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSpan MonotonicNow =>
            TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}

internal static class PipeSecurityFactory
{
    public static PipeSecurity Create(string? userSid)
    {
        var sid = new SecurityIdentifier(userSid ?? WindowsIdentity.GetCurrent().User!.Value);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}
