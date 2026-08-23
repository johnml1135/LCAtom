using System;
using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Contract;

public sealed class BaselineCommandContractTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void RequestsAndResponses_RoundTripWithStableWireOrder()
    {
        var project = Project();
        var token = Token();
        var offer = new BinaryTransferOffer("transfer-1", "upload", "pipe-1", 4096,
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        AssertJson(new BaselineOfferRequest(project),
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"fieldworks-project\"}}");
        AssertJson(new BaselineOfferResponse(offer, null),
            "{\"Offer\":{\"TransferId\":\"transfer-1\",\"Direction\":\"upload\",\"PipeName\":\"pipe-1\",\"MaximumBytes\":4096,\"ExpiresAt\":\"2030-01-01T00:00:00+00:00\"},\"Failure\":null}");
        AssertJson(new BaselinePublishRequest(project, "transfer-1", token),
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"fieldworks-project\"},\"TransferId\":\"transfer-1\",\"Token\":" + TokenJson() + "}");
        AssertJson(new BaselinePublishResponse(new BaselinePublicationResult("project-key", token), null),
            "{\"Publication\":{\"ProjectKey\":\"project-key\",\"Token\":" + TokenJson() + "},\"Failure\":null}");
        AssertJson(new BaselineCommandFailure(BaselineFailureCode.CapacityUnavailable, true, "Try later."),
            "{\"Code\":\"capacity-unavailable\",\"Retryable\":true,\"Message\":\"Try later.\"}");
    }

    [Fact]
    public void UnknownObjectProperties_AreIgnoredAcrossBaselineCommands()
    {
        var json = JsonSerializer.Serialize(new BaselinePublishRequest(Project(), "transfer-1", Token()),
            WorkerJson.CreateOptions());

        var parsed = JsonSerializer.Deserialize<BaselinePublishRequest>(
            json.Substring(0, json.Length - 1) + ",\"futureField\":true}", WorkerJson.CreateOptions());

        Assert.NotNull(parsed);
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(parsed, WorkerJson.CreateOptions()));
    }

    [Fact]
    public void RequiredNestedValues_RejectNullAndMissingJson()
    {
        Assert.Throws<ArgumentNullException>(() => new BaselineOfferRequest(null!));
        Assert.Throws<ArgumentNullException>(() => new BaselinePublishRequest(Project(), "transfer-1", null!));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<BaselineOfferRequest>(
            "{}", WorkerJson.CreateOptions()));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<BaselinePublishRequest>(
            "{\"Project\":null,\"TransferId\":\"transfer-1\",\"Token\":null}", WorkerJson.CreateOptions()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("transfer\r")]
    [InlineData("transfer\n")]
    [InlineData("transfer\0")]
    public void BoundedIdentifiers_RejectBlankAndControlCharacters(string value)
    {
        Assert.Throws<ArgumentException>(() => new BaselinePublishRequest(Project(), value, Token()));
        Assert.Throws<ArgumentException>(() => new BaselinePublicationResult(value, Token()));
    }

    [Fact]
    public void BoundedIdentifiers_RejectOversizedValues()
    {
        var value = new string('x', 257);

        Assert.Throws<ArgumentException>(() => new BaselinePublishRequest(Project(), value, Token()));
        Assert.Throws<ArgumentException>(() => new BaselinePublicationResult(value, Token()));
    }

    [Fact]
    public void Failure_RejectsUnknownCodeAndInvalidMessages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BaselineCommandFailure((BaselineFailureCode)999, false, "Failure."));
        Assert.Throws<ArgumentException>(() =>
            new BaselineCommandFailure(BaselineFailureCode.BundleInvalid, false, " "));
        Assert.Throws<ArgumentException>(() =>
            new BaselineCommandFailure(BaselineFailureCode.BundleInvalid, false, new string('x', 4097)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BaselineCommandFailure>(
            "{\"Code\":\"FutureFailure\",\"Retryable\":false,\"Message\":\"Failure.\"}",
            WorkerJson.CreateOptions()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BaselineCommandFailure>(
            "{\"Code\":999,\"Retryable\":false,\"Message\":\"Failure.\"}",
            WorkerJson.CreateOptions()));
    }

    [Fact]
    public void Responses_RequireExactlyOneSuccessOrFailureValue()
    {
        var offer = new BinaryTransferOffer("transfer-1", "upload", "pipe-1", 4096,
            DateTimeOffset.UtcNow.AddMinutes(1));
        var publication = new BaselinePublicationResult("project-key", Token());
        var failure = new BaselineCommandFailure(BaselineFailureCode.PublicationFailed, true, "Try later.");

        Assert.Throws<ArgumentException>(() => new BaselineOfferResponse(null, null));
        Assert.Throws<ArgumentException>(() => new BaselineOfferResponse(offer, failure));
        Assert.Throws<ArgumentException>(() => new BaselinePublishResponse(null, null));
        Assert.Throws<ArgumentException>(() => new BaselinePublishResponse(publication, failure));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<BaselineOfferResponse>(
            "{\"Offer\":null,\"Failure\":null}", WorkerJson.CreateOptions()));
    }

    private static void AssertJson<T>(T value, string expected)
    {
        var json = JsonSerializer.Serialize(value, WorkerJson.CreateOptions());
        Assert.Equal(expected, json);
        Assert.Equal(value, JsonSerializer.Deserialize<T>(json, WorkerJson.CreateOptions()));
    }

    private static ProjectLocator Project() =>
        new("C:\\workspace\\demo.fwdata", "fieldworks-project");

    private static BaselineToken Token() =>
        new("fieldworks-project", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest);

    private static string TokenJson() =>
        "{\"ProjectIdentity\":\"fieldworks-project\",\"SemanticSnapshotDigest\":\"" + Digest +
        "\",\"ProjectionVersion\":\"1\",\"CapturedUtc\":\"2026-08-23T12:34:56Z\",\"BundleDigest\":\"" +
        OtherDigest + "\",\"CapturedHostSessionId\":null,\"CapturedEditGeneration\":null}";
}
