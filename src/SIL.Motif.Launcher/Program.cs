using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Runner;

namespace SIL.Motif.Launcher;

/// <summary>Signals that no safe worker startup or connection path completed.</summary>
public class WorkerLaunchException : InvalidOperationException
{
    /// <summary>Creates a launcher diagnostic with optional local failure details.</summary>
    public WorkerLaunchException(string message, Exception? innerException = null, bool noCompatibleWorker = false)
        : base(message, innerException)
    {
        NoCompatibleWorker = noCompatibleWorker;
    }

    /// <summary>Whether the caller should report an install or update requirement.</summary>
    public bool NoCompatibleWorker { get; }
}

/// <summary>Reports an invalid or unavailable immutable worker registration.</summary>
public sealed class WorkerCatalogException : WorkerLaunchException
{
    /// <summary>Creates an actionable catalog diagnostic.</summary>
    public WorkerCatalogException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports that no registered worker matches the connecting client.</summary>
public sealed class NoCompatibleWorkerException : WorkerLaunchException
{
    /// <summary>Creates an install or update diagnostic.</summary>
    public NoCompatibleWorkerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Provides a monotonic time source for bounded startup.</summary>
public interface ILauncherClock
{
    /// <summary>The current monotonic timestamp.</summary>
    long Timestamp { get; }

    /// <summary>Ticks per second for <see cref="Timestamp"/>.</summary>
    long Frequency { get; }
}

/// <summary>Provides an injectable bounded delay between endpoint probes.</summary>
public interface ILauncherDelay
{
    /// <summary>Delays without extending the launcher's overall deadline.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Reports whether a job runner already holds the user-scoped owner mutex.</summary>
/// <remarks>
/// A runner answers no requests, so liveness cannot be confirmed by talking to it. What it does hold, for
/// exactly as long as it runs, is the named mutex that makes it the one runner for this user. Probing that
/// mutex is therefore the whole of "is one already running", and it is a seam so a test can drive startup
/// without starting a process.
/// </remarks>
public interface IRunnerPresence
{
    /// <summary>Whether some process currently owns the runner mutex for this user.</summary>
    bool IsRunning(string ownerMutexName);
}

/// <summary>Probes the real named mutex a running job runner holds.</summary>
public sealed class NamedMutexRunnerPresence : IRunnerPresence
{
    /// <inheritdoc />
    public bool IsRunning(string ownerMutexName)
    {
        if (string.IsNullOrWhiteSpace(ownerMutexName))
            throw new ArgumentException("An owner mutex name is required.", nameof(ownerMutexName));
        // Opening succeeds only while some process holds it; the handle is released immediately.
        if (!Mutex.TryOpenExisting(ownerMutexName, out var existing))
            return false;
        using (existing)
            return true;
    }
}

/// <summary>Starts one exact registered worker executable.</summary>
public interface IWorkerProcessStarter
{
    /// <summary>Starts the worker without accepting caller-supplied arguments.</summary>
    IWorkerProcess Start(InstalledWorker worker);
}

/// <summary>Reports enough process state for bounded launcher startup polling.</summary>
public interface IWorkerProcess : IDisposable
{
    /// <summary>Whether the process has already exited.</summary>
    bool HasExited { get; }

    /// <summary>The exit code when the process has exited.</summary>
    int ExitCode { get; }

    /// <summary>The exact shell-free startup configuration used by the process seam.</summary>
    ProcessStartInfo StartInfo { get; }

    /// <summary>Terminates a rejected candidate and returns only after its process exit is confirmed.</summary>
    void Terminate();
}

/// <summary>Connects to an existing worker or starts one registered compatible installation.</summary>
public sealed class WorkerLauncher
{
    private readonly InstalledWorkerCatalog _catalog;
    private readonly IRunnerPresence _presence;
    private readonly IWorkerProcessStarter _processStarter;
    private readonly string _ownerMutexName;
    private readonly TimeSpan _startupTimeout;
    private readonly ILauncherClock _clock;
    private readonly ILauncherDelay _delay;

