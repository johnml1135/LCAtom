using System.Text.Json;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Contract;

public sealed class LiveHostObservationContractTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CommandsAndPayloads_HaveClosedStableShapes()
    {
        var project = new ProjectLocator("C:\\workspace\\demo.fwdata", "project-identity");
        var observation = new LiveProjectObservation("host-session", 7, true, Digest);

        AssertJson(new LiveHostRegisterRequest(project, observation),
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"project-identity\"},\"Observation\":{\"HostSessionId\":\"host-session\",\"EditGeneration\":7,\"HasUnsavedChanges\":true,\"SavedSemanticDigest\":\"" + Digest + "\"}}");
        AssertJson(new LiveHostObservationUpdateRequest(project, observation),
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"project-identity\"},\"Observation\":{\"HostSessionId\":\"host-session\",\"EditGeneration\":7,\"HasUnsavedChanges\":true,\"SavedSemanticDigest\":\"" + Digest + "\"}}");
        AssertJson(new LiveHostDisconnectRequest(project, "host-session"),
            "{\"Project\":{\"FullFwDataPath\":\"C:\\\\workspace\\\\demo.fwdata\",\"FieldWorksProjectIdentity\":\"project-identity\"},\"HostSessionId\":\"host-session\"}");
        AssertJson(new LiveHostObservationResponse("project-key", true),
            "{\"ProjectKey\":\"project-key\",\"Accepted\":true}");

        Assert.Equal("live-host.register", WorkerCommands.LiveHostRegister);
        Assert.Equal("live-host.observation.update", WorkerCommands.LiveHostObservationUpdate);
        Assert.Equal("live-host.disconnect", WorkerCommands.LiveHostDisconnect);
        Assert.Equal("live-host.v1", WorkerCommands.RequiredCapability(WorkerCommands.LiveHostRegister));
    }

    [Fact]
    public void Payloads_RejectMissingOrInvalidAuthorityValues()
    {
        var project = new ProjectLocator("C:\\workspace\\demo.fwdata", "project-identity");
        var observation = new LiveProjectObservation("host-session", 7, false, Digest);

        Assert.Throws<ArgumentNullException>(() => new LiveHostRegisterRequest(null!, observation));
        Assert.Throws<ArgumentNullException>(() => new LiveHostRegisterRequest(project, null!));
        Assert.Throws<ArgumentException>(() => new LiveHostDisconnectRequest(project, " "));
        Assert.Throws<ArgumentException>(() => new LiveHostObservationResponse(" ", true));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<LiveHostRegisterRequest>(
            "{\"Project\":null,\"Observation\":null}", WorkerJson.CreateOptions()));
    }

    [Fact]
    public void LiveProjectObservation_RejectsOverLongOrControlCharacterHostSessionIds()
    {
        var overLong = new string('a', 257);
        Assert.Throws<ArgumentException>(() => new LiveProjectObservation(overLong, 0, false, Digest));
        var withControlCharacter = "host" + (char)7 + "session";
        Assert.Throws<ArgumentException>(() => new LiveProjectObservation(withControlCharacter, 0, false, Digest));
    }

    private static void AssertJson<T>(T value, string expected)
    {
        var json = JsonSerializer.Serialize(value, WorkerJson.CreateOptions());
        Assert.Equal(expected, json);
        Assert.Equal(value, JsonSerializer.Deserialize<T>(json, WorkerJson.CreateOptions()));
    }
}
