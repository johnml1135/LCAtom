using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Launcher;

/// <summary>Signals that no worker endpoint could be contacted.</summary>
public sealed class WorkerEndpointUnavailableException : Exception
{
    /// <summary>Creates an endpoint-unavailable diagnostic.</summary>
    public WorkerEndpointUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Signals that an existing endpoint refused the client's compatibility request.</summary>
public sealed class WorkerEndpointIncompatibleException : InvalidOperationException
{
    /// <summary>Creates an endpoint-incompatibility diagnostic.</summary>
    public WorkerEndpointIncompatibleException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

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

/// <summary>Exposes only the connection lifetime needed by launcher orchestration.</summary>
public interface IWorkerConnection : IDisposable
{
    /// <summary>The handshake negotiated with the endpoint.</summary>
    WorkerHandshakeResult Negotiated { get; }
}

/// <summary>Connects to the stable endpoint through an injectable transport seam.</summary>
public interface IWorkerConnector
{
    /// <summary>Connects and confirms the requested handshake before returning.</summary>
    Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
        TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Starts one exact registered worker executable.</summary>
public interface IWorkerProcessStarter
{
    /// <summary>Starts the worker without accepting caller-supplied arguments.</summary>
    IWorkerProcess Start(InstalledWorker worker);
}

/// <summary>Reports enough process state for bounded launcher startup polling.</summary>
public interface IWorkerProcess
{
    /// <summary>Whether the process has already exited.</summary>
    bool HasExited { get; }

    /// <summary>The exit code when the process has exited.</summary>
    int ExitCode { get; }

    /// <summary>The exact shell-free startup configuration used by the process seam.</summary>
    ProcessStartInfo StartInfo { get; }
}

/// <summary>Connects to an existing worker or starts one registered compatible installation.</summary>
public sealed class WorkerLauncher
{
    private readonly InstalledWorkerCatalog _catalog;
    private readonly IWorkerConnector _connector;
    private readonly IWorkerProcessStarter _processStarter;
    private readonly string _endpointName;
    private readonly TimeSpan _startupTimeout;
    private readonly ILauncherClock _clock;
    private readonly ILauncherDelay _delay;

