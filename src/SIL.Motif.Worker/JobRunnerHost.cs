using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker;

/// <summary>
/// The composition root of the per-user job runner: one owner mutex, one runtime registry, and the
/// per-project lanes its work is scheduled through.
/// </summary>
/// <remarks>
/// <para>
/// It answers no requests. Coordination with a <c>motif</c> invocation is the paired database and the
/// database's own owner lock, not a message, so this type has no endpoint, no connections, and no
/// protocol. What it owns is singularity — exactly one runner per Windows user, enforced by a named
/// mutex — and the lifetime of everything scheduled work needs.
/// </para>
/// <para>
/// The registry is composed once, before the runner starts, and is closed to further composition
/// afterwards. That ordering is what lets disposal be deterministic: lanes before runtimes, runtimes
/// before the mutex that admitted them.
/// </para>
/// </remarks>
public sealed class JobRunnerHost : IDisposable, IWorkerWorkTracker
{
    private readonly WorkerMutexOwner _ownerMutex;
    private readonly IWorkerWorkTracker _workTracker;
    private readonly ProjectHostRegistry _hostRegistry;
    private readonly ProjectHostReleaseCoordinator _hostReleases;
    private readonly ConcurrentDictionary<string, ProjectFreshnessTracker> _freshness =
        new ConcurrentDictionary<string, ProjectFreshnessTracker>(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly object _gate = new object();
    private ProjectRuntimeRegistry? _runtimeRegistry;
    private ProjectLaneRegistry? _projectLanes;
    private BaselineWorkspaceCatalog? _baselineWorkspaces;
    private string? _ownedTestRoot;
    private bool _ownsRuntimeRegistry;
    private Action<ProjectRuntimeRegistry>? _runtimeRegistryDisposeOverride;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a runner host for the current Windows user.</summary>
    public JobRunnerHost(IWorkerWorkTracker? workTracker = null)
        : this(CurrentSid(), workTracker)
    {
    }

    internal JobRunnerHost(string userNamespace, IWorkerWorkTracker? workTracker = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";
        OwnerName = GetOwnerMutexNameForNamespace(
            string.IsNullOrWhiteSpace(userNamespace) ? sid : userNamespace);
        _ownerMutex = new WorkerMutexOwner(OwnerName);
        _workTracker = workTracker ?? new WorkerWorkTracker();
        _hostRegistry = new ProjectHostRegistry();
        _hostReleases = new ProjectHostReleaseCoordinator();
    }

    /// <summary>Creates an isolated runner identity for tests only.</summary>
    internal static JobRunnerHost CreateForTests(string userNamespace, bool composeRuntime = true,
        string? workerRoot = null)
    {
        var host = new JobRunnerHost(userNamespace, null);
        var root = workerRoot ?? Path.Combine(
            Path.GetTempPath(), "motif-runner-test-" + Guid.NewGuid().ToString("N"));
        var ownership = WorkspaceOwnership.Bootstrap(root);
        host.ConfigureWorkspaces(ownership);
        host._ownedTestRoot = workerRoot is null ? root : null;
        if (composeRuntime)
        {
            var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
            host.CreateRuntimeRegistry(catalog,
                (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                    new WorkspaceCleaner(ownership)));
        }
        return host;
    }

    /// <summary>The named mutex which serializes runner ownership for one Windows user.</summary>
    public string OwnerName { get; }

    /// <summary>Whether this process currently owns the user-scoped runner mutex.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>Whether queued, running, or waiting work is keeping the runner alive.</summary>
    public bool HasQueuedRunningOrWaitingWork => _workTracker.HasQueuedRunningOrWaitingWork;

    internal ProjectLaneRegistry ProjectLanes => _projectLanes ??
        throw new InvalidOperationException("The project scheduler is not composed.");

    internal ProjectHostReleaseCoordinator HostReleases => _hostReleases;

    internal ProjectHostRegistry HostRegistry => _hostRegistry;

    /// <summary>Takes the user-scoped mutex, making this process the one runner.</summary>
    public bool TryAcquireOwnership()
    {
        lock (_gate)
        {
            if (IsOwner) return true;
            IsOwner = _ownerMutex.TryAcquire();
            return IsOwner;
        }
    }

    /// <summary>Closes composition; every later call that would add to it is refused.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _started = true;
        }
    }

    /// <summary>Creates the runtime registry and the per-project lanes bound to it.</summary>
    internal ProjectRuntimeRegistry CreateRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        Func<DateTimeOffset>? now = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started)
                throw new InvalidOperationException("Runtime composition is closed after the runner starts.");
            if (_runtimeRegistry is not null)
                throw new InvalidOperationException("The runner runtime registry is already composed.");
            if (_workTracker is not WorkerWorkTracker work)
                throw new InvalidOperationException("Runtime composition requires the worker work tracker.");
            _runtimeRegistry = new ProjectRuntimeRegistry(catalog, recoveryFactory, work, now, _hostRegistry);
            _projectLanes = new ProjectLaneRegistry(key =>
            {
                if (!_runtimeRegistry.TryGet(key, out var runtime))
                    throw new InvalidOperationException("A Ready project runtime is required for its lane.");
                return runtime.Baselines.GetCurrent(key)?.Token ??
                    throw new InvalidOperationException("A published Baseline is required for its lane.");
            });
            _ownsRuntimeRegistry = true;
            return _runtimeRegistry;
        }
    }

    /// <summary>Composes the Baseline workspace catalog this runner publishes into.</summary>
    internal void ConfigureWorkspaces(IWorkspaceOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started)
                throw new InvalidOperationException("Workspace composition is closed after the runner starts.");
            if (_baselineWorkspaces is not null)
                throw new InvalidOperationException("The runner workspace catalog is already composed.");
            _baselineWorkspaces = new BaselineWorkspaceCatalog(ownership);
        }
    }

    /// <summary>Injects a disposal failure for testing cleanup ordering and aggregation.</summary>
    internal void SetRuntimeRegistryDisposeOverrideForTests(Action<ProjectRuntimeRegistry> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        lock (_gate)
        {
            if (!_ownsRuntimeRegistry || _runtimeRegistry is null)
                throw new InvalidOperationException("Only an owned test registry can be overridden.");
            _runtimeRegistryDisposeOverride = dispose;
        }
    }

    /// <summary>Derives the runner owner mutex name for the current Windows user.</summary>
    public static string GetOwnerMutexName() => GetOwnerMutexNameForNamespace(CurrentSid());

    internal static string GetOwnerMutexNameForNamespace(string userNamespace) =>
        @"Local\SIL.Motif.Worker.Owner." + userNamespace;

    private static string CurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JobRunnerHost));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var failures = new List<Exception>();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
        }
        // Lanes before runtimes, runtimes before the mutex that admitted them.
        if (_projectLanes is not null)
        {
            try { _projectLanes.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (_ownsRuntimeRegistry && _runtimeRegistry is not null)
        {
            try { (_runtimeRegistryDisposeOverride ?? (registry => registry.Dispose()))(_runtimeRegistry); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (_ownedTestRoot is not null)
        {
            try { Directory.Delete(_ownedTestRoot, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { _hostRegistry.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        try { _hostReleases.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        _freshness.Clear();
        if (IsOwner)
        {
            try { _ownerMutex.Release(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { _ownerMutex.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        try { _shutdown.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Runner shutdown encountered cleanup failures.", failures);
    }
}
