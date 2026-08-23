using System.Text.Json;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class ProjectRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new("2026-08-23T12:00:00Z");

    public ProjectRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EquivalentLocatorsShareOneRecoveredRuntimeAndDatabase()
    {
        var first = Project("C:/workspace/./demo.fwdata", "project");
        var equivalent = Project("c:/workspace/demo.fwdata", "project");
        using var work = new WorkerWorkTracker();
        using var registry = Registry(work);

        var left = registry.GetOrOpen(first);
        var right = registry.GetOrOpen(equivalent);

        Assert.Same(left, right);
        Assert.Equal(ProjectWorkspaceKey.Compute(first), left.WorkspaceKey);
        Assert.Equal(ProjectDatabaseCatalog.DatabasePathFor(first), left.Database.FullPath);
    }

    [Fact]
    public void ASecondRegistryCannotOwnAnOpenProjectDatabase()
    {
        var project = Project("C:/workspace/ownership.fwdata", "project");
        using var firstWork = new WorkerWorkTracker();
        using var secondWork = new WorkerWorkTracker();
        using var first = Registry(firstWork);
        using var second = Registry(secondWork);
        var runtime = first.GetOrOpen(project);

        Assert.Throws<IOException>(() => second.GetOrOpen(project));
        runtime.Dispose();
        var reopened = second.GetOrOpen(project);
        Assert.NotSame(runtime, reopened);
    }

    [Fact]
    public void StartupCleanupAndRecoveryCompleteBeforeReady()
    {
        var project = Project("C:/workspace/recovery.fwdata", "project");
        using var work = new WorkerWorkTracker();
        using var registry = Registry(work);
        var key = ProjectWorkspaceKey.Compute(project);
        var ownedRoot = Path.Combine(_root, "owned");
        var storageKey = ProjectWorkspaceKey.StorageSegment(key);
        Directory.CreateDirectory(Path.Combine(ownedRoot, storageKey, "work", "orphan"));
        File.WriteAllText(Path.Combine(ownedRoot, ".motif-worker-root"), "SIL.Motif.WorkerRoot.v1");
        var runtime = registry.GetOrOpen(project);

        Assert.Equal(ProjectRuntimeAdmission.Ready, runtime.Admission);
        Assert.False(Directory.Exists(Path.Combine(ownedRoot, storageKey, "work", "orphan")));
    }

    [Fact]
    public void RejectedOpenReleasesOwnershipAndRemovesTheRuntimeEntry()
    {
        var project = Project("C:/workspace/rejected.fwdata", "project");
        using var work = new WorkerWorkTracker();
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        var attempts = 0;
        using var registry = new ProjectRuntimeRegistry(catalog,
            (jobs, key) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("recovery failed");
                return new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                    new WorkspaceCleaner(ownership));
            }, work, () => _clock.UtcNow);

        Assert.Throws<InvalidOperationException>(() => registry.GetOrOpen(project));
        var runtime = registry.GetOrOpen(project);

        Assert.Equal(ProjectRuntimeAdmission.Ready, runtime.Admission);
    }

    [Fact]
    public async Task ExclusiveWorkWaitsAheadOfLaterSharedWork()
    {
        using var gate = new ProjectOperationGate();
        using var first = await gate.AcquireOperationAsync(CancellationToken.None);
        var exclusive = gate.AcquireExclusiveAsync(CancellationToken.None);
        var laterShared = gate.AcquireOperationAsync(CancellationToken.None);

        Assert.False(exclusive.IsCompleted);
        Assert.False(laterShared.IsCompleted);
        first.Dispose();
        using var exclusiveLease = await exclusive.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(laterShared.IsCompleted);
        exclusiveLease.Dispose();
        using var laterSharedLease = await laterShared.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ActiveOperationPreventsIdleReleaseAndReopenCreatesFreshRuntime()
    {
        var project = Project("C:/workspace/release.fwdata", "project");
        using var work = new WorkerWorkTracker();
        using var registry = Registry(work);
        var runtime = registry.GetOrOpen(project);
        using var lease = await runtime.AcquireOperationAsync(CancellationToken.None);

        Assert.False(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
        lease.Dispose();
        Assert.True(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.AcquireOperationAsync(CancellationToken.None));
        var reopened = registry.GetOrOpen(project);
        Assert.NotSame(runtime, reopened);
    }

    [Fact]
    public void PendingEventPreventsIdleReleaseUntilTheEventCompletes()
    {
        var project = Project("C:/workspace/events.fwdata", "project");
        var pending = true;
        using var work = new WorkerWorkTracker();
        using var registry = Registry(work, _ => pending);
        var runtime = registry.GetOrOpen(project);

        Assert.False(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
        pending = false;
        Assert.True(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
    }

    [Fact]
    public void HostRegistrationsAreKeyedAndUnregisterIsGenerationSafe()
    {
        var left = Project("C:/workspace/left.fwdata", "left");
        var right = Project("C:/workspace/right.fwdata", "right");
        using var hosts = new ProjectHostRegistry();
        var leftStream = new MemoryStream();
        var rightStream = new MemoryStream();
        using var leftGate = new SemaphoreSlim(1, 1);
        using var rightGate = new SemaphoreSlim(1, 1);
        hosts.Register(left, new ProjectHostRegistration("left-1", "session-1", 1, leftStream, leftGate));
        hosts.Register(right, new ProjectHostRegistration("right-1", "session-1", 1, rightStream, rightGate));

        Assert.Throws<ProjectHostBusyException>(() => hosts.Register(left,
            new ProjectHostRegistration("left-2", "session-2", 1, new MemoryStream(), leftGate)));
        hosts.Unregister(left, "left-stale");
        Assert.True(hosts.TryGet(left, out _));
        hosts.Unregister(left, "left-1");
        Assert.False(hosts.TryGet(left, out _));
        Assert.True(hosts.TryGet(right, out _));
    }

    [Fact]
    public void ActiveDurableJobHoldsAndThenReleasesWorkerLease()
    {
        var project = Project("C:/workspace/jobs.fwdata", "project");
        using var work = new WorkerWorkTracker();
        using var catalog = Registry(work);
        var runtime = catalog.GetOrOpen(project);
        runtime.Jobs.Create("job", runtime.WorkspaceKey, "dry-run", "{}", _clock.UtcNow.ToString("O"));
        runtime.RefreshWorkLease();
        Assert.True(runtime.HasActiveWork);
        runtime.Jobs.Transition("job", JobStatus.Running);
        runtime.Jobs.Transition("job", JobStatus.Completed);
        runtime.RefreshWorkLease();
        Assert.False(runtime.HasActiveWork);
    }

    private ProjectRuntimeRegistry Registry(WorkerWorkTracker work, Func<string, bool>? hasPendingEvents = null)
    {
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        return new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                new WorkspaceCleaner(ownership)), work, () => _clock.UtcNow, hasPendingEvents);
    }

    private ProjectLocator Project(string path, string identity) =>
        new(Path.Combine(_root, Path.GetFileName(path)), identity);

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FixedClock : IJobClock
    {
        public FixedClock(string value) => UtcNow = DateTimeOffset.Parse(value);
        public DateTimeOffset UtcNow { get; }
    }
}