    /// <summary>Creates a launcher with injectable catalog, transport, process, and endpoint seams.</summary>
    public WorkerLauncher(InstalledWorkerCatalog catalog, IWorkerConnector connector,
        IWorkerProcessStarter processStarter, string endpointName, TimeSpan startupTimeout,
        ILauncherClock? clock = null, ILauncherDelay? delay = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        if (string.IsNullOrWhiteSpace(endpointName))
            throw new ArgumentException("A worker endpoint is required.", nameof(endpointName));
        if (startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        _endpointName = endpointName;
        _startupTimeout = startupTimeout;
        _clock = clock ?? SystemLauncherClock.Instance;
        _delay = delay ?? SystemLauncherDelay.Instance;
    }

    /// <summary>Creates a launcher using the stable user catalog and worker endpoint.</summary>
    public WorkerLauncher()
        : this(new InstalledWorkerCatalog(), new WorkerClientConnector(), new WorkerProcessStarter(),
            WorkerEndpointNames.ControlPipe(CurrentSid()), TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>Ensures one compatible endpoint is connected, disposing the confirmation connection.</summary>
    public async Task EnsureConnectedAsync(WorkerHandshakeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        var deadline = Deadline(_clock.Timestamp, _startupTimeout, _clock.Frequency);
        var first = await TryConnectAsync(request, Remaining(deadline), cancellationToken).ConfigureAwait(false);
        if (first is not null)
        {
            first.Dispose();
            return;
        }

        EnsureBeforeDeadline(deadline);
        InstalledWorker candidate;
        try
        {
            candidate = WorkerSelector.SelectNewestCompatible(_catalog.List(), request);
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is IOException ||
            exception is UnauthorizedAccessException)
        {
            throw new WorkerCatalogException(
                "The installed worker catalog is corrupt or unavailable; reinstall or update Motif and try again.",
                exception);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(
            "No installed worker overlaps", StringComparison.Ordinal))
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

        while (_clock.Timestamp <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - _clock.Timestamp;
            if (remaining <= 0)
                break;
            var connectionTimeout = TimeSpan.FromSeconds(remaining / (double)_clock.Frequency);
            var connection = await TryConnectAsync(request, connectionTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (connection is not null)
            {
                connection.Dispose();
                return;
            }
            if (process.HasExited)
                throw new WorkerLaunchException(
                    "Worker startup failed: the installed worker exited before its endpoint became ready; " +
                    "reinstall or update Motif and try again.");
            remaining = deadline - _clock.Timestamp;
            if (remaining <= 0)
                break;
            var delay = TimeSpan.FromMilliseconds(Math.Min(50,
                remaining * 1000.0 / _clock.Frequency));
            await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new WorkerLaunchException(
            "Worker startup failed: the endpoint did not become ready before startup timed out; " +
            "reinstall or update Motif and try again.");
    }

    private async Task<IWorkerConnection?> TryConnectAsync(WorkerHandshakeRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _connector.ConnectAsync(_endpointName, request, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkerEndpointUnavailableException)
        {
            return null;
        }
        catch (WorkerEndpointIncompatibleException)
        {
            throw;
        }
        catch (WorkerConnectionFailureException exception) when (
            exception.Stage == WorkerConnectionFailureStage.BeforePeerConnection)
        {
            return null;
        }
        catch (WorkerConnectionFailureException exception)
        {
            throw new WorkerEndpointIncompatibleException(
                "The existing worker endpoint returned an invalid or incompatible response.", exception);
        }
    }

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

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

/// <summary>Adapts the cross-runtime worker client to launcher connection semantics.</summary>
public sealed class WorkerClientConnector : IWorkerConnector
{
    /// <inheritdoc />
    public async Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await new WorkerClient().ConnectAsync(endpointName, request, timeout,
                cancellationToken).ConfigureAwait(false);
            return new WorkerConnectionAdapter(connection);
        }
        catch (WorkerConnectionFailureException exception) when (
            exception.Stage == WorkerConnectionFailureStage.BeforePeerConnection)
        {
            throw new WorkerEndpointUnavailableException("The worker endpoint did not respond.", exception);
        }
        catch (WorkerConnectionFailureException exception)
        {
            throw new WorkerEndpointIncompatibleException(
                "The existing worker endpoint returned an invalid or incompatible response.", exception);
        }
    }

    private sealed class WorkerConnectionAdapter : IWorkerConnection
    {
        private readonly WorkerConnection _connection;

        public WorkerConnectionAdapter(WorkerConnection connection)
        {
            _connection = connection;
        }

        public WorkerHandshakeResult Negotiated => _connection.Negotiated;

        public void Dispose() => _connection.Dispose();
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
    }
}

public static class Program
{
    private const int Success = 0;
    private const int NoCompatibleInstall = 2;
    private const int ExistingWorkerIncompatible = 3;
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
            var request = Parse(args);
            if (launcher is null)
                throw new ArgumentNullException(nameof(launcher));
            await launcher.EnsureConnectedAsync(request).ConfigureAwait(false);
            await output.WriteLineAsync("Connected to the Motif worker.").ConfigureAwait(false);
            return Success;
        }
        catch (WorkerEndpointIncompatibleException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExistingWorkerIncompatible;
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
            await error.WriteLineAsync("The client compatibility request is invalid.").ConfigureAwait(false);
            return StartupFailure;
        }
    }

    private static WorkerHandshakeRequest Parse(string[] args)
    {
        if (args is null || args.Length > 20)
            throw new ArgumentException("Too many launcher arguments.", nameof(args));
        string clientId = "motif-launcher";
        string productVersion = "0.0.0";
        var minimum = 1;
        var maximum = 1;
        var capabilities = new System.Collections.Generic.List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "--client-id" or "--product-version" or "--protocol-min" or "--protocol-max")
            {
                if (++index >= args.Length)
                    throw new ArgumentException("A launcher option is missing its value.");
                var value = args[index];
                if (option == "--client-id") clientId = value;
                else if (option == "--product-version") productVersion = value;
                else if (option == "--protocol-min" && !int.TryParse(value, out minimum))
                    throw new FormatException("The protocol minimum is invalid.");
                else if (option == "--protocol-max" && !int.TryParse(value, out maximum))
                    throw new FormatException("The protocol maximum is invalid.");
            }
            else if (option == "--required-capability")
            {
                if (++index >= args.Length || capabilities.Count >= 16)
                    throw new ArgumentException("A required capability is missing or exceeds the bound.");
                capabilities.Add(args[index]);
            }
            else
            {
                throw new ArgumentException("Unknown launcher option.");
            }
        }
        return new WorkerHandshakeRequest(clientId, productVersion,
            new ProtocolRange(minimum, maximum), capabilities);
    }
}
