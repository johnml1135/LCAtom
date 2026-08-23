using SIL.Motif.Contract.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Coordinates cleanup and durable recovery for one already-open project.</summary>
public sealed class WorkerRecoveryCoordinator
{
    private const int MaximumReportedFailures = 32;
    private readonly WorkerRecovery _recovery;
    private readonly WorkspaceCleaner _cleaner;

    public WorkerRecoveryCoordinator(WorkerRecovery recovery, WorkspaceCleaner cleaner)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
    }

    public StartupRecoveryResult RecoverStartup(string projectKey, DateTimeOffset now)
    {
        var cleanup = _cleaner.CleanupStartup(ProjectWorkspaceKey.StorageSegment(projectKey));
        var recovery = _recovery.RecoverInterruptedJobs(now);
        return new StartupRecoveryResult(recovery, Limit(cleanup));
    }

    public WorkspaceCleanupResult CleanupTerminal(string projectKey, string jobId) =>
        Limit(_cleaner.CleanupJob(ProjectWorkspaceKey.StorageSegment(projectKey), jobId));

    private static WorkspaceCleanupResult Limit(WorkspaceCleanupResult result) =>
        result.Failures.Count <= MaximumReportedFailures
            ? result
            : result with { Failures = result.Failures.Take(MaximumReportedFailures).ToArray() };
}

/// <summary>Reports the cleanup diagnostics and durable recovery for one project startup.</summary>
public sealed record StartupRecoveryResult(RecoveryResult Recovery, WorkspaceCleanupResult Cleanup);
