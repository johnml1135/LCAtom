namespace SIL.Motif.Worker.Store;

/// <summary>Controls when terminal workflow rows become eligible for archive purge.</summary>
public sealed record ArchivePolicy
{
    public ArchivePolicy(TimeSpan retention, bool forever = false)
    {
        if (retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        Retention = retention;
        Forever = forever;
    }

    public TimeSpan Retention { get; }
    public bool Forever { get; }

    public static ArchivePolicy Default { get; } = new(TimeSpan.FromDays(30));
    public bool ShouldPurge(DateTimeOffset? archivedUtc, DateTimeOffset now)
    {
        if (Forever || archivedUtc is null) return false;
        return now.ToUniversalTime() >= archivedUtc.Value.ToUniversalTime().Add(Retention);
    }
}
