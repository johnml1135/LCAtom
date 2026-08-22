using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>The terminal response to one unsolicited worker event.</summary>
public sealed record WorkerEventResultEnvelope
{
    [JsonConstructor]
    public WorkerEventResultEnvelope(
        string eventId, WorkerEventOutcome outcome, JsonElement payload, int protocolVersion)
    {
        EventId = WorkerProtocolValidation.Identifier(eventId, nameof(eventId));
        if (!Enum.IsDefined(typeof(WorkerEventOutcome), outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Unknown worker event outcome.");
        Outcome = outcome;
        Payload = payload.Clone();
        ProtocolVersion = WorkerEnvelope.ValidateProtocolVersion(protocolVersion);
    }

    /// <summary>The event identifier being completed.</summary>
    public string EventId { get; }

    /// <summary>The host's disposition of the event.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkerEventOutcome Outcome { get; }

    /// <summary>The outcome-specific JSON payload.</summary>
    public JsonElement Payload { get; }

    /// <summary>The negotiated protocol generation used by this result.</summary>
    public int ProtocolVersion { get; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>Dispositions a live host may return for a worker event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkerEventOutcome
{
    Accepted,
    Deferred,
    Declined,
    Completed,
    Refused,
    NeedsReconciliation,
    Cancelled,
    Failed,
}
