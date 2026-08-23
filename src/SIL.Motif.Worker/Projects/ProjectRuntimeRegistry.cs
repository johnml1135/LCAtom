using System.Collections.Concurrent;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Projects;

/// <summary>Synchronization boundary shared by runtime idle checks and activity mutations.</summary>
public sealed class ProjectRuntimeActivity
{
    private readonly object _sync;

    /// <summary>Creates a synchronization boundary callers can use around activity changes.</summary>
    public ProjectRuntimeActivity() : this(new object()) { }

    internal ProjectRuntimeActivity(object sync) => _sync = sync ?? throw new ArgumentNullException(nameof(sync));

    /// <summary>Runs an activity mutation or observation under the runtime boundary.</summary>
    public void Execute(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync) action();
    }

    internal object SyncRoot => _sync;
}

/// <summary>Owns at most one recovered runtime for each canonical project workspace key.</summary>
public sealed class ProjectRuntimeRegistry : IDisposable
{
    private readonly ProjectDatabaseCatalog _catalog;
    private readonly Func<JobRepository, string, WorkerRecoveryCoordinator> _recoveryFactory;
    private readonly WorkerWorkTracker _work;
    private readonly Func<DateTimeOffset>? _now;
    private readonly Func<string, bool> _hasLiveHost;
    private readonly Func<string, bool> _hasPendingEvents;
    private readonly object _activity;
    private readonly ConcurrentDictionary<string, Lazy<ProjectRuntime>> _runtimes = new();
    private readonly ConcurrentDictionary<string, object> _lifecycles = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates a registry with injected store, recovery, and keepalive boundaries.</summary>
    public ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, Func<string, bool> hasLiveHost,
        Func<string, bool> hasPendingEvents, ProjectRuntimeActivity activity,
        Func<DateTimeOffset>? now = null)
        : this(catalog, recoveryFactory, work, now, hasLiveHost, hasPendingEvents,
            activity?.SyncRoot ?? throw new ArgumentNullException(nameof(activity))) { }

    internal ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, Func<DateTimeOffset>? now, ProjectHostRegistry? hosts,
        Func<string, bool> hasPendingEvents)
        : this(catalog, recoveryFactory, work, now, hosts is null
            ? throw new ArgumentNullException(nameof(hosts))
            : hosts.HasRegistration, hasPendingEvents, new ProjectRuntimeActivity(hosts.ActivitySync)) { }

    private ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, Func<DateTimeOffset>? now,
        Func<string, bool> hasLiveHost, Func<string, bool> hasPendingEvents, object activity)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _recoveryFactory = recoveryFactory ?? throw new ArgumentNullException(nameof(recoveryFactory));
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _now = now;
        _hasLiveHost = hasLiveHost ?? throw new ArgumentNullException(nameof(hasLiveHost));
        _hasPendingEvents = hasPendingEvents ?? throw new ArgumentNullException(nameof(hasPendingEvents));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    /// <summary>Gets or opens the one recovered runtime for a canonical workspace key.</summary>
    public ProjectRuntime GetOrOpen(ProjectLocator project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var canonical = project;
        var key = ProjectWorkspaceKey.Compute(canonical);
        var lifecycle = _lifecycles.GetOrAdd(key, static _ => new object());
        lock (lifecycle)
        {
            ThrowIfDisposed();
            var lazy = _runtimes.GetOrAdd(key, _ => new Lazy<ProjectRuntime>(
                () => ProjectRuntime.Open(canonical, key, _catalog, _recoveryFactory, _work, _now,
                    () => _hasLiveHost(key), () => _hasPendingEvents(key)),
                LazyThreadSafetyMode.ExecutionAndPublication));
            try { return lazy.Value; }
            catch
            {
                _runtimes.TryRemove(new KeyValuePair<string, Lazy<ProjectRuntime>>(key, lazy));
                throw;
            }
        }
    }

    /// <summary>Tries to find an already-open runtime without opening a database.</summary>
    public bool TryGet(string workspaceKey, out ProjectRuntime runtime)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey))
        {
            runtime = null!;
            return false;
        }
        var lifecycle = _lifecycles.GetOrAdd(workspaceKey, static _ => new object());
        lock (lifecycle)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_runtimes.TryGetValue(workspaceKey, out var lazy) ||
                !lazy.IsValueCreated)
            {
                runtime = null!;
                return false;
            }
            try
            {
                runtime = lazy.Value;
                return runtime.Admission == ProjectRuntimeAdmission.Ready;
            }
            catch { runtime = null!; return false; }
        }
    }

    /// <summary>Releases a runtime only when durable work, hosts, events, and operations are absent.</summary>
    public bool TryReleaseIfIdle(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey)) return false;
        var lifecycle = _lifecycles.GetOrAdd(workspaceKey, static _ => new object());
        lock (_activity)
        lock (lifecycle)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_runtimes.TryGetValue(workspaceKey, out var lazy) || !lazy.IsValueCreated) return false;
            ProjectRuntime runtime;
            try { runtime = lazy.Value; }
            catch { return false; }
            if (!runtime.TryBeginReleaseIfIdle()) return false;
            if (!_runtimes.TryRemove(new KeyValuePair<string, Lazy<ProjectRuntime>>(workspaceKey, lazy)))
            {
                runtime.CancelRelease();
                return false;
            }
            runtime.Dispose();
            return true;
        }
    }

    /// <summary>Disposes all opened project runtimes and reports every disposal failure.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var failures = new List<Exception>();
        foreach (var pair in _runtimes.ToArray())
        {
            var lifecycle = _lifecycles.GetOrAdd(pair.Key, static _ => new object());
            lock (lifecycle)
            {
                if (!_runtimes.TryRemove(pair.Key, out var lazy) || !lazy.IsValueCreated) continue;
                try { lazy.Value.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
            }
        }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ProjectRuntimeRegistry));
    }
}
