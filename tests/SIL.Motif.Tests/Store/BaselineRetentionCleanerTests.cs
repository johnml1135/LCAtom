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
    public void DeletesOnlyOldSupersededUnpinnedPublishedDirectories()
    {
        var baselineRoot = Path.Combine(_root, "project", "baseline");
        var oldPath = CreatePublishedBaseline(baselineRoot, "old");
        var pinnedPath = CreatePublishedBaseline(baselineRoot, "pinned");
        var query = new BaselineQuery(oldPath, pinnedPath);
        var cleaner = new BaselineRetentionCleaner(WorkspaceOwnership.Bootstrap(_root), query, query,
            TimeRetentionPolicy.Default, new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("project");

        Assert.Equal([oldPath], result.DeletedPaths);
        Assert.True(Directory.Exists(pinnedPath));
    }

    [Fact]
    public void DeletesSupersededUnpinnedDirectoryShapedBaselineFromDisk()
    {
        var baselineRoot = Path.Combine(_root, "project", "baseline");
        var oldPath = CreatePublishedBaseline(baselineRoot, "old");
        var query = new SingleUnpinnedBaselineQuery(oldPath);
        var cleaner = new BaselineRetentionCleaner(WorkspaceOwnership.Bootstrap(_root), query, query,
            TimeRetentionPolicy.Default, new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("project");

        Assert.Equal([oldPath], result.DeletedPaths);
        Assert.Empty(result.Failures);
        Assert.False(Directory.Exists(oldPath));
    }

    [Fact]
    public void RefusesOutsideAndTraversalPaths()
    {
        var outside = CreatePublishedBaseline(_root, "outside");
        var query = new BaselineQuery(outside, outside);
        var cleaner = new BaselineRetentionCleaner(WorkspaceOwnership.Bootstrap(_root), query, query,
            new TimeRetentionPolicy(TimeSpan.Zero), new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("../other");

        Assert.NotEmpty(result.Failures);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void EveryDurablePinSourcePreventsDeletionAndUnpinnedDirectoryIsRemoved()
    {
        var baselineRoot = Path.Combine(_root, "project", "baseline");
        var paths = Enum.GetValues<BaselinePinSources>().Where(source => source != BaselinePinSources.None)
            .ToDictionary(source => source, source => CreatePublishedBaseline(baselineRoot, source.ToString()));
        var free = CreatePublishedBaseline(baselineRoot, "free");
        var query = new MultiPinQuery(paths, free);
        var cleaner = new BaselineRetentionCleaner(WorkspaceOwnership.Bootstrap(_root), query, query,
            new TimeRetentionPolicy(TimeSpan.Zero), new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("project");

        Assert.Equal([free], result.DeletedPaths);
        Assert.All(paths.Values, path => Assert.True(Directory.Exists(path)));
        Assert.False(Directory.Exists(free));
    }

    /// <summary>Guards the production shape: a published Baseline is a directory, not a file.</summary>
    private static string CreatePublishedBaseline(string baselineRoot, string digest)
    {
        var path = Path.Combine(baselineRoot, digest);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, digest + ".fwdata"), "baseline");
        Directory.CreateDirectory(Path.Combine(path, "WritingSystemStore"));
        Directory.CreateDirectory(Path.Combine(path, "SharedSettings"));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class BaselineQuery(string oldPath, string pinnedPath) : IPublishedBaselineQuery, IBaselinePinQuery
    {
        public IReadOnlyList<PublishedBaseline> ListPublished(string projectKey) =>
            [new(oldPath, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true),
             new(pinnedPath, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true)];
        public BaselinePinSources GetPinSources(string baselinePath) =>
            baselinePath == pinnedPath ? BaselinePinSources.ActiveJob : BaselinePinSources.None;
    }

    private sealed class SingleUnpinnedBaselineQuery(string path) : IPublishedBaselineQuery, IBaselinePinQuery
    {
        public IReadOnlyList<PublishedBaseline> ListPublished(string projectKey) =>
            [new(path, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true)];
        public BaselinePinSources GetPinSources(string baselinePath) => BaselinePinSources.None;
    }

    private sealed class MultiPinQuery(IReadOnlyDictionary<BaselinePinSources, string> paths, string free) : IPublishedBaselineQuery, IBaselinePinQuery
    {
        public IReadOnlyList<PublishedBaseline> ListPublished(string projectKey) =>
            paths.Values.Append(free)
                .Select(path => new PublishedBaseline(path, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), true, true))
                .ToArray();

        public BaselinePinSources GetPinSources(string baselinePath) =>
            paths.FirstOrDefault(pair => pair.Value == baselinePath).Key;
    }

    private sealed class FixedClock(string value) : IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
