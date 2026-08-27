namespace SIL.Motif.Worker.Baselines;

/// <summary>One Baseline bundle file that arrived complete and matched its declared hash.</summary>
/// <remarks>
/// The verification is what the name records, not the route. A bundle is written to a path by whoever
/// captured it and verified here before anything is published from it, so the same value describes a
/// capture written by an in-process host and one left by a separate command.
/// </remarks>
internal static class BaselineBundleBounds
{
    /// <summary>The largest Baseline bundle accepted, whatever wrote it.</summary>
    internal const long MaximumBundleBytes = 512L * 1024 * 1024;
}

internal sealed record VerifiedBinaryTransfer(
    string TransferId,
    string TemporaryPath,
    long ByteCount,
    string Sha256);
