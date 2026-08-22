using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>An unsolicited worker event sent to a connected client.</summary>
public sealed record WorkerEventEnvelope
{
    [JsonConstructor]
    public WorkerEventEnvelope(string eventId, string @event, JsonElement payload, int protocolVersion)
    {
        EventId = WorkerProtocolValidation.Identifier(eventId, nameof(eventId));
        Event = WorkerCommands.RequireKnownEvent(@event);
        Payload = payload.Clone();
        ProtocolVersion = WorkerEnvelope.ValidateProtocolVersion(protocolVersion);
    }

    /// <summary>The identifier used to correlate the event result.</summary>
    public string EventId { get; }

    /// <summary>The closed event discriminator.</summary>
    public string Event { get; }

    /// <summary>The event-specific JSON payload.</summary>
    public JsonElement Payload { get; }

    /// <summary>The negotiated protocol generation used by this event.</summary>
    public int ProtocolVersion { get; }

}
