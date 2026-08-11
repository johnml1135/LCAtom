using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.Effects;
using SIL.Motif.Model.Snapshot;
using Xunit;

namespace SIL.Motif.Tests.Model;

/// <summary>
/// LibLCM-free unit tests for snapshot/effect JSON rendering and digest: fast checks
/// that complement <see cref="SIL.Motif.Tests.Runner.ProposalDryRunnerTests"/>'s real-project
/// proof. See docs/change-set-contract.md, "Canonical Semantic Snapshot" and "Expected effects".
/// </summary>
public class SnapshotAndEffectJsonTests
{
    private static readonly CanonicalId SenseId = CanonicalId.FromGuid(
        Guid.ParseExact("00010203-0405-0607-0809-0a0b0c0d0e0f", "D"));

    [Fact]
    public void ObjectSnapshotJsonWriter_KeysTheSnapshotSetByCanonicalId()
    {
        var snapshot = new ObjectSnapshot(
            SenseId,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [SnapshotFields.LexSenseGloss] = new Dictionary<string, string> { ["en"] = "run quickly on foot" },
            });

        var json = ObjectSnapshotJsonWriter.WriteJson(new[] { snapshot });
        using var document = JsonDocument.Parse(json);

        // Multi-snapshot: outer key IS the canonical id, so no redundant nested "fields" wrapper.
        Assert.True(document.RootElement.TryGetProperty(SenseId.Value, out var bySenseId));
        Assert.Equal(
            "run quickly on foot",
            bySenseId.GetProperty(SnapshotFields.LexSenseGloss).GetProperty("en").GetString());

        // Single-snapshot: no outer key to carry identity, so "canonicalId" and "fields" are explicit.
        var singleJson = ObjectSnapshotJsonWriter.WriteJson(snapshot);
        using var singleDocument = JsonDocument.Parse(singleJson);
        Assert.Equal(SenseId.Value, singleDocument.RootElement.GetProperty("canonicalId").GetString());
        Assert.Equal(
            "run quickly on foot",
            singleDocument.RootElement.GetProperty("fields").GetProperty(SnapshotFields.LexSenseGloss)
                .GetProperty("en").GetString());
    }

    [Fact]
    public void ObjectSnapshotJsonWriter_OmitsFieldsWithNoPopulatedAlternatives()
    {
        // "No populated alternative" means the field is absent entirely, never an empty-string alternative.
        var emptySnapshot = ObjectSnapshot.Empty(SenseId);

        var json = ObjectSnapshotJsonWriter.WriteJson(emptySnapshot);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(0, document.RootElement.GetProperty("fields").EnumerateObject().Count());
    }

    [Fact]
    public void ExpectedEffectSetDigest_IsStableAcrossRepeatedComputation()
    {
        var effects = new[]
        {
            new ExpectedEffect(
                SenseId,
                SnapshotFields.LexSenseGloss,
                new Dictionary<string, string> { ["en"] = "run quickly on foot" },
                new Dictionary<string, string> { ["en"] = "move quickly on foot" }),
        };

        var digest1 = ExpectedEffectSetDigest.Compute(effects);
        var digest2 = ExpectedEffectSetDigest.Compute(effects);

        Assert.StartsWith("sha256:", digest1);
        Assert.Equal(digest1, digest2);
    }

    [Fact]
    public void ExpectedEffectSetDigest_ChangesWhenBeforeDiffersEvenIfAfterIsTheSame()
    {
        // Hash the transition, not the destination: a changed `before` must move the digest too.
        var after = new Dictionary<string, string> { ["en"] = "move quickly on foot" };

        var digestA = ExpectedEffectSetDigest.Compute(new[]
        {
            new ExpectedEffect(SenseId, SnapshotFields.LexSenseGloss,
                new Dictionary<string, string> { ["en"] = "run quickly on foot" }, after),
        });

        var digestB = ExpectedEffectSetDigest.Compute(new[]
        {
            new ExpectedEffect(SenseId, SnapshotFields.LexSenseGloss,
                new Dictionary<string, string> { ["en"] = "sprint on foot" }, after),
        });

        Assert.NotEqual(digestA, digestB);
    }

    [Fact]
    public void ExpectedEffectSetDigest_IsOrderIndependentOverTheEffectSet()
    {
        // The effect set is semantically a set: authored order must not affect the digest.
        var otherId = CanonicalId.FromGuid(Guid.ParseExact("10111213-1415-1617-1819-1a1b1c1d1e1f", "D"));

        var effectOne = new ExpectedEffect(
            SenseId, SnapshotFields.LexSenseGloss,
            new Dictionary<string, string> { ["en"] = "run quickly on foot" },
            new Dictionary<string, string> { ["en"] = "move quickly on foot" });
        var effectTwo = new ExpectedEffect(
            otherId, SnapshotFields.LexSenseGloss,
            new Dictionary<string, string> { ["en"] = "a container" },
            new Dictionary<string, string> { ["en"] = "a vessel" });

        var digestForward = ExpectedEffectSetDigest.Compute(new[] { effectOne, effectTwo });
        var digestReversed = ExpectedEffectSetDigest.Compute(new[] { effectTwo, effectOne });

        Assert.Equal(digestForward, digestReversed);
    }
}
