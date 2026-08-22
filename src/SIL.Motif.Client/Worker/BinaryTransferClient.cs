using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

internal static class BinaryTransferClient
{
    private const int BufferSize = 64 * 1024;

    public static async Task UploadAsync(
        BinaryTransferOffer offer,
        Stream source,
        CancellationToken cancellationToken,
        Func<BinaryTransferCompletion, DateTimeOffset, CancellationToken, Task> sendCompletion)
    {
        if (!string.Equals(offer.Direction, "upload", StringComparison.Ordinal))
            throw new InvalidOperationException("Only upload offers can be sent by this client.");
        if (DateTimeOffset.UtcNow >= offer.ExpiresAt)
            throw new InvalidOperationException("The binary transfer offer has expired.");

        var remaining = offer.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new InvalidOperationException("The binary transfer offer has expired.");
        using var expiryCancellation = new CancellationTokenSource();
        using var expiryMonitorCancellation = new CancellationTokenSource();
        var expiryMonitor = MonitorExpiryAsync(offer.ExpiresAt, expiryCancellation,
            expiryMonitorCancellation.Token);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, expiryCancellation.Token);
        var transferCancellation = linkedCancellation.Token;
        var binaryPipe = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        BinaryTransferCompletion completion;
        try
        {
            using (binaryPipe)
            {
                using var cancellation = transferCancellation.Register(binaryPipe.Dispose);
                await binaryPipe.ConnectAsync().ConfigureAwait(false);
                ThrowIfExpired(offer, expiryCancellation, transferCancellation);

                using var digest = SHA256.Create();
                var buffer = new byte[BufferSize];
                long count = 0;
                while (true)
                {
                    var read = await ReadWithDeadlineAsync(
                        offer, source, buffer, transferCancellation, expiryCancellation).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (count > offer.MaximumBytes - read)
                        throw new InvalidOperationException("The binary transfer exceeds the offered maximum.");
                    await binaryPipe.WriteAsync(buffer, 0, read, transferCancellation).ConfigureAwait(false);
                    digest.TransformBlock(buffer, 0, read, null, 0);
                    count += read;
                }
                digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                await binaryPipe.FlushAsync(transferCancellation).ConfigureAwait(false);
                ThrowIfExpired(offer, expiryCancellation, transferCancellation);
                completion = new BinaryTransferCompletion(offer.TransferId, count, ToLowerHex(digest.Hash!));
            }

            ThrowIfExpired(offer, expiryCancellation, transferCancellation);
            await sendCompletion(completion, offer.ExpiresAt, transferCancellation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            binaryPipe.Dispose();
            if (expiryCancellation.IsCancellationRequested)
                throw new InvalidOperationException("The binary transfer offer has expired.", exception);
            throw;
        }
        finally
        {
            expiryMonitorCancellation.Cancel();
            await expiryMonitor.ConfigureAwait(false);
        }
    }

    private static void ThrowIfExpired(
        BinaryTransferOffer offer, CancellationTokenSource expiryCancellation, CancellationToken transferCancellation)
    {
        if (expiryCancellation.IsCancellationRequested || DateTimeOffset.UtcNow >= offer.ExpiresAt)
            throw new InvalidOperationException("The binary transfer offer has expired.");
        transferCancellation.ThrowIfCancellationRequested();
    }

    private static async Task<int> ReadWithDeadlineAsync(
        BinaryTransferOffer offer,
        Stream source,
        byte[] buffer,
        CancellationToken transferCancellation,
        CancellationTokenSource expiryCancellation)
    {
        ThrowIfExpired(offer, expiryCancellation, transferCancellation);
        var readTask = source.ReadAsync(buffer, 0, buffer.Length, transferCancellation);
        if (readTask.IsCompleted)
            return await readTask.ConfigureAwait(false);

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, transferCancellation);
        if (await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false) == readTask)
            return await readTask.ConfigureAwait(false);

        ObserveFailure(readTask);
        ThrowIfExpired(offer, expiryCancellation, transferCancellation);
        throw new InvalidOperationException("The binary transfer offer has expired.");
    }

    private static async Task MonitorExpiryAsync(
        DateTimeOffset expiresAt, CancellationTokenSource expiryCancellation, CancellationToken cancellationToken)
    {
        var maximumWait = TimeSpan.FromDays(1);
        while (true)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                expiryCancellation.Cancel();
                return;
            }
            try
            {
                await Task.Delay(remaining < maximumWait ? remaining : maximumWait, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static void ObserveFailure(Task task)
    {
        task.ContinueWith(
            completed =>
            {
                var ignored = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ToLowerHex(byte[] digest)
    {
        var chars = new char[digest.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (var index = 0; index < digest.Length; index++)
        {
            chars[index * 2] = alphabet[digest[index] >> 4];
            chars[index * 2 + 1] = alphabet[digest[index] & 0x0f];
        }
        return new string(chars);
    }
}