    /// <summary>Creates a launcher with injectable catalog, presence, process, and mutex seams.</summary>
    public WorkerLauncher(InstalledWorkerCatalog catalog, IRunnerPresence presence,
        IWorkerProcessStarter processStarter, string ownerMutexName, TimeSpan startupTimeout,
        ILauncherClock? clock = null, ILauncherDelay? delay = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _presence = presence ?? throw new ArgumentNullException(nameof(presence));
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        if (string.IsNullOrWhiteSpace(ownerMutexName))
            throw new ArgumentException("A runner owner mutex name is required.", nameof(ownerMutexName));
        if (startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        _ownerMutexName = ownerMutexName;
        _startupTimeout = startupTimeout;
        _clock = clock ?? SystemLauncherClock.Instance;
        _delay = delay ?? SystemLauncherDelay.Instance;
    }

    /// <summary>Creates a launcher using the stable user catalog and worker endpoint.</summary>
    public WorkerLauncher()
        : this(new InstalledWorkerCatalog(), new NamedMutexRunnerPresence(), new WorkerProcessStarter(),
            OwnerMutexNameFor(CurrentSid()), TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>Ensures one runner able to open the given schema generation is running.</summary>
    public async Task EnsureRunningAsync(int requiredSchema, CancellationToken cancellationToken = default)
    {
        if (requiredSchema < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredSchema));
        var deadline = Deadline(_clock.Timestamp, _startupTimeout, _clock.Frequency);
        if (_presence.IsRunning(_ownerMutexName))
            return;

        EnsureBeforeDeadline(deadline);
        InstalledWorker candidate;
        try
        {
            candidate = WorkerSelector.SelectNewestCompatible(_catalog.List(), requiredSchema);
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            throw new WorkerCatalogException(
                "The installed worker catalog is corrupt or unavailable; reinstall or update Motif and try again.",
                exception);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(
            "No installed runner supports", StringComparison.Ordinal))
        {
            throw new WorkerLaunchException(
                "No compatible worker is installed; install or update Motif and try again.", exception,
                noCompatibleWorker: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
        {
            throw new WorkerCatalogException(
                "The installed worker catalog contains an ambiguous or invalid registration; reinstall or update " +
                "Motif and try again.", exception);
        }
        EnsureBeforeDeadline(deadline);
        try
        {
            candidate = _catalog.ValidateInstalled(candidate);
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            throw new WorkerCatalogException(
                "The selected worker registration changed or is unavailable; reinstall or update Motif and try again.",
                exception);
        }

        EnsureBeforeDeadline(deadline);
        IWorkerProcess process;
        try
        {
            process = _processStarter.Start(candidate);
        }
        catch (Exception exception)
        {
            throw new WorkerLaunchException(
                "The installed worker could not start; reinstall or update Motif and try again.", exception);
        }

        var accepted = false;
        Exception? launchFailure = null;
        try
        {
            while (_clock.Timestamp <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = deadline - _clock.Timestamp;
                if (remaining <= 0)
                    break;
                if (_presence.IsRunning(_ownerMutexName))
                {
                    accepted = true;
                    return;
                }
                if (process.HasExited)
                    throw new WorkerLaunchException(
                        "Runner startup failed: the installed runner exited before it took ownership; " +
                        "reinstall or update Motif and try again.");
                remaining = deadline - _clock.Timestamp;
                if (remaining <= 0)
                    break;
                var delay = TimeSpan.FromMilliseconds(Math.Min(50,
                    remaining * 1000.0 / _clock.Frequency));
                await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            throw new WorkerLaunchException(
                "Runner startup failed: it did not take ownership before startup timed out; " +
                "reinstall or update Motif and try again.");
        }
        catch (Exception exception)
        {
            launchFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (!accepted && !process.HasExited)
                    process.Terminate();
            }
            catch (Exception cleanupFailure) when (launchFailure is not null)
            {
                launchFailure.Data["workerTerminationFailure"] = cleanupFailure;
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception cleanupFailure) when (launchFailure is not null)
                {
                    launchFailure.Data["workerProcessDisposalFailure"] = cleanupFailure;
                }
            }
        }
    }

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

    /// <summary>The runner owner mutex name for one user namespace.</summary>
    public static string OwnerMutexNameFor(string userNamespace) =>
        @"Local\SIL.Motif.Worker.Owner." + userNamespace;

    private long Deadline(long start, TimeSpan timeout, long frequency)
    {
        var ticks = timeout.TotalSeconds * frequency;
        return start + (long)Math.Min(ticks, long.MaxValue - start);
    }

    private TimeSpan Remaining(long deadline)
    {
        var ticks = deadline - _clock.Timestamp;
        if (ticks <= 0)
            throw new WorkerLaunchException("Worker startup timed out; reinstall or update Motif and try again.");
        return TimeSpan.FromSeconds(ticks / (double)_clock.Frequency);
    }

    private void EnsureBeforeDeadline(long deadline)
    {
        if (_clock.Timestamp >= deadline)
            throw new WorkerLaunchException("Worker startup timed out; reinstall or update Motif and try again.");
    }

    private sealed class SystemLauncherClock : ILauncherClock
    {
        public static readonly SystemLauncherClock Instance = new SystemLauncherClock();
        public long Timestamp => Stopwatch.GetTimestamp();
        public long Frequency => Stopwatch.Frequency;
    }

    private sealed class SystemLauncherDelay : ILauncherDelay
    {
        public static readonly SystemLauncherDelay Instance = new SystemLauncherDelay();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}

/// <summary>Starts a registered worker with hidden, shell-free process creation.</summary>
public sealed class WorkerProcessStarter : IWorkerProcessStarter
{
    private readonly Func<ProcessStartInfo, Process?> _processFactory;

    /// <summary>Creates a process starter; the factory is injectable for safe startup configuration tests.</summary>
    public WorkerProcessStarter(Func<ProcessStartInfo, Process?>? processFactory = null)
    {
        _processFactory = processFactory ?? (info => Process.Start(info));
    }

    /// <inheritdoc />
    public IWorkerProcess Start(InstalledWorker worker)
    {
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));
        var info = new ProcessStartInfo
        {
            FileName = worker.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var process = _processFactory(info) ?? throw new InvalidOperationException("The worker process did not start.");
        return new StartedWorkerProcess(process, info);
    }

    private sealed class StartedWorkerProcess : IWorkerProcess
    {
        private readonly Process _process;
        private readonly ProcessStartInfo _startInfo;

        public StartedWorkerProcess(Process process, ProcessStartInfo startInfo)
        {
            _process = process;
            _startInfo = startInfo;
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public ProcessStartInfo StartInfo => _startInfo;

        public void Terminate()
        {
            try
            {
                if (_process.HasExited)
                    return;
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    if (_process.HasExited)
                        return;
                    throw;
                }
                if (!_process.WaitForExit(5000))
                    throw new WorkerLaunchException(
                        "The rejected worker did not exit within the termination deadline.");
            }
            catch (WorkerLaunchException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new WorkerLaunchException("The rejected worker could not be terminated.", exception);
            }
        }

        public void Dispose() => _process.Dispose();
    }
}

public static class Program
{
    private const int Success = 0;
    private const int NoCompatibleInstall = 2;
    private const int StartupFailure = 4;
    private const int CatalogFailure = 5;

    private static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, new WorkerLauncher()).ConfigureAwait(false);
    }

