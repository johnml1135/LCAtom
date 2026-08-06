using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.Caching;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// MOT-4 slice 2's round-trip proof for <c>LexEntry.LexemeForm</c> (owning/atomic, <c>create|delete</c>)
/// against a real project — the one field this slice's plan calls out as needing hand-written
/// entity-construction validity (ADR 0014; docs/plan-motif.md, MOT-4), because <c>MoForm</c> is
/// abstract and which concrete class to build depends on the authored morph type.
/// </summary>
/// <remarks>
/// Both verbs carry <c>manifest/liblcm-inventory.tsv</c> <c>AssessPoisonsCache=yes</c>
/// (<c>MorphTypeRASideEffects</c>/<c>LexemeFormOASideEffects</c> -&gt; <c>UpdateHomographs</c>), wired
/// into <see cref="DerivedCachePoisoningOperationKinds"/> alongside slice 1's
/// <c>CitationForm</c>/<c>Form</c> — so, exactly like <c>GeneratedBasicFieldOperationsTests</c>'
/// <c>MoFormForm</c> test, a DryRun poisons this cache instance and the test disposes/reloads before
/// the matching Apply.
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class LexEntryLexemeFormOperationsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private LcmCache _cache;

    public LexEntryLexemeFormOperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.LexemeForm", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = _loader.LoadCache(_fwDataPath);
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Create_OnAFreshEntry_StemMorphType_BuildsAConcreteMoStemAllomorph_RoundTripsDryRunAndApply()
    {
        var entryGuid = CreateBareEntry();
        var stemMorphTypeId = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem);
        var newFormId = CanonicalId.Mint();
        var vernWs = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        var proposal = BuildCreateProposal(CanonicalId.FromGuid(entryGuid), newFormId, stemMorphTypeId, vernWs, "zzMotifStem");

        var dryRun = ProposalDryRunner.Run(_cache, proposal);
        Assert.True(CacheReusability.IsPoisoned(_cache, out _));
        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Empty(effect.Before);
        Assert.Equal(newFormId.Value, effect.After[ReferenceFieldAlternativesKey]);

        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);

        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");
        Assert.False(receipt.AlreadyApplied);

        var entryRepo = _cache.ServiceLocator.GetInstance<ILexEntryRepository>();
        var entry = entryRepo.GetObject(entryGuid);

        Assert.NotNull(entry.LexemeFormOA);
        Assert.IsAssignableFrom<IMoStemAllomorph>(entry.LexemeFormOA);
        Assert.Equal(newFormId.ToGuid(), entry.LexemeFormOA.Guid);
        Assert.Equal(MoMorphTypeTags.kguidMorphStem, entry.LexemeFormOA.MorphTypeRA.Guid);
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(vernWs);
        Assert.Equal("zzMotifStem", entry.LexemeFormOA.Form.get_String(wsHandle).Text);
    }

    [Fact]
    public void Create_OnAFreshEntry_PrefixMorphType_BuildsAConcreteMoAffixAllomorph_RoundTripsDryRunAndApply()
    {
        // The other branch of the hand-written concrete-class decision (MoFormConcreteClassSelection):
        // an affix-shaped morph type must produce MoAffixAllomorph, never MoStemAllomorph.
        var entryGuid = CreateBareEntry();
        var prefixMorphTypeId = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphPrefix);
        var newFormId = CanonicalId.Mint();
        var vernWs = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        var proposal = BuildCreateProposal(CanonicalId.FromGuid(entryGuid), newFormId, prefixMorphTypeId, vernWs, "zzMotifPrefix");

        var dryRun = ProposalDryRunner.Run(_cache, proposal);
        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);
        ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        Assert.IsAssignableFrom<IMoAffixAllomorph>(entry.LexemeFormOA);
        Assert.Equal(MoMorphTypeTags.kguidMorphPrefix, entry.LexemeFormOA.MorphTypeRA.Guid);
    }

    [Fact]
    public void Create_IntoAnOccupiedSlot_ReplacesTheIncumbent_NeverAsADeletePlusCreate()
    {
        var (entryGuid, oldFormGuid) = FindEntryWithLexemeForm();
        var newFormId = CanonicalId.Mint();
        var stemMorphTypeId = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem);
        var vernWs = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        var proposal = BuildCreateProposal(CanonicalId.FromGuid(entryGuid), newFormId, stemMorphTypeId, vernWs, "zzMotifReplacement");

        var dryRun = ProposalDryRunner.Run(_cache, proposal);
        var oldRef = Assert.Single(dryRun.ExpectedEffects).Before;
        Assert.Equal(oldFormGuid.ToString(), CanonicalId.Parse(oldRef[ReferenceFieldAlternativesKey]).ToGuid().ToString());

        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);
        ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");

        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        Assert.Equal(newFormId.ToGuid(), entry.LexemeFormOA.Guid);

        // The single owning-atomic assignment performed the replacement -- this proposal contains
        // exactly one operation (never a separate authored delete for the incumbent).
        Assert.Single(proposal.Operations);
    }

    [Fact]
    public void Delete_RemovesTheLexemeForm_AndClearsTheSlot_RoundTripsDryRunAndApply()
    {
        var (entryGuid, oldFormGuid) = FindEntryWithLexemeForm();
        var proposal = BuildDeleteProposal(CanonicalId.FromGuid(entryGuid));

        var dryRun = ProposalDryRunner.Run(_cache, proposal);
        Assert.True(CacheReusability.IsPoisoned(_cache, out _));
        var effect = Assert.Single(dryRun.ExpectedEffects);
        Assert.Equal(oldFormGuid, CanonicalId.Parse(effect.Before[ReferenceFieldAlternativesKey]).ToGuid());
        Assert.Empty(effect.After);

        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);
        var receipt = ProposalApplier.Apply(_cache, proposal, dryRun.Anchor, "motif-tests");
        Assert.False(receipt.AlreadyApplied);

        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        Assert.Null(entry.LexemeFormOA);
        Assert.False(_cache.ServiceLocator.GetInstance<ICmObjectRepository>().IsValidObjectId(oldFormGuid));
    }

    [Fact]
    public void Delete_WhenThereIsNoLexemeForm_ThrowsAndLeavesTheProjectUnchanged()
    {
        var entryGuid = CreateBareEntry();
        var proposal = BuildDeleteProposal(CanonicalId.FromGuid(entryGuid));

        Assert.ThrowsAny<Exception>(() => ProposalDryRunner.Run(_cache, proposal));

        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        Assert.Null(entry.LexemeFormOA);
    }

    [Fact]
    public void Apply_MidProposalFailure_RollsBackTheCreate_AndWritesNoAppliedLogEntry()
    {
        // Rollback proof: op1 (a valid create) succeeds inside the unit of work, then op2 (a delete
        // whose target legitimately has no lexeme form) throws -- the whole Proposal is one unit of
        // work, so op1's mutation must be undone too, and the project must come back exactly where it
        // started. op2's target is deliberately a REAL, resolvable entry (not a nonexistent guid): a
        // bogus target would fail earlier, inside Apply's footprint pre-flight, before any unit of
        // work opens at all -- see ProposalApplierTests.Apply_UnknownTarget_... for that separate case
        // -- and this test wants the failure to happen mid-apply instead.
        var entryGuid = CreateBareEntry();
        var newFormId = CanonicalId.Mint();
        var stemMorphTypeId = CanonicalId.FromGuid(MoMorphTypeTags.kguidMorphStem);
        var vernWs = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        var entryWithNoFormGuid = CreateBareEntry();

        var createOp = BuildCreateOperation(CanonicalId.FromGuid(entryGuid), newFormId, stemMorphTypeId, vernWs, "zzMotifRollback");
        var bogusDeleteOp = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexEntryLexemeFormOperationKinds.DeleteLexemeForm,
            target: CanonicalId.FromGuid(entryWithNoFormGuid),
            after: EmptyObject());

        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { createOp, bogusDeleteOp });

        var footprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(_cache, proposal);
        var anchor = DummyAnchor() with { FootprintDigest = footprintDigest };

        Assert.ThrowsAny<Exception>(() => ProposalApplier.Apply(_cache, proposal, anchor, "motif-tests"));

        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        Assert.Null(entry.LexemeFormOA); // op1's create was rolled back
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
        Assert.True(CacheReusability.IsPoisoned(_cache, out _));
    }

    [Fact]
    public void CreatePayload_MissingMorphType_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text = "x" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(
            () => LexEntryLexemeFormCreatePayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void CreatePayload_UnknownProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new
        {
            morphType = CanonicalId.Mint().Value, ws = "en", text = "x", extra = "not allowed",
        });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(
            () => LexEntryLexemeFormCreatePayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void DeletePayload_AnyProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { reason = "cleanup" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<ContractParseException>(
            () => LexEntryLexemeFormDeletePayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void Create_UnresolvableMorphType_ThrowsNamingTheGuid()
    {
        // Resolving a canonical id that names no real object at all throws straight out of
        // ICmObjectRepository.GetObject (CanonicalIdResolver's own remarks: "no additional preflight
        // or existence check of its own"), the same behaviour TargetResolution already has for an
        // operation's own target -- this proves ReferenceFieldLowering.Resolve gets the identical
        // "let the real resolver fail loudly" treatment for a *referenced* id.
        var entryGuid = CreateBareEntry();
        var unresolvableGuid = Guid.NewGuid();
        var bogusMorphType = CanonicalId.FromGuid(unresolvableGuid);
        var proposal = BuildCreateProposal(
            CanonicalId.FromGuid(entryGuid), CanonicalId.Mint(), bogusMorphType, "en", "x");

        var ex = Assert.ThrowsAny<Exception>(() => ProposalDryRunner.Run(_cache, proposal));
        Assert.Contains(unresolvableGuid.ToString(), ex.Message);
    }

    [Fact]
    public void Create_MorphTypeResolvesButIsTheWrongType_ThrowsNamingTheMismatch()
    {
        // A canonical id that resolves to a REAL object of the wrong class is the case
        // ReferenceFieldLowering.Resolve itself is responsible for rejecting (as opposed to the
        // previous test, where the id names nothing at all).
        var entryGuid = CreateBareEntry();
        var wrongTypeId = CanonicalId.FromGuid(entryGuid); // a LexEntry, not a MoMorphType
        var proposal = BuildCreateProposal(
            CanonicalId.FromGuid(entryGuid), CanonicalId.Mint(), wrongTypeId, "en", "x");

        var ex = Assert.Throws<InvalidOperationException>(() => ProposalDryRunner.Run(_cache, proposal));
        Assert.Contains("not a MoMorphType", ex.Message);
        Assert.Contains("LexEntry", ex.Message);
    }

    [Fact]
    public void Create_ANonStandardMorphType_ThrowsNamingTheClosedTable()
    {
        // MoFormConcreteClassSelection's morph-type -> concrete-class table is closed by design
        // (docs/plan-motif.md, MOT-4: this mapping "lives outside MasterLCModel.xml"). A real,
        // resolvable MoMorphType whose guid is not one of the nineteen standard ones must fail
        // loudly naming the table, never guess a concrete class.
        var entryGuid = CreateBareEntry();
        var customMorphTypeGuid = Guid.NewGuid();
        var actionHandler = _cache.ServiceLocator.GetInstance<IActionHandler>();
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", actionHandler, () =>
        {
            _cache.ServiceLocator.GetInstance<IMoMorphTypeFactory>()
                .Create(customMorphTypeGuid, _cache.LangProject.LexDbOA.MorphTypesOA);
        });
        _loader.Save(_cache);

        var proposal = BuildCreateProposal(
            CanonicalId.FromGuid(entryGuid), CanonicalId.Mint(), CanonicalId.FromGuid(customMorphTypeGuid), "en", "x");

        var ex = Assert.Throws<InvalidOperationException>(() => ProposalDryRunner.Run(_cache, proposal));
        Assert.Contains(customMorphTypeGuid.ToString(), ex.Message);
        Assert.Contains("MasterLCModel.xml", ex.Message);
    }

    private const string ReferenceFieldAlternativesKey = "ref";

    /// <summary>
    /// Creates a bare <c>LexEntry</c> with no lexeme form and persists it, so a later dispose/reload
    /// (required by this field's <c>AssessPoisonsCache=yes</c> dry-run poisoning, matching
    /// <c>GeneratedBasicFieldOperationsTests</c>' <c>MoFormForm</c> test) still finds it -- Apply never
    /// saves itself (docs/change-set-contract.md, "Application Receipt"), so a from-scratch setup
    /// mutation that is never persisted would vanish on reload, same as any other unsaved edit.
    /// </summary>
    private Guid CreateBareEntry()
    {
        var actionHandler = _cache.ServiceLocator.GetInstance<IActionHandler>();
        ILexEntry entry = null!;
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", actionHandler, () =>
        {
            entry = _cache.ServiceLocator.GetInstance<ILexEntryFactory>().Create();
        });
        _loader.Save(_cache);
        return entry.Guid;
    }

    private (Guid EntryGuid, Guid FormGuid) FindEntryWithLexemeForm()
    {
        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().AllInstances()
            .First(e => e.LexemeFormOA is not null);
        return (entry.Guid, entry.LexemeFormOA.Guid);
    }

    private static OperationEnvelope BuildCreateOperation(
        CanonicalId target, CanonicalId entityId, CanonicalId morphType, string ws, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { morphType = morphType.Value, ws, text });
        using var afterDocument = JsonDocument.Parse(afterJson);

        return new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexEntryLexemeFormOperationKinds.CreateLexemeForm,
            entityId: entityId,
            target: target,
            after: afterDocument.RootElement.Clone());
    }

    private static Proposal BuildCreateProposal(
        CanonicalId target, CanonicalId entityId, CanonicalId morphType, string ws, string text) =>
        new(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { BuildCreateOperation(target, entityId, morphType, ws, text) });

    private static Proposal BuildDeleteProposal(CanonicalId target)
    {
        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexEntryLexemeFormOperationKinds.DeleteLexemeForm,
            target: target,
            after: EmptyObject());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static BoundDryRunAnchor DummyAnchor() => new(
        FootprintDigest: "sha256:" + new string('0', 64),
        EffectDigest: "sha256:" + new string('0', 64),
        RunnerVersion: "test",
        LibLcmVersion: "test",
        ProjectionVersion: "1",
        DryRunAtUtc: "20260101T000000Z");
}
