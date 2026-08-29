namespace SIL.Motif.Worker.Store;

/// <summary>Controls when a terminal row becomes eligible for purge, by age since it archived.</summary>
public sealed record TimeRetentionPolicy
{
    public TimeRetentionPolicy(TimeSpan retention, bool forever = false)
    {
        if (retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        Retention = retention;
        Forever = forever;
    }

    public TimeSpan Retention { get; }
    public bool Forever { get; }

    public static TimeRetentionPolicy Default { get; } = new(TimeSpan.FromDays(30));

    public bool ShouldPurge(DateTimeOffset? archivedUtc, DateTimeOffset now)
    {
        if (Forever || archivedUtc is null) return false;
        return now.ToUniversalTime() >= archivedUtc.Value.ToUniversalTime().Add(Retention);
    }
}
