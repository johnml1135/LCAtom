using SIL.Motif.Contract.Jobs;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class WorkspaceCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-workspace-" + Guid.NewGuid().ToString("N"));

    public WorkspaceCleanupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CleanupJobDeletesOnlyOneExactOwnedDirectoryAndStartupClearsAllChildren()
    {
        var project = Path.Combine(_root, "project");
        var work = Path.Combine(project, "work");
        Directory.CreateDirectory(Path.Combine(work, "job-1"));
        Directory.CreateDirectory(Path.Combine(work, "job-2"));
        File.WriteAllText(Path.Combine(work, "job-1", "candidate.tsv"), "candidate");
        var cleaner = new WorkspaceCleaner(new WorkspaceOwnership(_root));

        var one = cleaner.CleanupJob("project", "job-1");
        Assert.True(one.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(work, "job-1")));
        Assert.True(Directory.Exists(Path.Combine(work, "job-2")));
        var all = cleaner.CleanupStartup("project");
        Assert.True(all.Succeeded);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void StartupDeletesWorkerOwnedFilesIncludingDerivedLookingSuffixes()
    {
        var work = Path.Combine(_root, "project", "work");
        Directory.CreateDirectory(Path.Combine(work, "job"));
        File.WriteAllText(Path.Combine(work, "job", "candidate.fwdata"), "derived");
        File.WriteAllText(Path.Combine(work, "job", "candidate.motif.db"), "derived");

        var result = new WorkspaceCleaner(new WorkspaceOwnership(_root)).CleanupStartup("project");

        Assert.True(result.Succeeded);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public void BroadAndFileValuedRootsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceOwnership(Path.GetTempPath()));
        var file = Path.Combine(_root, "worker-root.txt");
        File.WriteAllText(file, "not a root");
        Assert.Throws<ArgumentException>(() => new WorkspaceOwnership(file));
    }

    [Fact]
    public void StartupRejectsAnEnumeratedEntryOutsideTheExactWorkRoot()
    {
        var work = Path.Combine(_root, "project", "work");
        var outside = Path.Combine(_root, "project", "outside.txt");
        var fileSystem = new OutsideChildFileSystem(work, outside);

        var result = new WorkspaceCleaner(new WorkspaceOwnership(_root), fileSystem).CleanupStartup("project");

        Assert.NotEmpty(result.Failures);
        Assert.False(fileSystem.Deleted);
    }

    [Theory]
    [InlineData("..", "job")]
    [InlineData("project", "..")]
    [InlineData("project", "job\\..\\other")]
    [InlineData("project", "job/other")]
    public void TraversalAndBroadTargetsAreReportedWithoutDeletion(string project, string job)
    {
        var sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var result = new WorkspaceCleaner(new WorkspaceOwnership(_root)).CleanupJob(project, job);
        Assert.False(result.Succeeded);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void EvictionRequiresDisuseAndNoLeaseOrDurableReference()
    {
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(project, "unclaimed"));
        var facts = new WorkspaceFacts
        {
            LastUsed = DateTimeOffset.Parse("2026-07-01T00:00:00Z")
        };
        var clock = new FixedClock("2026-08-23T00:00:00Z");
        var evictor = new ProjectWorkspaceEvictor(new WorkspaceOwnership(_root), facts, facts, facts, clock);

        Assert.Empty(evictor.Evict("project").Failures);
        Assert.False(Directory.Exists(project));
        Directory.CreateDirectory(project);
        facts.LiveLease = true;
        Assert.Empty(evictor.Evict("project").EvictedPaths);
        Assert.True(Directory.Exists(project));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class WorkspaceFacts : IProjectWorkspaceLease, IProjectWorkspaceReferences, IProjectWorkspaceLastUsed
    {
        public DateTimeOffset? LastUsed { get; set; }
        public bool LiveLease { get; set; }
        public bool DurableReference { get; set; }
        public bool HasLiveLease(string projectKey) => LiveLease;
        public bool HasDurableReference(string projectKey) => DurableReference;
        public DateTimeOffset? LastUsedUtc(string projectKey) => LastUsed;
    }

    private sealed class OutsideChildFileSystem(string work, string outside) : IWorkspaceFileSystem
    {
        public bool Deleted { get; private set; }
        public bool Exists(string path) => true;
        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, work, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory
                : FileAttributes.Normal;
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) =>
            string.Equals(path, work, StringComparison.OrdinalIgnoreCase) ? [outside] : [];
        public void DeleteFile(string path) => Deleted = true;
        public void DeleteDirectory(string path) => Deleted = true;
    }

    private sealed class FixedClock(string value) : IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
