using System.Collections.Concurrent;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Projects;

/// <summary>Synchronization boundary shared by runtime idle checks and activity mutations.</summary>
public sealed class ProjectRuntimeActivity
{
    private readonly object _sync;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Creates a synchronization boundary callers can use around activity changes.</summary>
    public ProjectRuntimeActivity() : this(new object()) { }

    internal ProjectRuntimeActivity(object sync) => _sync = sync ?? throw new ArgumentNullException(nameof(sync));

    /// <summary>Acquires a keepalive lease for one canonical workspace.</summary>
    public IDisposable Acquire(string workspaceKey)
    {
        if (string.IsNullOrWhiteSpace(workspaceKey))
            throw new ArgumentException("A workspace key is required.", nameof(workspaceKey));
        lock (_sync)
        {
            _counts[workspaceKey] = _counts.TryGetValue(workspaceKey, out var count) ? count + 1 : 1;
            return new ActivityLease(this, workspaceKey);
        }
    }

    /// <summary>Acquires the keepalive lease held by a live host route.</summary>
    public IDisposable AcquireHost(string workspaceKey) => Acquire(workspaceKey);

    /// <summary>Acquires the keepalive lease held by a pending worker event.</summary>
    public IDisposable AcquirePendingEvent(string workspaceKey) => Acquire(workspaceKey);

    internal object SyncRoot => _sync;

    internal bool IsActive(string workspaceKey)
    {
        lock (_sync) return _counts.TryGetValue(workspaceKey, out var count) && count != 0;
    }

    private void Release(string workspaceKey)
    {
        lock (_sync)
        {
            if (!_counts.TryGetValue(workspaceKey, out var count)) return;
            if (count == 1) _counts.Remove(workspaceKey);
            else _counts[workspaceKey] = count - 1;
        }
    }

    private sealed class ActivityLease : IDisposable
    {
        private ProjectRuntimeActivity? _owner;
        private readonly string _workspaceKey;

        public ActivityLease(ProjectRuntimeActivity owner, string workspaceKey)
        {
            _owner = owner;
            _workspaceKey = workspaceKey;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_workspaceKey);
    }
}

/// <summary>Owns at most one recovered runtime for each canonical project workspace key.</summary>
public sealed class ProjectRuntimeRegistry : IDisposable
{
    private readonly ProjectDatabaseCatalog _catalog;
    private readonly Func<JobRepository, string, WorkerRecoveryCoordinator> _recoveryFactory;
    private readonly WorkerWorkTracker _work;
    private readonly Func<DateTimeOffset>? _now;
    private readonly ProjectRuntimeActivity _activity;
    private readonly object _admission = new();
    private readonly ConcurrentDictionary<string, Lazy<ProjectRuntime>> _runtimes = new();
    private readonly ConcurrentDictionary<string, object> _lifecycles = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>Creates a registry with injected store, recovery, and keepalive boundaries.</summary>
    public ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, ProjectRuntimeActivity activity,
        Func<DateTimeOffset>? now = null)
        : this(catalog, recoveryFactory, work, now,
            activity ?? throw new ArgumentNullException(nameof(activity))) { }

    internal ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, Func<DateTimeOffset>? now, ProjectHostRegistry? hosts)
        : this(catalog, recoveryFactory, work, now, hosts is null
            ? throw new ArgumentNullException(nameof(hosts))
            : hosts.Activity) { }

    private ProjectRuntimeRegistry(ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        WorkerWorkTracker work, Func<DateTimeOffset>? now,
        ProjectRuntimeActivity activity)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _recoveryFactory = recoveryFactory ?? throw new ArgumentNullException(nameof(recoveryFactory));
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _now = now;
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
            Lazy<ProjectRuntime> lazy;
            lock (_admission)
            {
                ThrowIfDisposed();
                lazy = _runtimes.GetOrAdd(key, _ => new Lazy<ProjectRuntime>(
                    () => ProjectRuntime.Open(canonical, key, _catalog, _recoveryFactory, _work, _now,
                        () => _activity.IsActive(key), () => _activity.IsActive(key)),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            }
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
        lock (_activity.SyncRoot)
        lock (lifecycle)
        {
            lock (_admission)
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
    }

    /// <summary>Disposes all opened project runtimes and reports every disposal failure.</summary>
    public void Dispose()
    {
        KeyValuePair<string, Lazy<ProjectRuntime>>[] snapshot;
        lock (_admission)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            snapshot = _runtimes.ToArray();
            _runtimes.Clear();
        }
        var failures = new List<Exception>();
        foreach (var pair in snapshot)
        {
            var lifecycle = _lifecycles.GetOrAdd(pair.Key, static _ => new object());
            lock (lifecycle)
            {
                var lazy = pair.Value;
                if (!lazy.IsValueCreated) continue;
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
