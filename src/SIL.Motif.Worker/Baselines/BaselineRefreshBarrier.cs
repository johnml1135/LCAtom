using System;
using System.Threading;
using System.Threading.Tasks;
using SIL.LCModel;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Worker.Baselines;

/// <summary>Why one Baseline refresh did not happen.</summary>
public enum BaselineRefreshOutcome
{
    /// <summary>The project was captured and the replacement published.</summary>
    Refreshed,

    /// <summary>Something else holds the project. Retryable once it lets go.</summary>
    ProjectInUse,

    /// <summary>The project was reachable and the capture itself failed.</summary>
    CaptureFailed,
}

/// <summary>The result of one attempt, carrying the reason when there is one.</summary>
public sealed record BaselineRefreshResult(BaselineRefreshOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == BaselineRefreshOutcome.Refreshed;
}

/// <summary>
/// Starts one Baseline refresh, or declines to because the project is held elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// A refresh needs the project, and FieldWorks may hold it. Opening a <c>.fwdata</c> project takes the
/// same file lock FieldWorks takes, so attempting the open answers the only question a refresh has — and
/// answers it with no cooperation from whoever holds it, which no arrangement between running processes
/// can do.
/// </para>
/// <para>
/// The probe is the real open rather than a look at the lock file, because a lock file that exists is not
/// the same fact as a project that cannot be opened, and only the second one matters here.
/// </para>
/// </remarks>
public sealed class BaselineRefreshBarrier
{
    private readonly Func<ProjectLocator, LcmCache> _open;

    /// <summary>Creates a barrier over the project-open seam that decides whether a refresh may run.</summary>
    public BaselineRefreshBarrier(Func<ProjectLocator, LcmCache> open) =>
        _open = open ?? throw new ArgumentNullException(nameof(open));

    /// <summary>Runs one capture while holding the project, or declines because something else holds it.</summary>
    public async Task<BaselineRefreshResult> RefreshAsync(ProjectLocator project,
        Func<LcmCache, CancellationToken, Task> capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(capture);

        LcmCache cache;
        try
        {
            cache = _open(project);
        }
        catch (LcmFileLockedException)
        {
            return new BaselineRefreshResult(BaselineRefreshOutcome.ProjectInUse,
                "The project is open in another program, so its Baseline cannot be refreshed now. " +
                "Close it and run this again.");
        }

        try
        {
            await capture(cache, cancellationToken).ConfigureAwait(false);
            return new BaselineRefreshResult(BaselineRefreshOutcome.Refreshed,
                "The Baseline was refreshed from the saved project.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The existing Baseline stays current: a failed capture replaces nothing.
            return new BaselineRefreshResult(BaselineRefreshOutcome.CaptureFailed, exception.Message);
        }
        finally
        {
            cache?.Dispose();
        }
    }
}
