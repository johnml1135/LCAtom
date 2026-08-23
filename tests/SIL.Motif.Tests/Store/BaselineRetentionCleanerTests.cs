using SIL.Motif.Contract.Jobs;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class BaselineRetentionCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-baseline-retention-" + Guid.NewGuid().ToString("N"));

    public BaselineRetentionCleanerTests() => Directory.CreateDirectory(Path.Combine(_root, "project", "baseline"));

    [Fact]
    public void DeletesOnlyOldSupersededUnpinnedPublishedFiles()
    {
        var oldPath = Path.Combine(_root, "project", "baseline", "old.bundle");
        var pinnedPath = Path.Combine(_root, "project", "baseline", "pinned.bundle");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(pinnedPath, "pinned");
        var query = new BaselineQuery(oldPath, pinnedPath);
        var cleaner = new BaselineRetentionCleaner(new WorkspaceOwnership(_root), query, query,
            ArchivePolicy.Default, new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("project");

        Assert.Equal([oldPath], result.DeletedPaths);
        Assert.True(File.Exists(pinnedPath));
    }

    [Fact]
    public void RefusesOutsideAndTraversalPaths()
    {
        var outside = Path.Combine(_root, "outside.bundle");
        File.WriteAllText(outside, "keep");
        var query = new BaselineQuery(outside, outside);
        var cleaner = new BaselineRetentionCleaner(new WorkspaceOwnership(_root), query, query,
            new ArchivePolicy(TimeSpan.Zero), new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("../other");

        Assert.NotEmpty(result.Failures);
        Assert.True(File.Exists(outside));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class BaselineQuery(string oldPath, string pinnedPath) : IPublishedBaselineQuery, IBaselineReferenceQuery
    {
        public IReadOnlyList<PublishedBaseline> ListPublished(string projectKey) =>
            [new(oldPath, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true),
             new(pinnedPath, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true)];
        public bool HasActiveReference(string baselinePath) => baselinePath == pinnedPath;
    }

    private sealed class FixedClock(string value) : IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
