using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Contract.Baselines;

/// <summary>Requests a bounded upload offer for a Baseline bundle.</summary>
public sealed record BaselineOfferRequest
{
    [JsonConstructor]
    public BaselineOfferRequest(ProjectLocator project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    [JsonPropertyOrder(0)] public ProjectLocator Project { get; }
}

/// <summary>Returns either an upload offer or one closed Baseline command failure.</summary>
public sealed record BaselineOfferResponse
{
    [JsonConstructor]
    public BaselineOfferResponse(BinaryTransferOffer? offer, BaselineCommandFailure? failure)
    {
        RequireExclusive(offer, failure);
        Offer = offer;
        Failure = failure;
    }

    [JsonPropertyOrder(0)] public BinaryTransferOffer? Offer { get; }
    [JsonPropertyOrder(1)] public BaselineCommandFailure? Failure { get; }

    private static void RequireExclusive(BinaryTransferOffer? offer, BaselineCommandFailure? failure)
    {
        if ((offer is null) == (failure is null))
            throw new ArgumentException("A Baseline offer response requires exactly one result or failure.");
    }
}

/// <summary>Requests publication of one transferred Baseline bundle and its semantic token.</summary>
public sealed record BaselinePublishRequest
{
    [JsonConstructor]
    public BaselinePublishRequest(ProjectLocator project, string transferId, BaselineToken token)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        TransferId = WorkerProtocolValidation.Identifier(transferId, nameof(transferId));
        Token = token ?? throw new ArgumentNullException(nameof(token));
    }

    [JsonPropertyOrder(0)] public ProjectLocator Project { get; }
    [JsonPropertyOrder(1)] public string TransferId { get; }
    [JsonPropertyOrder(2)] public BaselineToken Token { get; }
}

/// <summary>Returns either a published Baseline identity or one closed command failure.</summary>
public sealed record BaselinePublishResponse
{
    [JsonConstructor]
    public BaselinePublishResponse(BaselinePublicationResult? publication, BaselineCommandFailure? failure)
    {
        RequireExclusive(publication, failure);
        Publication = publication;
        Failure = failure;
    }

    [JsonPropertyOrder(0)] public BaselinePublicationResult? Publication { get; }
    [JsonPropertyOrder(1)] public BaselineCommandFailure? Failure { get; }

    private static void RequireExclusive(BaselinePublicationResult? publication, BaselineCommandFailure? failure)
    {
        if ((publication is null) == (failure is null))
            throw new ArgumentException("A Baseline publish response requires exactly one result or failure.");
    }
}

/// <summary>Identifies the project workspace and semantic token accepted for publication.</summary>
public sealed record BaselinePublicationResult
{
    [JsonConstructor]
    public BaselinePublicationResult(string projectKey, BaselineToken token)
    {
        ProjectKey = WorkerProtocolValidation.Identifier(projectKey, nameof(projectKey));
        Token = token ?? throw new ArgumentNullException(nameof(token));
    }

    [JsonPropertyOrder(0)] public string ProjectKey { get; }
    [JsonPropertyOrder(1)] public BaselineToken Token { get; }
}

/// <summary>A bounded, classified refusal returned by a Baseline control command.</summary>
public sealed record BaselineCommandFailure
{
    public const int MaximumMessageLength = 4096;

    [JsonConstructor]
    public BaselineCommandFailure(BaselineFailureCode code, bool retryable, string message)
    {
        if (!Enum.IsDefined(typeof(BaselineFailureCode), code))
            throw new ArgumentOutOfRangeException(nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A failure message is required.", nameof(message));
        if (message.Length > MaximumMessageLength)
            throw new ArgumentException("The failure message exceeds its protocol bound.", nameof(message));
        Code = code;
        Retryable = retryable;
        Message = message;
    }

    [JsonPropertyOrder(0)] public BaselineFailureCode Code { get; }
    [JsonPropertyOrder(1)] public bool Retryable { get; }
    [JsonPropertyOrder(2)] public string Message { get; }
}

/// <summary>Closed failure meanings for Baseline offer and publication commands.</summary>
[JsonConverter(typeof(BaselineFailureCodeJsonConverter))]
public enum BaselineFailureCode
{
    CapacityUnavailable,
    TransferUnknown,
    TransferInvalid,
    ProjectRuntimeUnavailable,
    BundleInvalid,
    PublicationFailed
}

/// <summary>Serializes Baseline failure meanings as closed canonical JSON strings.</summary>
public sealed class BaselineFailureCodeJsonConverter : JsonConverter<BaselineFailureCode>
{
    public override BaselineFailureCode Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Baseline failure code must be a JSON string.");
        return Parse(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, BaselineFailureCode value,
        JsonSerializerOptions options) => writer.WriteStringValue(ToWire(value));

    private static BaselineFailureCode Parse(string value) => value switch
    {
        "capacity-unavailable" => BaselineFailureCode.CapacityUnavailable,
        "transfer-unknown" => BaselineFailureCode.TransferUnknown,
        "transfer-invalid" => BaselineFailureCode.TransferInvalid,
        "project-runtime-unavailable" => BaselineFailureCode.ProjectRuntimeUnavailable,
        "bundle-invalid" => BaselineFailureCode.BundleInvalid,
        "publication-failed" => BaselineFailureCode.PublicationFailed,
        _ => throw new JsonException("Unknown Baseline failure code.")
    };

    private static string ToWire(BaselineFailureCode value) => value switch
    {
        BaselineFailureCode.CapacityUnavailable => "capacity-unavailable",
        BaselineFailureCode.TransferUnknown => "transfer-unknown",
        BaselineFailureCode.TransferInvalid => "transfer-invalid",
        BaselineFailureCode.ProjectRuntimeUnavailable => "project-runtime-unavailable",
        BaselineFailureCode.BundleInvalid => "bundle-invalid",
        BaselineFailureCode.PublicationFailed => "publication-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
