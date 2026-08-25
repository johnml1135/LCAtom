using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker;

/// <summary>Handles one registered worker command payload.</summary>
public interface IWorkerCommandHandler
{
    /// <summary>Gets the closed command discriminator handled by this instance.</summary>
    string Command { get; }

    /// <summary>Validates and handles one command payload.</summary>
    Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>Dispatches only command handlers explicitly registered by the worker composition root.</summary>
public sealed class WorkerCommandDispatcher
{
    private readonly IReadOnlyDictionary<string, IWorkerCommandHandler> _handlers;

    /// <summary>Creates a dispatcher with a closed set of typed handlers.</summary>
    public WorkerCommandDispatcher(IEnumerable<IWorkerCommandHandler> handlers)
    {
        if (handlers is null) throw new ArgumentNullException(nameof(handlers));
        var map = new Dictionary<string, IWorkerCommandHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (handler is null) throw new ArgumentException("A command handler is required.", nameof(handlers));
            if (!WorkerCommands.IsKnown(handler.Command))
                throw new ArgumentException("Unknown worker command discriminator.", nameof(handlers));
            var command = handler.Command;
            if (command == WorkerCommands.Handshake || !map.TryAdd(command, handler))
                throw new ArgumentException("The command handler registry contains a duplicate or reserved command.",
                    nameof(handlers));
        }
        _handlers = map;
    }

    /// <summary>Returns whether this worker composed a handler for a command.</summary>
    public bool Handles(string command) => command is not null && _handlers.ContainsKey(command);

    /// <summary>Dispatches a registered command.</summary>
    public Task<JsonElement> DispatchAsync(string command, JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!WorkerCommands.IsKnown(command))
            throw new ArgumentException("Unknown worker command discriminator.", nameof(command));
        if (!_handlers.TryGetValue(command, out var handler))
            throw new InvalidDataException("The worker command is not registered.");
        return handler.HandleAsync(payload, cancellationToken);
    }
}