    /// <summary>Runs the bounded launcher command against an injectable orchestration seam.</summary>
    public static async Task<int> RunAsync(string[] args, WorkerLauncher launcher)
    {
        return await RunAsync(args, launcher, Console.Out, Console.Error).ConfigureAwait(false);
    }

    /// <summary>Runs the launcher with injected output streams for bounded CLI diagnostics.</summary>
    public static async Task<int> RunAsync(string[] args, WorkerLauncher launcher,
        TextWriter output, TextWriter error)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (error is null)
            throw new ArgumentNullException(nameof(error));
        try
        {
            var requiredSchema = Parse(args);
            if (launcher is null)
                throw new ArgumentNullException(nameof(launcher));
            await launcher.EnsureRunningAsync(requiredSchema).ConfigureAwait(false);
            await output.WriteLineAsync("The Motif job runner is running.").ConfigureAwait(false);
            return Success;
        }
        catch (WorkerCatalogException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return CatalogFailure;
        }
        catch (WorkerLaunchException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return exception.NoCompatibleWorker ? NoCompatibleInstall : StartupFailure;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is FormatException)
        {
            await error.WriteLineAsync("The required schema generation is invalid.").ConfigureAwait(false);
            return StartupFailure;
        }
    }

    private static int Parse(string[] args)
    {
        if (args is null || args.Length > 4)
            throw new ArgumentException("Too many launcher arguments.", nameof(args));
        var requiredSchema = 1;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--required-schema")
                throw new ArgumentException("Unknown launcher option.");
            if (++index >= args.Length)
                throw new ArgumentException("A launcher option is missing its value.");
            if (!int.TryParse(args[index], out requiredSchema))
                throw new FormatException("The required schema generation is invalid.");
        }
        return requiredSchema;
    }
}
