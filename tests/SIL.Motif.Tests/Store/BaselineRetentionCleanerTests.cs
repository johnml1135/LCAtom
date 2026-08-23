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

    [Fact]
    public void EveryDurablePinSourcePreventsDeletionAndUnpinnedFileIsRemoved()
    {
        var baselineRoot = Path.Combine(_root, "project", "baseline");
        var paths = Enum.GetValues<BaselinePinSources>().Where(source => source != BaselinePinSources.None)
            .ToDictionary(source => source, source => Path.Combine(baselineRoot, source + ".fwdata"));
        var free = Path.Combine(baselineRoot, "free.fwdata");
        foreach (var path in paths.Values.Append(free)) File.WriteAllText(path, "baseline");
        var query = new MultiPinQuery(paths);
        var cleaner = new BaselineRetentionCleaner(new WorkspaceOwnership(_root), query, query,
            new ArchivePolicy(TimeSpan.Zero), new FixedClock("2026-08-23T00:00:00Z"));

        var result = cleaner.Clean("project");

        Assert.Equal([free], result.DeletedPaths);
        Assert.All(paths.Values, path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(free));
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

    private sealed class MultiPinQuery(IReadOnlyDictionary<BaselinePinSources, string> paths) : IPublishedBaselineQuery, IBaselinePinQuery
    {
        public IReadOnlyList<PublishedBaseline> ListPublished(string projectKey) =>
            paths.Values.Append(Path.Combine(Path.GetDirectoryName(paths.Values.First())!, "free.fwdata"))
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
