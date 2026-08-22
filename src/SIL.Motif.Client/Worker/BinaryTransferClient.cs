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
        Func<BinaryTransferCompletion, CancellationToken, Task> sendCompletion)
    {
        if (!string.Equals(offer.Direction, "upload", StringComparison.Ordinal))
            throw new InvalidOperationException("Only upload offers can be sent by this client.");
        if (DateTimeOffset.UtcNow >= offer.ExpiresAt)
            throw new InvalidOperationException("The binary transfer offer has expired.");

        var binaryPipe = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        BinaryTransferCompletion completion;
        try
        {
            using (binaryPipe)
            {
                using var cancellation = cancellationToken.Register(binaryPipe.Dispose);
                await binaryPipe.ConnectAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                using var digest = SHA256.Create();
                var buffer = new byte[BufferSize];
                long count = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (count > offer.MaximumBytes - read)
                        throw new InvalidOperationException("The binary transfer exceeds the offered maximum.");
                    await binaryPipe.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    digest.TransformBlock(buffer, 0, read, null, 0);
                    count += read;
                }
                digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                await binaryPipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                completion = new BinaryTransferCompletion(offer.TransferId, count, ToLowerHex(digest.Hash!));
            }

            await sendCompletion(completion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            binaryPipe.Dispose();
            throw;
        }
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
