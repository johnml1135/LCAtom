using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>One-use bounded binary-pipe offer accompanying a control message.</summary>
public sealed record BinaryTransferOffer
{
    [JsonConstructor]
    public BinaryTransferOffer(
        string transferId, string direction, string pipeName, long maximumBytes, DateTimeOffset expiresAt)
    {
        TransferId = WorkerProtocolValidation.Identifier(transferId, nameof(transferId));
        Direction = WorkerProtocolValidation.Identifier(direction, nameof(direction));
        PipeName = WorkerProtocolValidation.Identifier(pipeName, nameof(pipeName));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Maximum transfer size must be positive.");
        MaximumBytes = maximumBytes;
        if (expiresAt == default)
            throw new ArgumentException("Transfer expiry must be specified.", nameof(expiresAt));
        ExpiresAt = expiresAt;
    }

    /// <summary>The identifier correlated with the completion message.</summary>
    public string TransferId { get; }

    /// <summary>The transfer direction, such as <c>upload</c> or <c>download</c>.</summary>
    public string Direction { get; }

    /// <summary>The unpredictable named-pipe endpoint.</summary>
    public string PipeName { get; }

    /// <summary>The maximum number of bytes the endpoint may accept.</summary>
    public long MaximumBytes { get; }

    /// <summary>The point after which the offer is unusable.</summary>
    public DateTimeOffset ExpiresAt { get; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }
}
