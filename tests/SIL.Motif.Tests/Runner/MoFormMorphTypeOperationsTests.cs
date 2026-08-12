using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Round-trip proof for <c>MoForm.MorphType</c> (<c>rel/atomic</c>, <c>set|clear</c>)
/// against a real project. This field feeds <c>UpdateHomographs</c> (<c>MorphTypeRASideEffects</c>);
/// the DryRun runs on a throwaway copy and never mutates this cache, so no dispose/reload is needed
/// between DryRun and Apply (ADR 0016).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class MoFormMorphTypeOperationsTests : IDisposable
{
    private const string RefKey = "ref";

    private readonly string _tempRoot;
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public MoFormMorphTypeOperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.MorphType", Guid.NewGuid().ToString("N"));
        _cache = NewLangProjFixture.CreateCache(_tempRoot);
        _seed = SeededProject.Seed(_cache);
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Set_ChangesTheMorphType_RoundTripsThroughDryRunAndApply()
    {
        var form = FindStemAllomorph();
        var formGuid = form.Guid;
        var originalMorphTypeGuid = form.MorphTypeRA.Guid;
        var newMorphTypeGuid = MoMorphTypeTags.kguidMorphBoundStem;
        Assert.NotEqual(originalMorphTypeGuid, newMorphTypeGuid); // real precondition, not assumed

        var target = CanonicalId.FromGuid(formGuid);
        var proposal = BuildSetProposal(target, CanonicalId.FromGuid(newMorphTypeGuid));

        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal(originalMorphTypeGuid, CanonicalId.Parse(effect.Before[RefKey]).ToGuid());
        Assert.Equal(newMorphTypeGuid, CanonicalId.Parse(effect.After[RefKey]).ToGuid());

        // No dispose/reload needed: the DryRun ran on a throwaway copy, so this cache never saw it.
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");
        Assert.False(receipt.AlreadyApplied);

        var reloadedForm = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(formGuid);
        Assert.Equal(newMorphTypeGuid, reloadedForm.MorphTypeRA.Guid);
    }

    [Fact]
    public void Clear_DetachesTheMorphType_RoundTripsThroughDryRunAndApply()
    {
        var form = FindStemAllomorph();
        var formGuid = form.Guid;
        var originalMorphTypeGuid = form.MorphTypeRA.Guid;

        var target = CanonicalId.FromGuid(formGuid);
        var proposal = BuildClearProposal(target);

        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal(originalMorphTypeGuid, CanonicalId.Parse(effect.Before[RefKey]).ToGuid());
        Assert.Empty(effect.After);

        // No dispose/reload needed: the DryRun ran on a throwaway copy, so this cache never saw it.
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");
        Assert.False(receipt.AlreadyApplied);

        var reloadedForm = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(formGuid);
        Assert.Null(reloadedForm.MorphTypeRA);
    }

    [Fact]
    public void Apply_MidProposalFailure_RollsBackTheSet_AndWritesNoAppliedLogEntry()
    {
        // op2's payload is malformed, not its target, so failure happens in apply, not footprint pre-flight.
        var form = FindStemAllomorph();
        var target = CanonicalId.FromGuid(form.Guid);
        var op1 = BuildSetOperation(target, CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphBoundStem));

        using var malformedAfter = JsonDocument.Parse("{}");
        var op2 = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: MoFormMorphTypeOperationKinds.SetMorphType,
            target: target,
            after: malformedAfter.RootElement.Clone());

        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["grammar"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { op1, op2 });

        var footprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);
        var anchor = DummyAnchor() with { FootprintDigest = footprintDigest };

        var originalMorphTypeGuid = form.MorphTypeRA.Guid;
        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, anchor, "motif-tests"));

        Assert.Equal(originalMorphTypeGuid, form.MorphTypeRA.Guid); // op1 rolled back too
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    [Fact]
    public void SetPayload_UnknownProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { @ref = CanonicalId.Mint().Value, extra = 1 });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(() => MoFormMorphTypeSetPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void ClearPayload_AnyProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { @ref = CanonicalId.Mint().Value });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(() => MoFormMorphTypeClearPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void Set_ReferencingAnObjectOfTheWrongType_ThrowsNamingTheMismatch()
    {
        var form = FindStemAllomorph();
        var target = CanonicalId.FromGuid(form.Guid);
        var wrongTypeId = CanonicalId.FromGuid(form.Guid); // a MoForm, not a MoMorphType

        var proposal = BuildSetProposal(target, wrongTypeId);

        var ex = Assert.Throws<InvalidOperationException>(() => ScratchDryRun.Of(_cache, proposal));
        Assert.Contains("not a MoMorphType", ex.Message);
    }

    private IMoForm FindStemAllomorph() =>
        _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(_seed.FirstLexemeFormId);

    private static OperationEnvelope BuildSetOperation(CanonicalId target, CanonicalId refId)
    {
        var afterJson = JsonSerializer.Serialize(new { @ref = refId.Value });
        using var afterDocument = JsonDocument.Parse(afterJson);

        return new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: MoFormMorphTypeOperationKinds.SetMorphType,
            target: target,
            after: afterDocument.RootElement.Clone());
    }

    private static Proposal BuildSetProposal(CanonicalId target, CanonicalId refId) =>
        new(
            contractVersions: new Dictionary<string, string> { ["grammar"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { BuildSetOperation(target, refId) });

    private static Proposal BuildClearProposal(CanonicalId target)
    {
        using var afterDocument = JsonDocument.Parse("{}");
        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: MoFormMorphTypeOperationKinds.ClearMorphType,
            target: target,
            after: afterDocument.RootElement.Clone());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { ["grammar"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }

    private static BoundDryRunAnchor DummyAnchor() => new(
        FootprintDigest: "sha256:" + new string('0', 64),
        EffectDigest: "sha256:" + new string('0', 64),
        RunnerVersion: "test",
        LibLcmVersion: "test",
        ProjectionVersion: "1",
        DryRunAtUtc: "20260101T000000Z");
}
