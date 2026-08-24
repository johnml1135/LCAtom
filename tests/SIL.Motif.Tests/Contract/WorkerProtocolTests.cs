using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Contract;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Negotiate_SelectsHighestIntersectionAndRequiredCapabilities()
    {
        var request = new WorkerHandshakeRequest(
            "motif-cli", "3.4.2", new ProtocolRange(1, 2), new[] { "jobs.v1" });
        var worker = new WorkerHandshakeOffer(
            "3.5.0", new ProtocolRange(2, 3), new[] { "baseline.v1", "jobs.v1" });

        var result = WorkerHandshake.Negotiate(request, worker);

        Assert.Equal(2, result.ProtocolVersion);
        Assert.Equal(new[] { "jobs.v1" }, result.Capabilities);
    }

    [Fact]
    public void Negotiate_RejectsMissingRequiredCapability()
    {
        var request = new WorkerHandshakeRequest(
            "motif-cli", "3.4.2", new ProtocolRange(1, 2), new[] { "jobs.v1" });
        var worker = new WorkerHandshakeOffer(
            "3.5.0", new ProtocolRange(1, 2), new[] { "baseline.v1" });

        Assert.Throws<ArgumentException>(() => WorkerHandshake.Negotiate(request, worker));
    }

    [Fact]
    public void Negotiate_RejectsNoProtocolOverlap()
    {
        var request = new WorkerHandshakeRequest(
            "motif-cli", "3.4.2", new ProtocolRange(1, 1), Array.Empty<string>());
        var worker = new WorkerHandshakeOffer(
            "3.5.0", new ProtocolRange(2, 3), Array.Empty<string>());

        Assert.Throws<InvalidOperationException>(() => WorkerHandshake.Negotiate(request, worker));
    }

    [Fact]
    public void Handshake_RejectsDuplicateAndUnboundedCapabilities()
    {
        Assert.Throws<ArgumentException>(() =>
            new WorkerHandshakeRequest("client", "1.0.0", new ProtocolRange(1, 1), new[] { "jobs.v1", "jobs.v1" }));
        Assert.Throws<ArgumentException>(() =>
            new WorkerHandshakeRequest("client", "1.0.0", new ProtocolRange(1, 1), new[] { new string('x', 257) }));
    }

    [Fact]
    public void Handshake_RejectsExcessCapabilitiesWithoutEnumeratingBeyondBound()
    {
        var moveNextCount = 0;

        IEnumerable<string> ExcessCapabilities()
        {
            for (var index = 0; index < 130; index++)
            {
                moveNextCount++;
                yield return "cap." + index;
            }

            throw new InvalidOperationException("The constructor enumerated beyond its bound.");
        }

        Assert.Throws<ArgumentException>(() => new WorkerHandshakeRequest(
            "client", "1.0.0", new ProtocolRange(1, 1), ExcessCapabilities()));
        Assert.Equal(WorkerProtocolTestLimits.MaximumCapabilities + 1, moveNextCount);
    }

    [Fact]
    public void HandshakeJson_DeserializesAndIgnoresUnknownProperties()
    {
        const string json = "{\"ClientId\":\"motif-cli\",\"ProductVersion\":\"3.4.2\",\"Protocols\":{\"Minimum\":1,\"Maximum\":2},\"Capabilities\":[\"jobs.v1\"],\"futureField\":true}";

        var request = JsonSerializer.Deserialize<WorkerHandshakeRequest>(json);

        Assert.NotNull(request);
        Assert.Equal("motif-cli", request!.ClientId);
        Assert.Equal(1, request.Protocols.Minimum);
        Assert.Equal(new[] { "jobs.v1" }, request.Capabilities);
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(request));
    }

    [Fact]
    public void HandshakeResultJson_RoundTripsAndIgnoresUnknownProperties()
    {
        var result = new WorkerHandshakeResult(2, new[] { "jobs.v1" });
        var json = JsonSerializer.Serialize(result).TrimEnd('}') + ",\"futureField\":true}";

        var parsed = JsonSerializer.Deserialize<WorkerHandshakeResult>(json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.ProtocolVersion);
        Assert.Equal(new[] { "jobs.v1" }, parsed.Capabilities);
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void CapabilityCollections_CannotBeMutatedThroughListCasts()
    {
        var request = new WorkerHandshakeRequest(
            "client", "1.0.0", new ProtocolRange(1, 1), new[] { "jobs.v1" });

        var list = (IList<string>)request.Capabilities;

        Assert.Throws<NotSupportedException>(() => list[0] = "changed");
        Assert.Equal("jobs.v1", request.Capabilities[0]);
    }

    [Fact]
    public void Envelopes_RoundTripAndIgnoreUnknownObjectProperties()
    {
        var payload = JsonDocument.Parse("{\"value\":42}").RootElement.Clone();
        var envelope = new WorkerEnvelope("request-1", WorkerCommands.Handshake, payload, 2);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var json = JsonSerializer.Serialize(envelope, options);
        var parsed = JsonSerializer.Deserialize<WorkerEnvelope>(
            json.TrimEnd('}') + ",\"futureField\":true}", options);

        Assert.NotNull(parsed);
        Assert.Equal(envelope.RequestId, parsed!.RequestId);
        Assert.Equal(envelope.Command, parsed.Command);
        Assert.Equal(42, parsed.Payload.GetProperty("value").GetInt32());
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(parsed, options));
    }

    [Fact]
    public void JobStatusContracts_RoundTripWithStableNamesAndIgnoreUnknownProperties()
    {
        var request = new JobStatusRequest(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "fieldworks-project"), "job-1");
        var requestJson = JsonSerializer.Serialize(request, WorkerJson.CreateOptions());
        Assert.Equal(
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"fieldworks-project\"},\"JobId\":\"job-1\"}",
            requestJson);
        var parsedRequest = JsonSerializer.Deserialize<JobStatusRequest>(
            requestJson.TrimEnd('}') + ",\"futureField\":true}", WorkerJson.CreateOptions());

        Assert.NotNull(parsedRequest);
        Assert.Equal(request, parsedRequest);
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(parsedRequest, WorkerJson.CreateOptions()));

        var response = new JobStatusResponse("job-1", "workspace-key", true, "dry-run",
            JobStatus.WaitingForBaseline, 2, "2026-08-23T12:00:00Z", false,
            JobFailureCategory.None, 4);
        var responseJson = JsonSerializer.Serialize(response, WorkerJson.CreateOptions());
        Assert.Equal(
            "{\"JobId\":\"job-1\",\"ProjectKey\":\"workspace-key\",\"Found\":true,\"Kind\":\"dry-run\",\"Status\":\"waiting-for-baseline\",\"Attempt\":2,\"UpdatedUtc\":\"2026-08-23T12:00:00Z\",\"CancellationRequested\":false,\"FailureCategory\":\"none\",\"Version\":4}",
            responseJson);
        var parsedResponse = JsonSerializer.Deserialize<JobStatusResponse>(
            responseJson.TrimEnd('}') + ",\"futureField\":true}", WorkerJson.CreateOptions());

        Assert.Equal(response, parsedResponse);
        Assert.Contains("waiting-for-baseline", responseJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"JobId\":\"job-1\"}")]
    [InlineData("{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"fieldworks-project\"}}")]
    public void JobStatusRequest_RejectsMissingRequiredProperties(string json)
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<JobStatusRequest>(
            json, WorkerJson.CreateOptions()));
    }

    [Fact]
    public void JobStatusResponse_RejectsMissingRequiredProperties()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<JobStatusResponse>(
            "{\"Found\":false}", WorkerJson.CreateOptions()));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("job\r")]
    [InlineData("job\n")]
    [InlineData("job\0")]
    public void JobStatusRequest_RejectsBlankAndControlCharacterIds(string jobId)
    {
        Assert.Throws<ArgumentException>(() => new JobStatusRequest(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "fieldworks-project"), jobId));
    }

    [Fact]
    public void JobStatusRequest_RejectsOversizedIds()
    {
        Assert.Throws<ArgumentException>(() => new JobStatusRequest(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "fieldworks-project"),
            new string('x', 257)));
    }

    [Fact]
    public void JobStatusCommand_IsClosedAndCapabilityBound()
    {
        Assert.True(WorkerCommands.IsKnown(WorkerCommands.JobStatus));
        Assert.Equal("jobs.v1", WorkerCommands.RequiredCapability(WorkerCommands.JobStatus));
        Assert.False(WorkerCommands.IsKnown("job.status.future"));

        var handshake = new WorkerHandshakeRequest("client", "1.0.0", new ProtocolRange(1, 1),
            Array.Empty<string>());
        var offer = new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), new[] { "jobs.v1" });
        var negotiated = WorkerHandshake.Negotiate(handshake, offer);

        Assert.DoesNotContain("jobs.v1", negotiated.Capabilities);
        Assert.Throws<ArgumentException>(() => new WorkerEnvelope(
            "request-1", "job.status.future", JsonDocument.Parse("{}").RootElement.Clone(), 1));
    }

    [Fact]
    public void BaselineCommands_AreClosedAndCapabilityBound()
    {
        Assert.Equal("baseline.offer", WorkerCommands.BaselineOffer);
        Assert.Equal("baseline.publish", WorkerCommands.BaselinePublish);
        Assert.True(WorkerCommands.IsKnown(WorkerCommands.BaselineOffer));
        Assert.True(WorkerCommands.IsKnown(WorkerCommands.BaselinePublish));
        Assert.Equal("baseline.v1", WorkerCommands.RequiredCapability(WorkerCommands.BaselineOffer));
        Assert.Equal("baseline.v1", WorkerCommands.RequiredCapability(WorkerCommands.BaselinePublish));
    }

    [Fact]
    public void JobStatusPayload_IsNotAnotherRegisteredCommandPayload()
    {
        var request = new JobStatusRequest(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "fieldworks-project"), "job-1");
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, WorkerJson.CreateOptions());

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<WorkerHandshakeRequest>(
            payload, WorkerJson.CreateOptions()));
    }

    [Fact]
    public void WorkerJson_ReturnsFreshOptionsAndUsesClosedEnumConverters()
    {
        var first = WorkerJson.CreateOptions();
        var second = WorkerJson.CreateOptions();
        var json = JsonSerializer.Serialize(JobStatus.Cancelled, first);

        Assert.NotSame(first, second);
        Assert.Equal("\"cancelled\"", json);
    }

    [Fact]
    public void Envelope_RejectsUnknownCommand()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();

        Assert.Throws<ArgumentException>(() => new WorkerEnvelope("request-1", "future.command", payload, 1));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<WorkerEnvelope>(
            "{\"RequestId\":\"request-1\",\"Command\":\"future.command\",\"Payload\":{},\"ProtocolVersion\":1}"));
    }

    [Fact]
    public void EventAndResponseEnvelopes_HaveDistinctFraming()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        var request = new WorkerEnvelope("request-1", WorkerCommands.Handshake, payload, 1);
        var progress = new WorkerEventEnvelope("event-1", WorkerCommands.BaselineRefreshRequested, payload, 1);
        var result = new WorkerEventResultEnvelope("event-1", WorkerEventOutcome.Accepted, payload, 1);

        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("event-1", progress.EventId);
        Assert.Equal(WorkerEventOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public void EventEnvelope_RejectsUnknownEventDiscriminator()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();

        Assert.Throws<ArgumentException>(() =>
            new WorkerEventEnvelope("event-1", "speculative.future-event", payload, 1));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<WorkerEventEnvelope>(
            "{\"EventId\":\"event-1\",\"Event\":\"speculative.future-event\",\"Payload\":{},\"ProtocolVersion\":1}"));
    }

    [Fact]
    public void EventResult_RejectsUnknownOutcome()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkerEventResultEnvelope("event-1", (WorkerEventOutcome)999, payload, 1));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkerEventResultEnvelope>(
            "{\"EventId\":\"event-1\",\"Outcome\":\"FutureOutcome\",\"Payload\":{},\"ProtocolVersion\":1}",
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
    }

    [Fact]
    public void UnknownProperties_AreSkippedAcrossWorkerDtoFamilies()
    {
        AssertUnknownPropertySkipped<WorkerHandshakeOffer>(
            "{\"ProductVersion\":\"3.5.0\",\"Protocols\":{\"Minimum\":2,\"Maximum\":3},\"Capabilities\":[\"jobs.v1\"],\"futureField\":true}");
        AssertUnknownPropertySkipped<WorkerEventEnvelope>(
            "{\"EventId\":\"event-1\",\"Event\":\"baseline.refresh.requested\",\"Payload\":{},\"ProtocolVersion\":1,\"futureField\":true}");
        AssertUnknownPropertySkipped<WorkerEventResultEnvelope>(
            "{\"EventId\":\"event-1\",\"Outcome\":\"Accepted\",\"Payload\":{},\"ProtocolVersion\":1,\"futureField\":true}");
        AssertUnknownPropertySkipped<BinaryTransferOffer>(
            "{\"TransferId\":\"transfer-1\",\"Direction\":\"upload\",\"PipeName\":\"pipe-1\",\"MaximumBytes\":1024,\"ExpiresAt\":\"2030-01-01T00:00:00+00:00\",\"futureField\":true}");
        AssertUnknownPropertySkipped<BinaryTransferCompletion>(
            "{\"TransferId\":\"transfer-1\",\"ByteCount\":12,\"Sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"futureField\":true}");
    }

    private static void AssertUnknownPropertySkipped<T>(string json)
    {
        var parsed = JsonSerializer.Deserialize<T>(json);

        Assert.NotNull(parsed);
        Assert.DoesNotContain("futureField", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void BinaryTransferShapes_ValidateBoundsAndDigest()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var offer = new BinaryTransferOffer("transfer-1", "upload", "pipe-1", 1024, expires);
        var completion = new BinaryTransferCompletion("transfer-1", 12, new string('a', 64));

        Assert.Equal(1024, offer.MaximumBytes);
        Assert.Equal(12, completion.ByteCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BinaryTransferOffer("transfer-1", "upload", "pipe-1", -1, expires));
        Assert.Throws<ArgumentException>(() =>
            new BinaryTransferCompletion("transfer-1", 12, "not-a-sha256"));
    }

    [Fact]
    public void WorkerCommands_RegistryContainsOnlySettledDiscriminators()
    {
        var commands = WorkerCommands.All.ToArray();
        var events = WorkerCommands.Events.ToArray();

        Assert.Equal(new[]
        {
            WorkerCommands.Handshake,
            WorkerCommands.JobStatus,
            WorkerCommands.BaselineOffer,
            WorkerCommands.BaselinePublish,
            WorkerCommands.LiveHostRegister,
            WorkerCommands.LiveHostObservationUpdate,
            WorkerCommands.LiveHostDisconnect,
        }, commands);
        Assert.Equal(new[]
        {
            WorkerCommands.BaselineRefreshRequested,
            WorkerCommands.ApplyRequested,
            WorkerCommands.ReconciliationRequested,
            WorkerCommands.CancellationRequested,
        }, events);
        Assert.Contains(WorkerCommands.Handshake, commands);
        Assert.Contains(WorkerCommands.JobStatus, commands);
        Assert.Contains(WorkerCommands.BaselineRefreshRequested, WorkerCommands.Events);
        Assert.Equal(commands.Length, commands.Distinct(StringComparer.Ordinal).Count());
        Assert.All(commands, command => Assert.True(WorkerCommands.IsKnown(command)));
        Assert.False(WorkerCommands.IsKnown("speculative.future-command"));
    }

    [Fact]
    public void WorkerCommands_RegistryViewsCannotBeMutatedThroughCollectionCasts()
    {
        var commands = (ICollection<string>)WorkerCommands.All;
        var events = (ICollection<string>)WorkerCommands.Events;

        Assert.Throws<NotSupportedException>(() => commands.Add("speculative.future-command"));
        Assert.Throws<NotSupportedException>(() => events.Add("speculative.future-event"));
        Assert.False(WorkerCommands.IsKnown("speculative.future-command"));
        Assert.False(WorkerCommands.IsKnownEvent("speculative.future-event"));
    }

    [Fact]
    public void ProductVersion_DoesNotDecideCompatibility()
    {
        var request = new WorkerHandshakeRequest(
            "motif-cli", "99.0.0", new ProtocolRange(1, 1), Array.Empty<string>());
        var worker = new WorkerHandshakeOffer(
            "0.1.0", new ProtocolRange(1, 1), Array.Empty<string>());

        var result = WorkerHandshake.Negotiate(request, worker);

        Assert.Equal(1, result.ProtocolVersion);
    }
}

internal static class WorkerProtocolTestLimits
{
    public const int MaximumCapabilities = 128;
}
