namespace SIL.Motif.Worker.Store;

/// <summary>Caps how many finished jobs one project keeps; the rest are eligible for purge.</summary>
/// <remarks>
/// Queued, running and waiting rows are never counted toward the cap and never purged, no matter how many
/// exist: only rows a terminal transition has stamped with <c>ArchivedUtc</c> compete for the retained
/// slots, ranked by that timestamp.
/// </remarks>
public sealed record ArchivePolicy
{
    // Internal, not public: the cap is a fixed constant, never a caller-configurable knob.
    internal ArchivePolicy(int retainedCount)
    {
        if (retainedCount < 0) throw new ArgumentOutOfRangeException(nameof(retainedCount));
        RetainedCount = retainedCount;
    }

    /// <summary>How many finished jobs survive a purge, most recently archived first.</summary>
    public int RetainedCount { get; }

    public static ArchivePolicy Default { get; } = new(500);
}
