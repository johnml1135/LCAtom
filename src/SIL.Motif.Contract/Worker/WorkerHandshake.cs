using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Worker;

/// <summary>Client identity and compatibility interval presented during connection setup.</summary>
public sealed record WorkerHandshakeRequest
{
    public WorkerHandshakeRequest(
        string clientId, string productVersion, ProtocolRange protocols, IEnumerable<string> capabilities)
    {
        ClientId = WorkerProtocolValidation.Identifier(clientId, nameof(clientId));
        ProductVersion = WorkerProtocolValidation.Identifier(productVersion, nameof(productVersion));
        Protocols = protocols ?? throw new ArgumentNullException(nameof(protocols));
        Capabilities = WorkerProtocolValidation.CopyCapabilities(capabilities, nameof(capabilities));
    }

    [JsonConstructor]
    public WorkerHandshakeRequest(
        string clientId, string productVersion, ProtocolRange protocols, IReadOnlyList<string> capabilities)
        : this(clientId, productVersion, protocols, (IEnumerable<string>)capabilities)
    {
    }

    /// <summary>The client identity used for diagnostics.</summary>
    public string ClientId { get; }

    /// <summary>The informational product version, not a compatibility authority.</summary>
    public string ProductVersion { get; }

    /// <summary>The wire-protocol interval understood by the client.</summary>
    public ProtocolRange Protocols { get; }

    /// <summary>Capabilities required by this client and understood by its caller.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>Worker identity and compatibility interval offered to a connecting client.</summary>
public sealed record WorkerHandshakeOffer
{
    public WorkerHandshakeOffer(
        string productVersion, ProtocolRange protocols, IEnumerable<string> capabilities)
    {
        ProductVersion = WorkerProtocolValidation.Identifier(productVersion, nameof(productVersion));
        Protocols = protocols ?? throw new ArgumentNullException(nameof(protocols));
        Capabilities = WorkerProtocolValidation.CopyCapabilities(capabilities, nameof(capabilities));
    }

    [JsonConstructor]
    public WorkerHandshakeOffer(
        string productVersion, ProtocolRange protocols, IReadOnlyList<string> capabilities)
        : this(productVersion, protocols, (IEnumerable<string>)capabilities)
    {
    }

    /// <summary>The informational worker product version.</summary>
    public string ProductVersion { get; }

    /// <summary>The wire-protocol interval understood by the worker.</summary>
    public ProtocolRange Protocols { get; }

    /// <summary>Capabilities exposed by the worker.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>Negotiated protocol generation and effective capability set.</summary>
public sealed record WorkerHandshakeResult
{
    [JsonConstructor]
    public WorkerHandshakeResult(int protocolVersion, IReadOnlyList<string> capabilities)
    {
        ProtocolVersion = WorkerEnvelope.ValidateProtocolVersion(protocolVersion);
        Capabilities = WorkerProtocolValidation.CopyCapabilities(capabilities, nameof(capabilities));
    }

    /// <summary>The single protocol generation shared by both peers.</summary>
    public int ProtocolVersion { get; }

    /// <summary>Capabilities available to the connected client.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnknownProperties { get; set; }
}

/// <summary>Negotiates a safe protocol generation and capability set.</summary>
public static class WorkerHandshake
{
    /// <summary>Chooses the highest shared protocol and verifies every requested capability.</summary>
    public static WorkerHandshakeResult Negotiate(
        WorkerHandshakeRequest client, WorkerHandshakeOffer worker)
    {
        if (client is null)
            throw new ArgumentNullException(nameof(client));
        if (worker is null)
            throw new ArgumentNullException(nameof(worker));

        var protocolVersion = client.Protocols.HighestCommon(worker.Protocols);
        if (protocolVersion == 0)
            throw new InvalidOperationException("The client and worker have no shared protocol version.");

        var workerCapabilities = new HashSet<string>(worker.Capabilities, StringComparer.Ordinal);
        var missing = client.Capabilities.Where(capability => !workerCapabilities.Contains(capability)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                "The worker does not provide required capabilities: " + string.Join(", ", missing),
                nameof(worker));
        }

        return new WorkerHandshakeResult(protocolVersion, client.Capabilities);
    }
}
