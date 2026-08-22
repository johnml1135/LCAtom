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
public sealed class WorkerEndpointUnavailableException : IOException
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
public sealed class WorkerLaunchException : InvalidOperationException
{
    /// <summary>Creates a launcher diagnostic with optional local failure details.</summary>
    public WorkerLaunchException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
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
}

/// <summary>Connects to an existing worker or starts one registered compatible installation.</summary>
public sealed class WorkerLauncher
{
    private readonly InstalledWorkerCatalog _catalog;
    private readonly IWorkerConnector _connector;
    private readonly IWorkerProcessStarter _processStarter;
    private readonly string _endpointName;
    private readonly TimeSpan _startupTimeout;

    /// <summary>Creates a launcher with injectable catalog, transport, process, and endpoint seams.</summary>
    public WorkerLauncher(InstalledWorkerCatalog catalog, IWorkerConnector connector,
        IWorkerProcessStarter processStarter, string endpointName, TimeSpan startupTimeout)
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
        var first = await TryConnectAsync(request, _startupTimeout, cancellationToken).ConfigureAwait(false);
        if (first is not null)
        {
            first.Dispose();
            return;
        }

        InstalledWorker candidate;
        try
        {
            candidate = WorkerSelector.SelectNewestCompatible(_catalog.List(), request);
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
        {
            throw new WorkerLaunchException(
                "No compatible worker is installed; install or update Motif and try again.", exception);
        }

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

        var deadline = Stopwatch.GetTimestamp() + (long)(_startupTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                break;
            var connectionTimeout = TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency);
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
            remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                break;
            var delay = TimeSpan.FromMilliseconds(Math.Min(50,
                remaining * 1000.0 / Stopwatch.Frequency));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
        catch (IOException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (InvalidOperationException exception)
        {
            throw new WorkerEndpointIncompatibleException(
                "The existing worker endpoint is incompatible with this client.", exception);
        }
    }

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";
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
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw new WorkerEndpointUnavailableException("The worker endpoint did not respond.", exception);
        }
        catch (Exception exception) when (IsIncompatible(exception))
        {
            throw new WorkerEndpointIncompatibleException(
                "The existing worker endpoint is incompatible with this client.", exception);
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is TimeoutException || exception is EndOfStreamException ||
        exception is UnauthorizedAccessException || exception is IOException;

    private static bool IsIncompatible(Exception exception) =>
        exception is InvalidOperationException || exception is ArgumentException ||
        exception is InvalidDataException || exception is System.Text.Json.JsonException;

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
        var process = Process.Start(info) ?? throw new InvalidOperationException("The worker process did not start.");
        return new StartedWorkerProcess(process);
    }

    private sealed class StartedWorkerProcess : IWorkerProcess
    {
        private readonly Process _process;

        public StartedWorkerProcess(Process process) { _process = process; }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;
    }
}

internal static class Program
{
    private const int Success = 0;
    private const int NoCompatibleInstall = 2;
    private const int ExistingWorkerIncompatible = 3;
    private const int StartupFailure = 4;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var request = Parse(args);
            await new WorkerLauncher().EnsureConnectedAsync(request).ConfigureAwait(false);
            Console.WriteLine("Connected to the Motif worker.");
            return Success;
        }
        catch (WorkerEndpointIncompatibleException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return ExistingWorkerIncompatible;
        }
        catch (WorkerLaunchException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.Message.IndexOf("No compatible", StringComparison.OrdinalIgnoreCase) >= 0
                ? NoCompatibleInstall
                : StartupFailure;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is FormatException)
        {
            Console.Error.WriteLine("The client compatibility request is invalid.");
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
