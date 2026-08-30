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
    // These waits are for the thread pool to hand out a thread, not for anything under test; be patient.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new("2026-08-23T12:00:00Z");

    public ProjectRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EquivalentLocatorsShareOneRecoveredRuntimeAndDatabase()
    {
        var first = Project("C:/workspace/./demo.fwdata", "project");
        var equivalent = Project("c:/workspace/demo.fwdata", "project");
        using var registry = Registry();

        var left = registry.GetOrOpen(first);
        var right = registry.GetOrOpen(equivalent);

        Assert.Same(left, right);
        Assert.Equal(ProjectWorkspaceKey.Compute(first), left.WorkspaceKey);
        Assert.Equal(ProjectDatabaseCatalog.DatabasePathFor(first), left.Database.FullPath);
    }

    [Fact]
    public void ASecondRegistryMayOpenAProjectDatabaseAlreadyOpen()
    {
        var project = Project("C:/workspace/ownership.fwdata", "project");
        using var first = Registry();
        using var second = Registry();
        var runtime = first.GetOrOpen(project);

        // Both are open at once by design; excluding one would make the database unusable as a rendezvous.
        var concurrent = second.GetOrOpen(project);

        Assert.NotSame(runtime, concurrent);
        runtime.Dispose();
        Assert.NotSame(runtime, second.GetOrOpen(project));
    }

    [Fact]
    public void StartupCleanupAndRecoveryCompleteBeforeReady()
    {
        var project = Project("C:/workspace/recovery.fwdata", "project");
        using var registry = Registry();
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
            }, new ProjectRuntimeActivity(), () => _clock.UtcNow);

        Assert.Throws<InvalidOperationException>(() => registry.GetOrOpen(project));
        Assert.False(registry.TryGet(ProjectWorkspaceKey.Compute(project), out _));
        using var otherRegistry = new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                new WorkspaceCleaner(ownership)), new ProjectRuntimeActivity(),
            () => _clock.UtcNow);
        var other = otherRegistry.GetOrOpen(project);
        other.Dispose();
        var runtime = registry.GetOrOpen(project);

        Assert.Equal(ProjectRuntimeAdmission.Ready, runtime.Admission);
        Assert.NotSame(other, runtime);
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
        using var exclusiveLease = await exclusive.WaitAsync(Patience);
        Assert.False(laterShared.IsCompleted);
        exclusiveLease.Dispose();
        using var laterSharedLease = await laterShared.WaitAsync(Patience);
    }

    [Fact]
    public async Task GateCancellationAndDisposalDoNotCorruptWaitingExclusiveCount()
    {
        using var gate = new ProjectOperationGate();
        using var held = await gate.AcquireOperationAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.AcquireExclusiveAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var later = gate.AcquireOperationAsync(CancellationToken.None);
        held.Dispose();
        using var laterLease = await later.WaitAsync(Patience);
        laterLease.Dispose();
        var dispose = Task.Run(gate.Dispose);
        await dispose.WaitAsync(Patience);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gate.AcquireOperationAsync(CancellationToken.None));
    }

    [Fact]
    public void TryAcquireOperationGrantsALeaseWhenTheGateIsFree()
    {
        using var gate = new ProjectOperationGate();

        Assert.True(gate.TryAcquireOperation(out var lease));
        lease.Dispose();
    }

    [Fact]
    public async Task TryAcquireOperationFailsWhileExclusiveIsHeldAndSucceedsAfterItIsReleased()
    {
        using var gate = new ProjectOperationGate();
        var exclusive = await gate.AcquireExclusiveAsync(CancellationToken.None);

        Assert.False(gate.TryAcquireOperation(out var busyLease));
        Assert.Null(busyLease);

        exclusive.Dispose();
        Assert.True(gate.TryAcquireOperation(out var freeLease));
        freeLease.Dispose();
    }

    [Fact]
    public async Task TryAcquireOperationFailureDoesNotCorruptALaterBlockingAcquire()
    {
        using var gate = new ProjectOperationGate();
        var exclusive = await gate.AcquireExclusiveAsync(CancellationToken.None);
        Assert.False(gate.TryAcquireOperation(out _));

        var blockingShared = gate.AcquireOperationAsync(CancellationToken.None);
        Assert.False(blockingShared.IsCompleted);
        exclusive.Dispose();
        using var sharedLease = await blockingShared.WaitAsync(Patience);
    }

    [Fact]
    public async Task RuntimeDisposeWaitsForAnActiveOperationBeforeClosingTheDatabase()
    {
        var project = Project("C:/workspace/dispose-active.fwdata", "project");
        using var registry = Registry();
        var runtime = registry.GetOrOpen(project);
        var lease = await runtime.AcquireOperationAsync(CancellationToken.None);
        var disposing = Task.Run(runtime.Dispose);
        await Task.Delay(50);
        Assert.False(disposing.IsCompleted);
        lease.Dispose();
        await disposing.WaitAsync(Patience);
        Assert.Equal(ProjectRuntimeAdmission.Disposed, runtime.Admission);
    }

    [Fact]
    public async Task DifferentProjectRecoveryDoesNotWaitForBlockedRecovery()
    {
        var first = Project("C:/workspace/blocked-a.fwdata", "a");
        var second = Project("C:/workspace/blocked-b.fwdata", "b");
        var firstKey = ProjectWorkspaceKey.Compute(first);
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "parallel-owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registry = new ProjectRuntimeRegistry(catalog, (jobs, key) =>
        {
            if (key == firstKey)
            {
                entered.Set();
                release.Wait(Patience);
            }
            return new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                new WorkspaceCleaner(ownership));
        }, new ProjectRuntimeActivity(), () => _clock.UtcNow);

        var openingFirst = Task.Run(() => registry.GetOrOpen(first));
        Assert.True(entered.Wait(Patience));
        var openingSecond = Task.Run(() => registry.GetOrOpen(second));
        await openingSecond.WaitAsync(Patience);
        release.Set();
        await openingFirst.WaitAsync(Patience);
    }

    [Fact]
    public async Task DisposalAdmissionBarrierDoesNotAllowAGetOrOpenToEscape()
    {
        var project = Project("C:/workspace/dispose-race.fwdata", "project");
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "dispose-race-owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registry = new ProjectRuntimeRegistry(catalog, (jobs, key) =>
        {
            entered.Set();
            release.Wait(Patience);
            return new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                new WorkspaceCleaner(ownership));
        }, new ProjectRuntimeActivity(), () => _clock.UtcNow);

        var opening = Task.Run(() => registry.GetOrOpen(project));
        Assert.True(entered.Wait(Patience));
        var disposing = Task.Run(registry.Dispose);
        Assert.Throws<ObjectDisposedException>(() => registry.GetOrOpen(project));
        release.Set();
        var opened = await opening.WaitAsync(Patience);
        await disposing.WaitAsync(Patience);
        Assert.Equal(ProjectRuntimeAdmission.Disposed, opened.Admission);
        Assert.False(registry.TryGet(ProjectWorkspaceKey.Compute(project), out _));
    }

    [Fact]
    public async Task ActiveOperationPreventsIdleReleaseAndReopenCreatesFreshRuntime()
    {
        var project = Project("C:/workspace/release.fwdata", "project");
        using var registry = Registry();
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
    public async Task TryReleaseIfIdleReturnsFalseWithoutBlockingWhileExclusiveLeaseIsHeld()
    {
        var project = Project("C:/workspace/exclusive-idle.fwdata", "project");
        using var registry = Registry();
        var runtime = registry.GetOrOpen(project);
        var exclusive = await runtime.AcquireExclusiveAsync(CancellationToken.None);

        var releasing = Task.Run(() => registry.TryReleaseIfIdle(runtime.WorkspaceKey));
        Assert.False(await releasing.WaitAsync(TimeSpan.FromSeconds(5)));

        exclusive.Dispose();
        Assert.True(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
    }

    [Fact]
    public void PendingEventPreventsIdleReleaseUntilTheEventCompletes()
    {
        var project = Project("C:/workspace/events.fwdata", "project");
        var activity = new ProjectRuntimeActivity();
        using var registry = Registry(activity);
        var runtime = registry.GetOrOpen(project);
        using var pendingLease = activity.Acquire(runtime.WorkspaceKey);

        Assert.False(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
        pendingLease.Dispose();
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
        hosts.Unregister(left, "left-stale", "session-stale");
        Assert.True(hosts.TryGet(left, out _));
        hosts.Unregister(left, "left-1", "session-1");
        Assert.False(hosts.TryGet(left, out _));
        hosts.Register(left, new ProjectHostRegistration("left-1", "session-2", 1, leftStream, leftGate));
        Assert.False(hosts.Unregister(left, "left-1", "session-1"));
        Assert.True(hosts.TryGet(left, out _));
        Assert.True(hosts.Unregister(left, "left-1", "session-2"));
        Assert.False(hosts.TryGet(left, out _));
        Assert.True(hosts.TryGet(right, out _));
    }

    [Fact]
    public void HostRegistrationRejectsInvalidGenerationAndTransportValues()
    {
        using var hosts = new ProjectHostRegistry();
        var project = Project("C:/workspace/invalid-host.fwdata", "project");
        using var stream = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);
        Assert.Throws<ArgumentException>(() => hosts.Register(project,
            new ProjectHostRegistration("connection", "", 1, stream, gate)));
        Assert.Throws<ArgumentOutOfRangeException>(() => hosts.Register(project,
            new ProjectHostRegistration("connection", "session", 0, stream, gate)));
        Assert.Throws<ArgumentNullException>(() => hosts.Register(project,
            new ProjectHostRegistration("connection", "session", 1, null!, gate)));
        Assert.Throws<ArgumentNullException>(() => hosts.Register(project,
            new ProjectHostRegistration("connection", "session", 1, stream, null!)));
    }

    [Fact]
    public void ActiveDurableJobPreventsIdleReleaseAndItsCompletionAllowsIt()
    {
        var project = Project("C:/workspace/jobs.fwdata", "project");
        using var registry = Registry();
        var runtime = registry.GetOrOpen(project);
        runtime.Jobs.Create("job", runtime.WorkspaceKey, "dry-run", "{}", _clock.UtcNow.ToString("O"));

        // Derived fresh from the durable row, not a cached lease: idleness is one statement on this connection.
        Assert.False(registry.TryReleaseIfIdle(runtime.WorkspaceKey));

        runtime.Jobs.Transition("job", JobStatus.Running);
        runtime.Jobs.Transition("job", JobStatus.Completed);

        Assert.True(registry.TryReleaseIfIdle(runtime.WorkspaceKey));
    }

    private ProjectRuntimeRegistry Registry(ProjectRuntimeActivity? activity = null)
    {
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        return new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, _clock),
                new WorkspaceCleaner(ownership)), activity ?? new ProjectRuntimeActivity(), () => _clock.UtcNow);
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
