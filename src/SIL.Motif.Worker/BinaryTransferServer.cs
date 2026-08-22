using System;
using System.Collections.Concurrent;
using System.IO;
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
    private readonly string _tempDirectory;
    private readonly IWorkerClock _clock;
    private readonly string? _userSid;
    private readonly ConcurrentDictionary<string, Transfer> _transfers =
        new ConcurrentDictionary<string, Transfer>(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates a server that owns temporary files under the supplied directory.</summary>
    public BinaryTransferServer(string tempDirectory, IWorkerClock? clock = null, string? userSid = null)
    {
        _tempDirectory = Path.GetFullPath(tempDirectory ?? throw new ArgumentNullException(nameof(tempDirectory)));
        Directory.CreateDirectory(_tempDirectory);
        _clock = clock ?? new SystemClock();
        _userSid = userSid;
    }

    /// <summary>Exposes the same restricted ACL used by every binary pipe.</summary>
    public static PipeSecurity CreatePipeSecurity(string? userSid = null) =>
        PipeSecurityFactory.Create(userSid ?? CurrentSid());

    /// <summary>Creates an unpredictable one-use offer and starts accepting its upload.</summary>
    public BinaryTransferOffer CreateOffer(long maximumBytes, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        ThrowIfDisposed();
        var id = Guid.NewGuid().ToString("N");
        var offer = new BinaryTransferOffer(id, "upload", "motif-transfer-" + Guid.NewGuid().ToString("N"),
            maximumBytes, _clock.UtcNow + lifetime);
        var transfer = new Transfer(offer, Path.Combine(_tempDirectory, id + ".tmp"));
        if (!_transfers.TryAdd(id, transfer))
            throw new InvalidOperationException("A transfer identifier was unexpectedly reused.");
        transfer.AcceptTask = AcceptAsync(transfer, cancellationToken);
        _ = RemoveAfterFailureAsync(transfer);
        return offer;
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
        if (_clock.UtcNow >= transfer.Offer.ExpiresAt)
            return Reject(transfer, "The binary transfer offer has expired.");
        if (completion.ByteCount != transfer.ByteCount ||
            !string.Equals(completion.Sha256, transfer.Sha256, StringComparison.OrdinalIgnoreCase))
            return Reject(transfer, "The binary transfer completion does not match received bytes.");
        var readyPath = Path.Combine(_tempDirectory, transfer.Offer.TransferId + ".ready");
        File.Move(transfer.TempPath, readyPath);
        lock (transfer.Gate)
            transfer.State = TransferState.Published;
        _transfers.TryRemove(transfer.Offer.TransferId, out _);
        return readyPath;
    }

    /// <summary>Removes all unpublished temporary files owned by this server.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var transfer in _transfers.Values)
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
                _clock.DelayAsync(DelayUntil(transfer.Offer.ExpiresAt), deadlineCancellation.Token), expiry);
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
            if (_clock.UtcNow >= transfer.Offer.ExpiresAt)
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
        try { if (File.Exists(transfer.TempPath)) File.Delete(transfer.TempPath); } catch { }
    }

    private TimeSpan DelayUntil(DateTimeOffset deadline)
    {
        var delay = deadline - _clock.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(BinaryTransferServer));
    }

    private sealed class Transfer
    {
        public Transfer(BinaryTransferOffer offer, string tempPath) { Offer = offer; TempPath = tempPath; }
        public object Gate { get; } = new object();
        public BinaryTransferOffer Offer { get; }
        public string TempPath { get; }
        public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();
        public Task AcceptTask { get; set; } = Task.CompletedTask;
        public long ByteCount { get; set; }
        public string? Sha256 { get; set; }
        public TransferState State { get; set; }
    }

    private enum TransferState { Receiving, Received, Completing, Published, Rejected }

    private sealed class SystemClock : IWorkerClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
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
            PipeAccessRights.ReadWrite, AccessControlType.Deny));
        return security;
    }
}
