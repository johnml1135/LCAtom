using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>A correlated request or response on the worker control channel.</summary>
public sealed record WorkerEnvelope
{
    [JsonConstructor]
    public WorkerEnvelope(string requestId, string command, JsonElement payload, int protocolVersion)
    {
        RequestId = WorkerProtocolValidation.Identifier(requestId, nameof(requestId));
        Command = WorkerCommands.RequireKnown(command);
        Payload = payload.Clone();
        ProtocolVersion = ValidateProtocolVersion(protocolVersion);
    }

    /// <summary>The identifier correlating this envelope with its caller.</summary>
    public string RequestId { get; }

    /// <summary>The closed command discriminator.</summary>
    public string Command { get; }

    /// <summary>The command-specific JSON payload.</summary>
    public JsonElement Payload { get; }

    /// <summary>The negotiated protocol generation used by this envelope.</summary>
    public int ProtocolVersion { get; }

    internal static int ValidateProtocolVersion(int version)
    {
        if (version < 1 || version > 10000)
            throw new ArgumentOutOfRangeException(nameof(version), "Protocol version is outside the supported bound.");
        return version;
    }
}
