using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Projects;

/// <summary>Describes whether a keyed project runtime can admit ordinary work.</summary>
public enum ProjectRuntimeAdmission
{
    Opening,
    Recovering,
    Ready,
    Rejected,
    Disposed
}

/// <summary>Owns one opened Motif store, its repositories, recovery, and operation admission.</summary>
public sealed class ProjectRuntime : IDisposable
{
    private readonly object _state = new();
    private readonly ProjectOperationGate _operations = new();
    private readonly Func<bool> _hasLiveHost;
    private readonly Func<bool> _hasPendingEvents;
    private int _activeOperations;
    private bool _draining;
    private bool _disposed;

    private ProjectRuntime(ProjectLocator project, string workspaceKey, MotifDatabase database,
        JobRepository jobs, BaselineRepository baselines, Func<bool> hasLiveHost, Func<bool> hasPendingEvents)
    {
        Project = project;
        WorkspaceKey = workspaceKey;
        Database = database;
        Jobs = jobs;
        Baselines = baselines;
        _hasLiveHost = hasLiveHost;
        _hasPendingEvents = hasPendingEvents;
        Admission = ProjectRuntimeAdmission.Opening;
    }

    /// <summary>Gets the canonical project locator owned by this runtime.</summary>
    public ProjectLocator Project { get; }

    /// <summary>Gets the stable key used for all runtime and job routing.</summary>
    public string WorkspaceKey { get; }

    /// <summary>Gets the exclusively opened Motif store; callers must hold an operation lease while accessing it.</summary>
    public MotifDatabase Database { get; }

    /// <summary>Gets repositories bound to the store; callers must hold an operation lease while accessing them.</summary>
    public JobRepository Jobs { get; }

    internal BaselineRepository Baselines { get; }

    /// <summary>Gets the current admission state.</summary>
    public ProjectRuntimeAdmission Admission { get; private set; }

    internal bool HasActiveOperation
    {
        get { lock (_state) return _activeOperations != 0; }
    }

    internal static ProjectRuntime Open(ProjectLocator project, string workspaceKey,
        ProjectDatabaseCatalog catalog,
        Func<JobRepository, string, WorkerRecoveryCoordinator> recoveryFactory,
        Func<DateTimeOffset>? now = null,
        Func<bool>? hasLiveHost = null, Func<bool>? hasPendingEvents = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(recoveryFactory);
        var database = catalog.OpenOwned(project);
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var runtime = new ProjectRuntime(project, workspaceKey, database, jobs, baselines,
            hasLiveHost ?? (() => false), hasPendingEvents ?? (() => false));
        try
        {
            runtime.Admission = ProjectRuntimeAdmission.Recovering;
            recoveryFactory(jobs, workspaceKey).RecoverStartup(workspaceKey,
                (now ?? (() => DateTimeOffset.UtcNow))());
            runtime.Admission = ProjectRuntimeAdmission.Ready;
            return runtime;
        }
        catch
        {
            runtime.Admission = ProjectRuntimeAdmission.Rejected;
            runtime.DisposeResources();
            throw;
        }
    }

    /// <summary>Acquires a shared lease for the whole lifetime of a runtime repository operation.</summary>
    public async Task<IDisposable> AcquireOperationAsync(CancellationToken cancellationToken)
    {
        EnterAdmission();
        try
        {
            var gateLease = await _operations.AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
            return new OperationLease(this, gateLease);
        }
        catch
        {
            ExitAdmission();
            throw;
        }
    }

    /// <summary>Acquires an exclusive lease which waits for all shared work and blocks later shared work.</summary>
    public async Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        EnterAdmission();
        try
        {
            var gateLease = await _operations.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);
            return new OperationLease(this, gateLease);
        }
        catch
        {
            ExitAdmission();
            throw;
        }
    }

    private bool TryAcquireOperationLease(out IDisposable lease)
    {
        EnterAdmission();
        if (_operations.TryAcquireOperation(out var gateLease))
        {
            lease = new OperationLease(this, gateLease);
            return true;
        }
        ExitAdmission();
        lease = null!;
        return false;
    }

    /// Mirrors the disposed/rejected pre-check every non-throwing lease acquisition needs before entering admission.
    private bool TryAcquireOperationLeaseIfReady(out IDisposable lease)
    {
        lock (_state)
        {
            if (_disposed || Admission is ProjectRuntimeAdmission.Rejected or ProjectRuntimeAdmission.Disposed)
            {
                lease = null!;
                return false;
            }
        }
        return TryAcquireOperationLease(out lease);
    }

    /// <summary>Releases resources after the registry has proven that no work can enter this runtime.</summary>
    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            Admission = ProjectRuntimeAdmission.Disposed;
        }
        DisposeResources();
    }

    internal bool TryBeginReleaseIfIdle()
    {
        if (!TryAcquireOperationLeaseIfReady(out var operation)) return false;
        bool active;
        using (operation) active = Jobs.ListActive(WorkspaceKey).Count != 0;
        lock (_state)
        {
            if (_disposed || _draining || _activeOperations != 0 || active ||
                _hasLiveHost() || _hasPendingEvents()) return false;
            _draining = true;
            return true;
        }
    }

    internal void CancelRelease()
    {
        lock (_state) _draining = false;
    }

    private void EnterAdmission()
    {
        lock (_state)
        {
            if (Admission != ProjectRuntimeAdmission.Ready || _disposed || _draining)
                throw new InvalidOperationException("The project runtime is not ready for operations.");
            _activeOperations++;
        }
    }

    private void ExitAdmission()
    {
        lock (_state) _activeOperations--;
    }

    private void DisposeResources()
    {
        Exception? gateFailure = null;
        try { _operations.Dispose(); }
        catch (Exception exception) { gateFailure = exception; }
        try { Database.Dispose(); }
        catch (Exception exception)
        {
            if (gateFailure is not null) throw new AggregateException(gateFailure, exception);
            throw;
        }
        if (gateFailure is not null) throw gateFailure;
    }

    private sealed class OperationLease : IDisposable
    {
        private ProjectRuntime? _runtime;
        private readonly IDisposable _gateLease;

        public OperationLease(ProjectRuntime runtime, IDisposable gateLease)
        {
            _runtime = runtime;
            _gateLease = gateLease;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _runtime, null) is not { } runtime) return;
            _gateLease.Dispose();
            runtime.ExitAdmission();
        }
    }
}
