using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;
using ContractIntentDigest = SIL.Motif.Contract.Canonicalization.IntentDigest;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Pins <c>TargetResolution</c>'s two fail-closed guards -- missing <c>target</c> and a target of the
/// wrong type -- as reached through <see cref="ProposalApplier.Apply"/>'s footprint pre-flight, via
/// <c>IOperationHandler.ReadCurrentFootprint</c>, which <see cref="FootprintProbe"/> calls before Apply
/// opens any unit of work. Both tests assert the refusal happens before mutation: nothing is committed.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class TargetResolutionRefusalsTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public TargetResolutionRefusalsTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Fact]
    public void Apply_OperationWithNoTarget_RefusesBeforeAnyMutation_NamingTheOperationAndKind()
    {
        var operationId = CanonicalId.Mint();
        var operation = BuildSetGlossOperation(operationId, target: null);
        var proposal = BuildProposal(operation);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProposalApplier.Apply(_cache, proposal, DummyAnchor(proposal), "motif-tests"));

        Assert.Contains($"Operation '{operationId.Value}'", ex.Message);
        Assert.Contains($"kind '{LexicalSenseOperationKinds.SetGloss}'", ex.Message);
        Assert.Contains("requires 'target'", ex.Message);

        // Refused during the footprint pre-flight, before any unit of work opened: nothing committed.
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    [Fact]
    public void Apply_TargetOfTheWrongType_RefusesBeforeAnyMutation_NamingExpectedAndActualType()
    {
        // A LexEntry guid presented where lexical/lexSense/setGloss expects an ILexSense.
        var wrongTypeTarget = CanonicalId.FromGuid(_seed.FirstEntryId);
        var operation = BuildSetGlossOperation(CanonicalId.Mint(), wrongTypeTarget);
        var proposal = BuildProposal(operation);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProposalApplier.Apply(_cache, proposal, DummyAnchor(proposal), "motif-tests"));

        Assert.Contains($"Target '{wrongTypeTarget.Value}'", ex.Message);
        Assert.Contains("is not a LexSense", ex.Message);
        Assert.Contains("it is a", ex.Message);

        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    private static OperationEnvelope BuildSetGlossOperation(CanonicalId operationId, CanonicalId? target)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text = "does not matter" });
        using var afterDocument = JsonDocument.Parse(afterJson);
        return new OperationEnvelope(
            operationId: operationId,
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: afterDocument.RootElement.Clone());
    }

    private static Proposal BuildProposal(OperationEnvelope operation) => new(
        contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
        proposalId: CanonicalId.Mint(),
        requires: null,
        operations: new[] { operation });

    /// <summary>An anchor bound to <paramref name="proposal"/> with an otherwise placeholder footprint.</summary>
    private static BoundDryRunAnchor DummyAnchor(Proposal proposal) => new(
        IntentDigest: ContractIntentDigest.Compute(proposal),
        FootprintDigest: "sha256:" + new string('0', 64),
        EffectDigest: "sha256:" + new string('0', 64),
        RunnerVersion: "test",
        LibLcmVersion: "test",
        ProjectionVersion: "1",
        DryRunAtUtc: "20260101T000000Z");
}
