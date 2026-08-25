using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Store;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Launcher;

namespace SIL.Motif.Cli.Worker;

/// <summary>
/// The CLI verbs that move this machine's store into the worker. Parsing and rendering stay here; the
/// state change happens only inside the worker.
/// </summary>
public static class StoreCommands
{
    /// <summary>The handshake this CLI offers when it needs store commands.</summary>
    public static WorkerHandshakeRequest Handshake(string productVersion) =>
        new WorkerHandshakeRequest("motif-cli", productVersion, new ProtocolRange(1, 1), new[] { "store.v1" });

    /// <summary>Cuts one store location over to the worker and renders what moved.</summary>
    public static async Task<CommandResult> CutoverAsync(
        IWorkerCommandSession session, string storeDirectory, string fwDataPath, string productVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ProjectLocator project;
        try
        {
            project = Locate(fwDataPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }

        try
        {
            using var client = await session.ConnectAsync(Handshake(productVersion), cancellationToken)
                .ConfigureAwait(false);
            var response = await client.ExecuteAsync<StoreCutoverRequest, StoreCutoverResponse>(
                WorkerCommands.StoreCutover, new StoreCutoverRequest(project, storeDirectory), cancellationToken)
                .ConfigureAwait(false);
            return new CommandResult(0, Render(storeDirectory, response));
        }
        catch (WorkerCommandUnavailableException exception)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
        catch (WorkerRequestRefusedException exception)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
        catch (WorkerLaunchException exception)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
        catch (WorkerEndpointUnavailableException exception)
        {
            return new CommandResult(1, "error: " + exception.Message + Environment.NewLine);
        }
    }

    /// The file must exist: an unresolvable path would key a second, empty workspace instead of the real one.
    private static ProjectLocator Locate(string fwDataPath)
    {
        var full = System.IO.Path.GetFullPath(fwDataPath);
        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException("Project file not found: '" + full + "'.", full);
        return new ProjectLocator(full, System.IO.Path.GetFileNameWithoutExtension(full));
    }

    private static string Render(string storeDirectory, StoreCutoverResponse response)
    {
        var text = new StringBuilder();
        if (response.AlreadyCutOver)
        {
            text.AppendLine("Store '" + storeDirectory + "' was already taken by the Motif worker.");
            return text.ToString();
        }
        text.AppendLine("Store '" + storeDirectory + "' is now held by the Motif worker.");
        text.AppendLine("  Proposals imported: " + response.ImportedProposals);
        text.AppendLine("  Legacy rows imported: " + response.ImportedLegacyRows);
        foreach (var path in response.ArchivedPaths)
            text.AppendLine("  Archived: " + path);
        if (response.UnarchivedPaths.Count == 0)
            return text.ToString();
        // The cutover succeeded; naming what is left says why the old files are still on disk.
        text.AppendLine("  The database is authoritative, but these sources could not be moved aside:");
        foreach (var path in response.UnarchivedPaths)
            text.AppendLine("    " + path);
        text.AppendLine("  Run this command again to retry moving them; nothing is imported twice.");
        return text.ToString();
    }
}

/// <summary>Obtains a command client, launching a worker if none is running.</summary>
/// <remarks>
/// This is a seam so a test can drive the real CLI against a worker it controls. Without it, exercising the
/// CLI end to end would mean installing a worker into the machine-wide catalog the launcher reads.
/// </remarks>
public interface IWorkerCommandSession
{
    /// <summary>Connects, negotiating the given handshake.</summary>
    Task<IWorkerCommandClient> ConnectAsync(WorkerHandshakeRequest request, CancellationToken cancellationToken);
}

/// <summary>One connected command client, owned by its caller.</summary>
public interface IWorkerCommandClient : IDisposable
{
    /// <summary>Sends one command and returns its typed response.</summary>
    Task<TResponse> ExecuteAsync<TRequest, TResponse>(string command, TRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Launches or joins the user's worker, then connects to it.</summary>
public sealed class LaunchedWorkerCommandSession : IWorkerCommandSession
{
    private readonly WorkerLauncher _launcher;
    private readonly string _endpointName;
    private readonly TimeSpan _connectTimeout;

    /// <summary>Creates a session over the user's own worker endpoint.</summary>
    public LaunchedWorkerCommandSession()
        : this(new WorkerLauncher(), WorkerEndpointNames.ControlPipe(CurrentSid()), TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>Creates a session with explicit launcher and endpoint seams.</summary>
    public LaunchedWorkerCommandSession(WorkerLauncher launcher, string endpointName, TimeSpan connectTimeout)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _endpointName = endpointName;
        _connectTimeout = connectTimeout;
    }

    /// <inheritdoc />
    public async Task<IWorkerCommandClient> ConnectAsync(WorkerHandshakeRequest request,
        CancellationToken cancellationToken)
    {
        await _launcher.EnsureConnectedAsync(request, cancellationToken).ConfigureAwait(false);
        var connection = await new WorkerClient().ConnectAsync(_endpointName, request, _connectTimeout,
            cancellationToken).ConfigureAwait(false);
        return new ConnectedCommandClient(connection);
    }

    private static string CurrentSid() =>
        System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "unknown-user";

    private sealed class ConnectedCommandClient : IWorkerCommandClient
    {
        private readonly WorkerConnection _connection;
        private readonly WorkerCommandClient _client;

        public ConnectedCommandClient(WorkerConnection connection)
        {
            _connection = connection;
            _client = new WorkerCommandClient(connection);
        }

        public Task<TResponse> ExecuteAsync<TRequest, TResponse>(string command, TRequest request,
            CancellationToken cancellationToken) =>
            _client.ExecuteAsync<TRequest, TResponse>(command, request, cancellationToken);

        public void Dispose() => _connection.Dispose();
    }
}
