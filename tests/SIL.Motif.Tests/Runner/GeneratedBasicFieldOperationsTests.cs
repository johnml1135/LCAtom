using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Apply;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Round-trip proof, on a real project, for the nine fields generated alongside the
/// regenerated <c>setGloss</c>/<c>clearGloss</c> (covered by the untouched, pre-existing
/// <see cref="ProposalDryRunnerTests"/>/<see cref="ProposalApplierTests"/>). One representative per
/// LibLCM sig covered — MultiUnicode, MultiString, Boolean — round-trips
/// author -&gt; DryRun -&gt; Apply -&gt; read-back, plus the closed-payload-schema requirement (unknown
/// properties rejected).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class GeneratedBasicFieldOperationsTests : IDisposable
{
    private readonly LcmCache _cache;
    private readonly SeededProject _seed;

    public GeneratedBasicFieldOperationsTests(PristineProjectFixture pristine)
    {
        _cache = pristine.NewScratch();
        _seed = pristine.Seed;
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
    }

    /// <summary>
    /// <c>MoForm.Form</c> feeds LexEntry headword and homograph caches. That once forced a dispose/reload
    /// between DryRun and Apply, since a rollback left those derived caches stale. The DryRun now mutates
    /// a copy and deletes it (ADR 0016), so this field
    /// round-trips exactly like the MultiUnicode and boolean fields above, with no ceremony needed.
    /// </summary>
    [Fact]
    public void SetThenClear_MoFormForm_MultiUnicode_RoundTripsThroughDryRunAndApply()
    {
        var formGuid = FindEntryWithLexemeForm().LexemeFormOA.Guid;
        var target = CanonicalId.FromGuid(formGuid);
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        // --- set ---
        var setProposal = BuildProposal(MoFormFormOperationKinds.SetForm, target, new { ws = wsTag, text = "zzMotifTestForm" });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        var wsHandleForSet = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var formAfterSet = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(formGuid);
        Assert.Equal("zzMotifTestForm", formAfterSet.Form.get_String(wsHandleForSet).Text);
        var setEffect = Assert.Single(setReceipt.ActualEffects);
        Assert.Equal("zzMotifTestForm", setEffect.After[wsTag]);

        // --- clear: no save/reload between halves -- DryRun's baseline already reflects the set. ---
        var clearProposal = BuildProposal(MoFormFormOperationKinds.ClearForm, target, new { ws = wsTag });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        var wsHandleForClear = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var formAfterClear = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(formGuid);
        Assert.True(string.IsNullOrEmpty(formAfterClear.Form.get_String(wsHandleForClear).Text));
        var clearEffect = Assert.Single(clearReceipt.ActualEffects);
        Assert.Equal("zzMotifTestForm", clearEffect.Before[wsTag]);
        Assert.False(clearEffect.After.ContainsKey(wsTag));
    }

    [Fact]
    public void SetThenClear_LexEntryComment_MultiString_RoundTripsThroughDryRunAndApply()
    {
        var entry = FindAnyEntry();
        var target = CanonicalId.FromGuid(entry.Guid);
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultAnalWs);
        var wsHandle = _cache.DefaultAnalWs;

        var setProposal = BuildProposal(LexEntryCommentOperationKinds.SetComment, target, new { ws = wsTag, text = "zzMotifTestComment" });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.Equal("zzMotifTestComment", entry.Comment.get_String(wsHandle).Text);

        var clearProposal = BuildProposal(LexEntryCommentOperationKinds.ClearComment, target, new { ws = wsTag });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.True(string.IsNullOrEmpty(entry.Comment.get_String(wsHandle).Text));
    }

    [Fact]
    public void SetThenClear_LexEntryDoNotUseForParsing_Boolean_RoundTripsThroughDryRunAndApply()
    {
        var entry = FindAnyEntry();
        var target = CanonicalId.FromGuid(entry.Guid);

        // Known starting state: LibLCM requires an open unit of work for every mutating call, direct or not.
        var actionHandler = _cache.ServiceLocator.GetInstance<IActionHandler>();
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", actionHandler, () => entry.DoNotUseForParsing = false);

        var setProposal = BuildProposal(LexEntryDoNotUseForParsingOperationKinds.SetDoNotUseForParsing, target, new { value = true });
        var setDryRun = ScratchDryRun.Of(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.True(entry.DoNotUseForParsing);
        var setEffect = Assert.Single(setReceipt.ActualEffects);
        Assert.Equal("false", setEffect.Before["value"]);
        Assert.Equal("true", setEffect.After["value"]);

        var clearProposal = BuildProposal(LexEntryDoNotUseForParsingOperationKinds.ClearDoNotUseForParsing, target, new { });
        var clearDryRun = ScratchDryRun.Of(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.False(entry.DoNotUseForParsing);
    }

    [Fact]
    public void ClearGloss_TheNewVerbOnThePinnedField_RoundTripsThroughDryRunAndApply()
    {
        // clearGloss is new here; setGloss's own tests (ProposalDryRunnerTests/ProposalApplierTests) stand.
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var target = CanonicalId.FromGuid(sense.Guid);

        var clearProposal = BuildProposal(LexicalSenseOperationKinds.ClearGloss, target, new { ws = wsTag });
        var dryRun = ScratchDryRun.Of(_cache, clearProposal);
        var receipt = ProposalApplier.Apply(_cache, clearProposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.True(string.IsNullOrEmpty(sense.Gloss.get_String(wsHandle).Text));
        var effect = Assert.Single(receipt.ActualEffects);
        Assert.Equal(originalGloss, effect.Before[wsTag]);
        Assert.False(effect.After.ContainsKey(wsTag));
    }

    // No Dry Run rolls back any more, so no field needs a hand-maintained "feeds a derived cache" list.

    private ILexEntry FindAnyEntry() =>
        _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(_seed.FirstEntryId);

    private ILexEntry FindEntryWithLexemeForm() =>
        _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(_seed.FirstEntryId);

    private (ILexSense Sense, int WsHandle, string WsTag, string Gloss) FindSenseWithKnownGloss()
    {
        var sense = _cache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(_seed.FirstSenseId);
        var wsHandle = _cache.DefaultAnalWs;
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(wsHandle);
        return (sense, wsHandle, wsTag, SeededProject.FirstGloss);
    }

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

/// <summary>
/// Closed-schema rejection tests for the citationForm and doNotUseForParsing payloads. Pure JSON
/// parsing — no <c>LcmCache</c> involved, so unlike <see cref="GeneratedBasicFieldOperationsTests"/>
/// this class needs no <see cref="PristineProjectFixture"/>.
/// </summary>
public sealed class GeneratedBasicFieldOperationsSchemaTests
{
    [Fact]
    public void SetCitationForm_UnknownPayloadProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text = "x", extra = "not allowed" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<SIL.Motif.Contract.Parsing.ContractParseException>(
            () => LexEntryCitationFormSetPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void ClearCitationForm_TextPropertyIsUnknown_IsRejectedByTheClosedSchema()
    {
        // "text" is legal for set but not clear: the two payload shapes are independently closed.
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text = "should not be here" });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<SIL.Motif.Contract.Parsing.ContractParseException>(
            () => LexEntryCitationFormClearPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void SetDoNotUseForParsing_UnknownPayloadProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { value = true, extra = 1 });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<SIL.Motif.Contract.Parsing.ContractParseException>(
            () => LexEntryDoNotUseForParsingSetPayload.Parse(afterDocument.RootElement));
    }

    [Fact]
    public void ClearDoNotUseForParsing_AnyPayloadProperty_IsRejectedByTheClosedSchema()
    {
        var afterJson = JsonSerializer.Serialize(new { value = true });
        using var afterDocument = JsonDocument.Parse(afterJson);

        Assert.Throws<SIL.Motif.Contract.Parsing.ContractParseException>(
            () => LexEntryDoNotUseForParsingClearPayload.Parse(afterDocument.RootElement));
    }
}
