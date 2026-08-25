using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Cli.Worker;

/// <summary>
/// Signals that a command cannot be sent on this connection, and why. The CLI turns this into the message a
/// person reads, so it names the command and the capability rather than only the failure.
/// </summary>
public sealed class WorkerCommandUnavailableException : InvalidOperationException
{
    /// <summary>Creates an exception naming the command and the capability it needed.</summary>
    public WorkerCommandUnavailableException(string command, string? capability, string message)
        : base(message)
    {
        Command = command;
        Capability = capability;
    }

    /// <summary>The command discriminator that could not be sent.</summary>
    public string Command { get; }

    /// <summary>The capability the command required, or null when the command itself is unknown.</summary>
    public string? Capability { get; }
}

/// <summary>
/// Sends typed worker commands and returns typed responses, refusing locally what this connection could
/// never carry.
/// </summary>
/// <remarks>
/// <para>
/// The local refusal exists because the two failures read very differently to a person. A worker that is
/// older than this CLI has genuinely not got the command, and saying so before sending turns a remote
/// refusal into an immediate, specific message that names the missing capability. Sending anyway would
/// produce the same outcome one round trip later, with less to say about it.
/// </para>
/// <para>
/// It is not a substitute for the worker's own refusal. This client checks what it can see -- the closed
/// command registry and the capabilities negotiated for this connection -- while the worker refuses what
/// only it can judge, such as a malformed payload, pinned by
/// `AWorkerRefusalSurfacesTypedAndLeavesTheConnectionUsable`.
/// </para>
/// </remarks>
public sealed class WorkerCommandClient
{
    private readonly WorkerConnection _connection;
    private readonly HashSet<string> _capabilities;

    /// <summary>Creates a command client over one negotiated connection.</summary>
    public WorkerCommandClient(WorkerConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _capabilities = new HashSet<string>(connection.Negotiated.Capabilities, StringComparer.Ordinal);
    }

    /// <summary>Whether this connection could carry a command, without sending it.</summary>
    public bool CanExecute(string command) =>
        WorkerCommands.IsKnown(command) && HasCapabilityFor(command);

    /// <summary>Sends one command and returns its typed response.</summary>
    /// <exception cref="WorkerCommandUnavailableException">This connection cannot carry the command.</exception>
    /// <exception cref="WorkerRequestRefusedException">The worker refused the request.</exception>
    public async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string command,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        RequireAvailable(command);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(request, WorkerJson.CreateOptions()));
        var response = await _connection.SendAsync(new WorkerEnvelope(
            Guid.NewGuid().ToString("N"), command, payload.RootElement.Clone(),
            _connection.Negotiated.ProtocolVersion), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(response.Payload.GetRawText(), WorkerJson.CreateOptions())
            ?? throw new InvalidOperationException("The worker returned an empty " + command + " response.");
    }

    private void RequireAvailable(string command)
    {
        if (!WorkerCommands.IsKnown(command))
        {
            throw new WorkerCommandUnavailableException(command, null,
                "This build of Motif does not have a '" + command + "' command.");
        }
        if (HasCapabilityFor(command)) return;
        var capability = WorkerCommands.RequiredCapability(command);
        throw new WorkerCommandUnavailableException(command, capability,
            "The connected Motif worker does not offer '" + capability + "', which '" + command +
            "' requires. Its version is " + _connection.Offer.ProductVersion + ".");
    }

    private bool HasCapabilityFor(string command)
    {
        var capability = WorkerCommands.RequiredCapability(command);
        return capability is null || _capabilities.Contains(capability);
    }
}
