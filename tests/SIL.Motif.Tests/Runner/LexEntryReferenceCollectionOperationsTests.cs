using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.LcmUtils;
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
/// Round-trip proof for the three <c>rel/col</c>/<c>rel/seq</c>
/// <c>addRef</c>/<c>removeRef</c> fields against a real project: <c>LexEntry.DialectLabels</c>
/// (<c>rel/seq</c>), <c>.DoNotPublishIn</c>, <c>.DoNotShowMainEntryIn</c> (both <c>rel/col</c>). None
/// need a dispose/reload dance between DryRun and Apply (ADR 0016), because no DryRun touches the
/// live cache at all.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class LexEntryReferenceCollectionOperationsTests : IDisposable
{
    private const string MemberKey = "member";

    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public LexEntryReferenceCollectionOperationsTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    [Theory]
    [InlineData("DialectLabels")]
    [InlineData("DoNotPublishIn")]
    [InlineData("DoNotShowMainEntryIn")]
    public void AddRefThenRemoveRef_RoundTripsThroughDryRunAndApply_ForEachField(string field)
    {
        var (addKind, removeKind) = KindsFor(field);
        var entry = FindAnyEntry();
        var member = FindAPossibilityNotAlreadyOn(entry, field);
        var target = CanonicalId.FromGuid(entry.Guid);
        var memberId = CanonicalId.FromGuid(member.Guid);

        // --- addRef ---
        var addProposal = BuildProposal(addKind, target, memberId);
        var addDryRun = ScratchDryRun.Of(_cache, addProposal);
        var addEffect = Assert.Single(addDryRun.ExpectedEffects);
        Assert.DoesNotContain(memberId.Value, addEffect.Before.Keys);
        Assert.Contains(memberId.Value, addEffect.After.Keys);

        var addReceipt = ProposalApplier.Apply(_cache, addProposal, addDryRun.Anchor, "motif-tests");
        Assert.False(addReceipt.AlreadyApplied);
        Assert.Contains(member, CurrentMembers(entry, field));

        // --- removeRef ---
        var removeProposal = BuildProposal(removeKind, target, memberId);
        var removeDryRun = ScratchDryRun.Of(_cache, removeProposal);
        var removeEffect = Assert.Single(removeDryRun.ExpectedEffects);
        Assert.Contains(memberId.Value, removeEffect.Before.Keys);
        Assert.DoesNotContain(memberId.Value, removeEffect.After.Keys);

        var removeReceipt = ProposalApplier.Apply(_cache, removeProposal, removeDryRun.Anchor, "motif-tests");
        Assert.False(removeReceipt.AlreadyApplied);
        Assert.DoesNotContain(member, CurrentMembers(entry, field));
    }

    [Fact]
    public void AddRef_AlreadyPresentMember_IsIdempotent()
    {
        var entry = FindAnyEntry();
        var member = FindAPossibilityNotAlreadyOn(entry, "DoNotPublishIn");
        var target = CanonicalId.FromGuid(entry.Guid);
        var memberId = CanonicalId.FromGuid(member.Guid);

        var proposal = BuildProposal(LexEntryDoNotPublishInOperationKinds.AddRefDoNotPublishIn, target, memberId);
        var dryRun1 = ScratchDryRun.Of(_cache, proposal);
        ProposalApplier.Apply(_cache, proposal, dryRun1.Anchor, "motif-tests");
        Assert.Equal(1, entry.DoNotPublishInRC.Count(m => m.Guid == member.Guid));

        // A second, distinct addRef of the SAME member must not duplicate it.
        var secondProposal = BuildProposal(
            LexEntryDoNotPublishInOperationKinds.AddRefDoNotPublishIn, target, memberId, CanonicalId.Mint());
        var dryRun2 = ScratchDryRun.Of(_cache, secondProposal);
        ProposalApplier.Apply(_cache, secondProposal, dryRun2.Anchor, "motif-tests");
        Assert.Equal(1, entry.DoNotPublishInRC.Count(m => m.Guid == member.Guid));
    }

    [Fact]
    public void RemoveRef_AbsentMember_IsIdempotent()
    {
        var entry = FindAnyEntry();
        var member = FindAPossibilityNotAlreadyOn(entry, "DoNotShowMainEntryIn");
        var target = CanonicalId.FromGuid(entry.Guid);
        var memberId = CanonicalId.FromGuid(member.Guid);

        var proposal = BuildProposal(LexEntryDoNotShowMainEntryInOperationKinds.RemoveRefDoNotShowMainEntryIn, target, memberId);

        var dryRun = ScratchDryRun.Of(_cache, proposal);
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.DoesNotContain(member, entry.DoNotShowMainEntryInRC);
    }

    [Fact]
    public void Apply_MidProposalFailure_RollsBackTheAddRef_AndWritesNoAppliedLogEntry()
    {
        var entry = FindAnyEntry();
        var member = FindAPossibilityNotAlreadyOn(entry, "DialectLabels");
        var target = CanonicalId.FromGuid(entry.Guid);
        var memberId = CanonicalId.FromGuid(member.Guid);

        var op1 = BuildOperation(LexEntryDialectLabelsOperationKinds.AddRefDialectLabels, target, memberId);

        using var malformedAfter = JsonDocument.Parse("{}"); // missing required 'member'
        var op2 = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexEntryDialectLabelsOperationKinds.AddRefDialectLabels,
            target: target,
            after: malformedAfter.RootElement.Clone());

        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { op1, op2 });

        var footprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);
        var anchor = DummyAnchor() with { FootprintDigest = footprintDigest };

        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, anchor, "motif-tests"));

        Assert.DoesNotContain(member, entry.DialectLabelsRS); // op1's addRef rolled back too
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    [Fact]
    public void AddRef_MemberOfTheWrongType_ThrowsNamingTheMismatch()
    {
        var entry = FindAnyEntry();
        var target = CanonicalId.FromGuid(entry.Guid);
        var wrongTypeId = target; // a LexEntry, not a CmPossibility

        var proposal = BuildProposal(LexEntryDoNotPublishInOperationKinds.AddRefDoNotPublishIn, target, wrongTypeId);

        var ex = Assert.Throws<InvalidOperationException>(() => ScratchDryRun.Of(_cache, proposal));
        Assert.Contains("not a CmPossibility", ex.Message);
    }

    private ILexEntry FindAnyEntry() =>
        _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(_seed.FirstEntryId);

    private ICmPossibility FindAPossibilityNotAlreadyOn(ILexEntry entry, string field)
    {
        var current = CurrentMembers(entry, field).Select(m => m.Guid).ToHashSet();
        return _cache.ServiceLocator.GetInstance<ICmPossibilityRepository>().AllInstances()
            .First(p => !current.Contains(p.Guid));
    }

    private static IEnumerable<ICmPossibility> CurrentMembers(ILexEntry entry, string field) => field switch
    {
        "DialectLabels" => entry.DialectLabelsRS,
        "DoNotPublishIn" => entry.DoNotPublishInRC,
        "DoNotShowMainEntryIn" => entry.DoNotShowMainEntryInRC,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static (string AddKind, string RemoveKind) KindsFor(string field) => field switch
    {
        "DialectLabels" => (LexEntryDialectLabelsOperationKinds.AddRefDialectLabels, LexEntryDialectLabelsOperationKinds.RemoveRefDialectLabels),
        "DoNotPublishIn" => (LexEntryDoNotPublishInOperationKinds.AddRefDoNotPublishIn, LexEntryDoNotPublishInOperationKinds.RemoveRefDoNotPublishIn),
        "DoNotShowMainEntryIn" => (LexEntryDoNotShowMainEntryInOperationKinds.AddRefDoNotShowMainEntryIn, LexEntryDoNotShowMainEntryInOperationKinds.RemoveRefDoNotShowMainEntryIn),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static OperationEnvelope BuildOperation(string kind, CanonicalId target, CanonicalId memberId, CanonicalId? operationId = null)
    {
        var afterJson = JsonSerializer.Serialize(new { member = memberId.Value });
        using var afterDocument = JsonDocument.Parse(afterJson);

        return new OperationEnvelope(
            operationId: operationId ?? CanonicalId.Mint(),
            kind: kind,
            target: target,
            after: afterDocument.RootElement.Clone());
    }

    private static Proposal BuildProposal(string kind, CanonicalId target, CanonicalId memberId, CanonicalId? operationId = null)
    {
        var group = kind.Substring(0, kind.IndexOf('/'));
        return new Proposal(
            contractVersions: new Dictionary<string, string> { [group] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { BuildOperation(kind, target, memberId, operationId) });
    }

    private static BoundDryRunAnchor DummyAnchor() => new(
        FootprintDigest: "sha256:" + new string('0', 64),
        EffectDigest: "sha256:" + new string('0', 64),
        RunnerVersion: "test",
        LibLcmVersion: "test",
        ProjectionVersion: "1",
        DryRunAtUtc: "20260101T000000Z");
}

/// <summary>
/// Registry and payload-parsing tests for the <c>rel/col</c>/<c>rel/seq</c> reference-collection
/// verbs — no <c>LcmCache</c> involved, so unlike <see cref="LexEntryReferenceCollectionOperationsTests"/>
/// this class needs no <see cref="PristineProjectFixture"/>.
/// </summary>
public sealed class LexEntryReferenceCollectionSchemaTests
{
    [Fact]
    public void Move_IsDeferred_TheKindIsNotRegistered()
    {
        // Move is deliberately deferred for DialectLabels; proven here, not merely asserted.
        Assert.False(OperationKindRegistry.IsKnown("lexical/lexEntry/moveDialectLabels"));
        Assert.Throws<NotSupportedException>(
            () => OperationHandlerRegistry.Resolve("lexical/lexEntry/moveDialectLabels", "test"));
    }

    [Fact]
    public void AddRefPayload_UnknownProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { member = CanonicalId.Mint().Value, extra = 1 });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(() =>
            LexEntryDialectLabelsMemberPayload.Parse(afterDocument.RootElement, LexEntryDialectLabelsOperationKinds.AddRefDialectLabels));
    }

    [Fact]
    public void RemoveRefPayload_MissingMember_IsRejectedByTheClosedSchema()
    {
        using var afterDocument = JsonDocument.Parse("{}");

        Assert.Throws<ContractParseException>(() =>
            LexEntryDoNotPublishInMemberPayload.Parse(afterDocument.RootElement, LexEntryDoNotPublishInOperationKinds.RemoveRefDoNotPublishIn));
    }
}
