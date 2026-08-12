using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Round-trip proof, on a real project, for a representative field per shape
/// <see cref="SIL.Motif.Generator.Emit.Slice3CatalogWriter"/> emits: basic MultiUnicode
/// (<c>CmPossibility.Abbreviation</c>), basic Boolean (<c>MoInflAffixSlot.Optional</c>),
/// <c>rel/atomic</c> (<c>MoStemMsa.PartOfSpeech</c>), <c>rel/col</c> (<c>MoStemMsa.FromPartsOfSpeech</c>),
/// and <c>rel/seq</c> (<c>MoInflAffixTemplate.PrefixSlots</c>) — the same shapes already proved on the
/// lexical-entry family, now exercised on grammar classes as well.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class GeneratedSlice3OperationsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public GeneratedSlice3OperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Slice3", Guid.NewGuid().ToString("N"));
        _cache = NewLangProjFixture.CreateCache(_tempRoot);
        _seed = SeededProject.Seed(_cache);
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
    public void SetThenClear_CmPossibilityAbbreviation_MultiUnicode_RoundTripsThroughDryRunAndApply()
    {
        // A part of speech IS-A CmPossibility (ADR 0023), so it's a valid target for a base-class field.
        var pos = FindAnyPartOfSpeech();
        var target = CanonicalId.FromGuid(pos.Guid);
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultAnalWs);

        var setProposal = BuildProposal(
            CmPossibilityAbbreviationOperationKinds.SetAbbreviation, target, new { ws = wsTag, text = "zzMotifAbbr" });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.Equal("zzMotifAbbr", pos.Abbreviation.get_String(_cache.DefaultAnalWs).Text);

        var clearProposal = BuildProposal(CmPossibilityAbbreviationOperationKinds.ClearAbbreviation, target, new { ws = wsTag });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.True(string.IsNullOrEmpty(pos.Abbreviation.get_String(_cache.DefaultAnalWs).Text));
    }

    [Fact]
    public void SetThenClear_MoInflAffixSlotOptional_Boolean_RoundTripsThroughDryRunAndApply()
    {
        var slot = CreateInflAffixSlot();
        var target = CanonicalId.FromGuid(slot.Guid);

        var setProposal = BuildProposal(MoInflAffixSlotOptionalOperationKinds.SetOptional, target, new { value = true });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.True(slot.Optional);

        var clearProposal = BuildProposal(MoInflAffixSlotOptionalOperationKinds.ClearOptional, target, new { });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.False(slot.Optional);
    }

    [Fact]
    public void SetThenClear_MoStemMsaPartOfSpeech_RelAtomic_RoundTripsThroughDryRunAndApply()
    {
        var msa = FindAnyStemMsa();
        var newPos = CreatePartOfSpeech();
        var target = CanonicalId.FromGuid(msa.Guid);

        // Known starting state, asserted rather than assumed from the seed.
        UndoableUnitOfWorkHelper.Do(
            "test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(),
            () => msa.PartOfSpeechRA = null);

        var setProposal = BuildProposal(
            MoStemMsaPartOfSpeechOperationKinds.SetPartOfSpeech, target, new { @ref = CanonicalId.FromGuid(newPos.Guid).Value });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.Equal(newPos.Guid, msa.PartOfSpeechRA?.Guid);

        var clearProposal = BuildProposal(MoStemMsaPartOfSpeechOperationKinds.ClearPartOfSpeech, target, new { });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.Null(msa.PartOfSpeechRA);
    }

    [Fact]
    public void AddThenRemoveRef_MoStemMsaFromPartsOfSpeech_RelCol_RoundTripsThroughDryRunAndApply()
    {
        var msa = FindAnyStemMsa();
        var pos = CreatePartOfSpeech();
        var target = CanonicalId.FromGuid(msa.Guid);
        var member = CanonicalId.FromGuid(pos.Guid);

        var addProposal = BuildProposal(
            MoStemMsaFromPartsOfSpeechOperationKinds.AddRefFromPartsOfSpeech, target, new { member = member.Value });
        var addDryRun = ScratchDryRun.Of(_cache, addProposal);
        var addReceipt = ProposalApplier.Apply(_cache, addProposal, addDryRun.Anchor, "motif-tests");

        Assert.False(addReceipt.AlreadyApplied);
        Assert.Contains(pos, msa.FromPartsOfSpeechRC);

        var removeProposal = BuildProposal(
            MoStemMsaFromPartsOfSpeechOperationKinds.RemoveRefFromPartsOfSpeech, target, new { member = member.Value });
        var removeDryRun = ScratchDryRun.Of(_cache, removeProposal);
        var removeReceipt = ProposalApplier.Apply(_cache, removeProposal, removeDryRun.Anchor, "motif-tests");

        Assert.False(removeReceipt.AlreadyApplied);
        Assert.DoesNotContain(pos, msa.FromPartsOfSpeechRC);
    }

    [Fact]
    public void AddThenRemoveRef_MoInflAffixTemplatePrefixSlots_RelSeq_RoundTripsThroughDryRunAndApply()
    {
        var pos = CreatePartOfSpeech();
        IMoInflAffixTemplate template = null!;
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(), () =>
        {
            template = _cache.ServiceLocator.GetInstance<IMoInflAffixTemplateFactory>().Create();
            pos.AffixTemplatesOS.Add(template);
        });
        var slot = CreateInflAffixSlot(pos);

        var target = CanonicalId.FromGuid(template.Guid);
        var member = CanonicalId.FromGuid(slot.Guid);

        var addProposal = BuildProposal(
            MoInflAffixTemplatePrefixSlotsOperationKinds.AddRefPrefixSlots, target, new { member = member.Value });
        var addDryRun = ScratchDryRun.Of(_cache, addProposal);
        var addReceipt = ProposalApplier.Apply(_cache, addProposal, addDryRun.Anchor, "motif-tests");

        Assert.False(addReceipt.AlreadyApplied);
        Assert.Contains(slot, template.PrefixSlotsRS);

        var removeProposal = BuildProposal(
            MoInflAffixTemplatePrefixSlotsOperationKinds.RemoveRefPrefixSlots, target, new { member = member.Value });
        var removeDryRun = ScratchDryRun.Of(_cache, removeProposal);
        var removeReceipt = ProposalApplier.Apply(_cache, removeProposal, removeDryRun.Anchor, "motif-tests");

        Assert.False(removeReceipt.AlreadyApplied);
        Assert.DoesNotContain(slot, template.PrefixSlotsRS);
    }

    private IPartOfSpeech FindAnyPartOfSpeech() =>
        (IPartOfSpeech)_cache.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(_seed.PartOfSpeechId);

    private IPartOfSpeech CreatePartOfSpeech()
    {
        IPartOfSpeech pos = null!;
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(), () =>
        {
            pos = _cache.ServiceLocator.GetInstance<IPartOfSpeechFactory>().Create();
            _cache.LangProject.PartsOfSpeechOA.PossibilitiesOS.Add(pos);
        });
        return pos;
    }

    private IMoInflAffixSlot CreateInflAffixSlot(IPartOfSpeech? owner = null)
    {
        var pos = owner ?? CreatePartOfSpeech();
        IMoInflAffixSlot slot = null!;
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", _cache.ServiceLocator.GetInstance<IActionHandler>(), () =>
        {
            slot = _cache.ServiceLocator.GetInstance<IMoInflAffixSlotFactory>().Create();
            pos.AffixSlotsOC.Add(slot);
        });
        return slot;
    }

    private IMoStemMsa FindAnyStemMsa() =>
        (IMoStemMsa)_cache.ServiceLocator.GetInstance<ILexSenseRepository>()
            .GetObject(_seed.FirstSenseId).MorphoSyntaxAnalysisRA!;

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
