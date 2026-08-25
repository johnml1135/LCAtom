using System;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>
/// A typed refusal of one correlated request. The worker answers with this instead of closing the control
/// connection when a frame it can address is one it will not act on, so the caller learns why and keeps its
/// other outstanding requests. Framing violations and requests carrying no usable identifier remain fatal to
/// the connection, because a refusal no caller can correlate is indistinguishable from silence.
/// </summary>
public sealed record WorkerRefusalEnvelope
{
    [JsonConstructor]
    public WorkerRefusalEnvelope(string requestId, WorkerRefusalReason refusal, int protocolVersion)
    {
        RequestId = WorkerProtocolValidation.Identifier(requestId, nameof(requestId));
        if (!Enum.IsDefined(typeof(WorkerRefusalReason), refusal))
            throw new ArgumentOutOfRangeException(nameof(refusal), "Unknown worker refusal reason.");
        Refusal = refusal;
        ProtocolVersion = WorkerEnvelope.ValidateProtocolVersion(protocolVersion);
    }

    /// <summary>The identifier of the request being refused.</summary>
    public string RequestId { get; }

    /// <summary>The closed reason discriminator, and the property that distinguishes a refusal frame.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkerRefusalReason Refusal { get; }

    /// <summary>The negotiated protocol generation used by this refusal.</summary>
    public int ProtocolVersion { get; }
}

/// <summary>
/// Why a worker refused one request. The set is closed and carries no caller-supplied text, so a refusal can
/// never reflect an unbounded payload or a filesystem path back onto the wire.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerRefusalReason
{
    /// <summary>The command discriminator is not one this worker offers.</summary>
    UnknownCommand,

    /// <summary>The command is known but its capability was not negotiated for this connection.</summary>
    CapabilityNotNegotiated,

    /// <summary>The request declared a protocol generation other than the negotiated one.</summary>
    ProtocolMismatch,

    /// <summary>The payload could not be read as the command's request contract.</summary>
    MalformedPayload,
}
