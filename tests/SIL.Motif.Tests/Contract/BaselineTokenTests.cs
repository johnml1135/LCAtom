using System;
using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using Xunit;

namespace SIL.Motif.Tests.Contract;

public sealed class BaselineTokenTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void BaselineToken_SerializesInStableWireOrderAndRoundTrips()
    {
        var token = new BaselineToken("project-1", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest, "host-1", 7);

        var json = JsonSerializer.Serialize(token);

        Assert.Equal(
            "{\"ProjectIdentity\":\"project-1\",\"SemanticSnapshotDigest\":\"" + Digest +
            "\",\"ProjectionVersion\":\"1\",\"CapturedUtc\":\"2026-08-23T12:34:56Z\",\"BundleDigest\":\"" +
            OtherDigest + "\",\"CapturedHostSessionId\":\"host-1\",\"CapturedEditGeneration\":7}", json);
        var parsed = JsonSerializer.Deserialize<BaselineToken>(json);

        Assert.Equal(token, parsed);
    }

    [Fact]
    public void BaselineToken_UnknownJsonPropertiesAreIgnoredLikeOtherContractDtos()
    {
        var json = "{\"ProjectIdentity\":\"project-1\",\"SemanticSnapshotDigest\":\"" + Digest +
            "\",\"ProjectionVersion\":\"1\",\"CapturedUtc\":\"2026-08-23T12:34:56Z\",\"BundleDigest\":\"" +
            OtherDigest + "\",\"futureField\":true}";

        var token = JsonSerializer.Deserialize<BaselineToken>(json);

        Assert.NotNull(token);
        Assert.Null(token!.CapturedHostSessionId);
    }

    [Fact]
    public void SemanticIdentity_ExcludesFreshnessAndBundleEvidence()
    {
        var first = new BaselineToken("project-1", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest, "host-1", 7);
        var second = new BaselineToken("project-1", Digest, "1", "2026-08-24T12:34:56Z", Digest, "host-2", 8);

        Assert.Equal(first.SemanticIdentity, second.SemanticIdentity);
        Assert.Equal(first.SemanticIdentityDigest, second.SemanticIdentityDigest);
        Assert.True(first.HasSameSemanticIdentity(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SemanticIdentity_ChangesForProjectSnapshotOrProjection()
    {
        var baseline = new BaselineToken("project-1", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest);
        var project = new BaselineToken("project-2", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest);
        var snapshot = new BaselineToken("project-1", OtherDigest, "1", "2026-08-23T12:34:56Z", OtherDigest);
        var projection = new BaselineToken("project-1", Digest, "2", "2026-08-23T12:34:56Z", OtherDigest);

        Assert.NotEqual(baseline.SemanticIdentityDigest, project.SemanticIdentityDigest);
        Assert.NotEqual(baseline.SemanticIdentityDigest, snapshot.SemanticIdentityDigest);
        Assert.NotEqual(baseline.SemanticIdentityDigest, projection.SemanticIdentityDigest);
    }

    [Theory]
    [InlineData(null, Digest, "1", "2026-08-23T12:34:56Z", OtherDigest)]
    [InlineData("", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest)]
    [InlineData("project-1", "not-a-digest", "1", "2026-08-23T12:34:56Z", OtherDigest)]
    [InlineData("project-1", Digest, "1", "2026-08-23T12:34:56Z", "not-a-digest")]
    [InlineData("project-1", Digest, "", "2026-08-23T12:34:56Z", OtherDigest)]
    [InlineData("project-1", Digest, "1", "2026-08-23T12:34:56-04:00", OtherDigest)]
    [InlineData("project-1", Digest, "1", "not-a-timestamp", OtherDigest)]
    public void BaselineToken_RejectsMalformedRequiredValues(
        string? projectIdentity, string semanticDigest, string projectionVersion, string capturedUtc, string bundleDigest)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new BaselineToken(projectIdentity!, semanticDigest, projectionVersion, capturedUtc, bundleDigest));
    }

    [Theory]
    [InlineData(-1)]
    public void BaselineToken_RejectsNegativeCapturedEditGeneration(long generation)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BaselineToken("project-1", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest, null, generation));
    }

    [Fact]
    public void LiveProjectObservation_ValidatesHostGenerationAndSavedDigest()
    {
        var observation = new LiveProjectObservation("host-1", 12, true, Digest);

        Assert.Equal("host-1", observation.HostSessionId);
        Assert.Equal(12, observation.EditGeneration);
        Assert.True(observation.HasUnsavedChanges);

        Assert.Throws<ArgumentException>(() => new LiveProjectObservation(" ", 0, false, Digest));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveProjectObservation("host-1", -1, false, Digest));
        Assert.Throws<ArgumentException>(() => new LiveProjectObservation("host-1", 0, false, "bad"));
    }
}
