using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// MOT-22's round-trip proof for <c>analysis/wfiWordform/setSpellingStatus</c> and its
/// <c>clearSpellingStatus</c> counterpart — the closest existing pattern is
/// <see cref="GeneratedBasicFieldOperationsTests"/>/<see cref="GeneratedSlice3OperationsTests"/>'s basic-field
/// coverage, adapted for this field's one difference from every basic field those tests cover: the payload is
/// a closed-range Integer rather than free text/boolean, so an out-of-range value must be rejected rather
/// than silently accepted or clamped, and <c>clear</c> writes the enum's zero member (<c>Undecided</c>)
/// rather than erasing anything.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class WfiWordformSpellingStatusOperationsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private LcmCache _cache;

    public WfiWordformSpellingStatusOperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.SpellingStatus", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = _loader.LoadCache(_fwDataPath);
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; a locked native handle should not fail the test
        }
    }

    [Fact]
    public void BothKinds_AreRegistered()
    {
        Assert.Equal("analysis/wfiWordform/setSpellingStatus", WfiWordformSpellingStatusOperationKinds.SetSpellingStatus);
        Assert.Equal("analysis/wfiWordform/clearSpellingStatus", WfiWordformSpellingStatusOperationKinds.ClearSpellingStatus);

        foreach (var kind in new[]
                 {
                     WfiWordformSpellingStatusOperationKinds.SetSpellingStatus,
                     WfiWordformSpellingStatusOperationKinds.ClearSpellingStatus,
                 })
        {
            Assert.True(OperationKindRegistry.IsKnown(kind));
            Assert.Contains(kind, OperationHandlerRegistry.RegisteredKinds);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SetSpellingStatusPayload_AcceptsEveryDocumentedValue(int value)
    {
        var afterJson = JsonSerializer.Serialize(new { value });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Equal(value, WfiWordformSpellingStatusSetPayload.Parse(afterDocument.RootElement));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void SetSpellingStatusPayload_OutOfRangeValue_FailsLoudly_RatherThanClamping(int outOfRangeValue)
    {
        // liblcm's own ValidateSpellingStatus exists, but this operation must not depend on it to
        // silently fix an out-of-range value -- the closed payload rejects it before any LibLCM call.
        var afterJson = JsonSerializer.Serialize(new { value = outOfRangeValue });
        using var afterDocument = JsonDocument.Parse(afterJson);

        var ex = Assert.Throws<ContractParseException>(
            () => WfiWordformSpellingStatusSetPayload.Parse(afterDocument.RootElement));
        Assert.Contains(outOfRangeValue.ToString(), ex.Message);
    }

    [Fact]
    public void SetSpellingStatusPayload_UnknownProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { value = 1, extra = "not allowed" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(() => WfiWordformSpellingStatusSetPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void SetSpellingStatusPayload_NonIntegerValue_IsRejected()
    {
        var afterJson = JsonSerializer.Serialize(new { value = "incorrect" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(() => WfiWordformSpellingStatusSetPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void DryRun_SetSpellingStatus_ExpectedEffectReportsBeforeAndAfterValues()
    {
        var wordform = FindAnyWordform();
        var target = CanonicalId.FromGuid(wordform.Guid);

        // Known starting state, regardless of what the fixture's own wordform already carries.
        UndoableUnitOfWorkHelper.Do(
            "test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(),
            () => wordform.SpellingStatus = 0);

        var proposal = BuildProposal(WfiWordformSpellingStatusOperationKinds.SetSpellingStatus, target, new { value = 2 });
        var dryRun = ScratchDryRun.Of(_cache, proposal);

        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal("0", effect.Before["value"]);
        Assert.Equal("2", effect.After["value"]);

        // The dry run must not have mutated the live cache (ADR 0016).
        Assert.Equal(0, wordform.SpellingStatus);
    }

    [Fact]
    public void SetSpellingStatus_RoundTripsThroughDryRunAndApply()
    {
        var wordform = FindAnyWordform();
        var target = CanonicalId.FromGuid(wordform.Guid);

        UndoableUnitOfWorkHelper.Do(
            "test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(),
            () => wordform.SpellingStatus = 0);

        var proposal = BuildProposal(WfiWordformSpellingStatusOperationKinds.SetSpellingStatus, target, new { value = 2 });
        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.Equal(2, wordform.SpellingStatus);

        var effect = Assert.Single(receipt.ActualEffects);
        Assert.Equal("0", effect.Before["value"]);
        Assert.Equal("2", effect.After["value"]);
    }

    [Fact]
    public void ClearSpellingStatusPayload_RejectsAnyProperty_IncludingTheSetVerbsOwn()
    {
        using var empty = JsonDocument.Parse("{}");
        WfiWordformSpellingStatusClearPayload.Parse(empty.RootElement); // does not throw

        using var withValue = JsonDocument.Parse(JsonSerializer.Serialize(new { value = 0 }));
        Assert.Throws<ContractParseException>(
            () => WfiWordformSpellingStatusClearPayload.Parse(withValue.RootElement));
    }

    /// <summary>
    /// The behaviour the manifest's rationale claims: clearing retracts a judgement by writing the zero
    /// member, <c>Undecided</c>. It is a different act from asserting <c>Correct</c>, which is why this
    /// field keeps the derived <c>clear</c> rather than making a caller spell it <c>set 0</c>.
    /// </summary>
    [Fact]
    public void ClearSpellingStatus_RoundTripsThroughDryRunAndApply_WritingUndecided()
    {
        var wordform = FindAnyWordform();
        var target = CanonicalId.FromGuid(wordform.Guid);

        UndoableUnitOfWorkHelper.Do(
            "test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(),
            () => wordform.SpellingStatus = 2);

        var proposal = BuildProposal(WfiWordformSpellingStatusOperationKinds.ClearSpellingStatus, target, new { });
        var dryRun = ScratchDryRun.Of(_cache, proposal);

        var expected = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal("2", expected.Before["value"]);
        Assert.Equal("0", expected.After["value"]);
        Assert.Equal(2, wordform.SpellingStatus); // the dry run left the live cache alone (ADR 0016)

        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.Equal(0, wordform.SpellingStatus);

        var actual = Assert.Single(receipt.ActualEffects);
        Assert.Equal("2", actual.Before["value"]);
        Assert.Equal("0", actual.After["value"]);
    }

    private IWfiWordform FindAnyWordform() =>
        _cache.ServiceLocator.GetInstance<IWfiWordformRepository>().AllInstances().First();

    private static Proposal BuildProposal(string kind, CanonicalId target, object after)
    {
        var afterJson = JsonSerializer.Serialize(after);
        using var afterDocument = JsonDocument.Parse(afterJson);

        var group = kind.Substring(0, kind.IndexOf('/'));
        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: kind,
            target: target,
            after: afterDocument.RootElement.Clone());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { [group] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }
}
