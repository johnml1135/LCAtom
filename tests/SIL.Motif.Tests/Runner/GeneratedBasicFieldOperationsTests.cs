using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
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
/// MOT-4 slice 1's round-trip proof, on a real project, for the nine fields generated alongside the
/// regenerated <c>setGloss</c>/<c>clearGloss</c> (covered by the untouched, pre-existing
/// <see cref="ProposalDryRunnerTests"/>/<see cref="ProposalApplierTests"/>). One representative per
/// LibLCM sig this slice covers — MultiUnicode, MultiString, Boolean — round-trips
/// author -&gt; DryRun -&gt; Apply -&gt; read-back, per docs/plan-motif.md MOT-4's acceptance
/// criterion, plus the closed-payload-schema requirement (unknown properties rejected).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class GeneratedBasicFieldOperationsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private LcmCache _cache;

    public GeneratedBasicFieldOperationsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Generated", Guid.NewGuid().ToString("N"));
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
    public void SetThenClear_MoFormForm_MultiUnicode_RoundTripsThroughDryRunAndApply()
    {
        // MoForm.Form has manifest/liblcm-inventory.tsv AssessPoisonsCache=yes, unlike Gloss/Comment/
        // DoNotUseForParsing above — so DryRun's mutate-then-rollback pass marks THIS cache instance
        // poisoned before it even runs (docs/adr/0006, decision 3; see the
        // DerivedCachePoisoningGuard_... test below for the guard knowing this field's real,
        // ADR-0023-derived kind name). Applying then requires a freshly reloaded cache — exactly
        // what CachePoisonedException's own message says to do, so this test (uniquely among this
        // file's round-trips) disposes and reloads between DryRun and Apply for each half, one cache
        // open at a time — never two live LcmCache instances on the same .fwdata concurrently, which
        // is its own hazard independent of the poisoning guard.
        var formGuid = FindEntryWithLexemeForm().LexemeFormOA.Guid;
        var target = CanonicalId.FromGuid(formGuid);
        var wsTag = _cache.WritingSystemFactory.GetStrFromWs(_cache.DefaultVernWs);

        // --- set ---
        var setProposal = BuildProposal(MoFormFormOperationKinds.SetForm, target, new { ws = wsTag, text = "zzMotifTestForm" });
        var setDryRun = ProposalDryRunner.Run(_cache, setProposal);
        Assert.True(CacheReusability.IsPoisoned(_cache, out _));

        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        var wsHandleForSet = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var formAfterSet = _cache.ServiceLocator.GetInstance<IMoFormRepository>().GetObject(formGuid);
        Assert.Equal("zzMotifTestForm", formAfterSet.Form.get_String(wsHandleForSet).Text);
        var setEffect = Assert.Single(setReceipt.ActualEffects);
        Assert.Equal("zzMotifTestForm", setEffect.After[wsTag]);

        // Apply never saves (that is the host's job, docs/change-set-contract.md, "Application
        // Receipt") — the set commits only inside _cache's in-memory UOW. Persist it now so the
        // reload below (required by the clear-side DryRun's own poisoning, same as above) actually
        // observes "zzMotifTestForm" as its baseline rather than the original on-disk content.
        _loader.Save(_cache);

        // --- clear: the cache _cache now points at is itself poisoned by the clear-side DryRun
        // below, exactly as the previous one was above, so dispose and reload once more ---
        var clearProposal = BuildProposal(MoFormFormOperationKinds.ClearForm, target, new { ws = wsTag });
        var clearDryRun = ProposalDryRunner.Run(_cache, clearProposal);
        Assert.True(CacheReusability.IsPoisoned(_cache, out _));

        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);
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
        var setDryRun = ProposalDryRunner.Run(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.Equal("zzMotifTestComment", entry.Comment.get_String(wsHandle).Text);

        var clearProposal = BuildProposal(LexEntryCommentOperationKinds.ClearComment, target, new { ws = wsTag });
        var clearDryRun = ProposalDryRunner.Run(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.True(string.IsNullOrEmpty(entry.Comment.get_String(wsHandle).Text));
    }

    [Fact]
    public void SetThenClear_LexEntryDoNotUseForParsing_Boolean_RoundTripsThroughDryRunAndApply()
    {
        var entry = FindAnyEntry();
        var target = CanonicalId.FromGuid(entry.Guid);

        // Known starting state, regardless of fixture data. A direct property setter still needs an
        // open unit of work — LibLCM enforces this on every mutating call, not just through the
        // generated Lowering classes — so this is set up the same way the pre-existing
        // ProposalApplierTests' own drift setup mutates outside the Motif operation surface.
        var actionHandler = _cache.ServiceLocator.GetInstance<IActionHandler>();
        UndoableUnitOfWorkHelper.Do("test setup", "test setup", actionHandler, () => entry.DoNotUseForParsing = false);

        var setProposal = BuildProposal(LexEntryDoNotUseForParsingOperationKinds.SetDoNotUseForParsing, target, new { value = true });
        var setDryRun = ProposalDryRunner.Run(_cache, setProposal);
        var setReceipt = ProposalApplier.Apply(_cache, setProposal, setDryRun.Anchor, "motif-tests");

        Assert.False(setReceipt.AlreadyApplied);
        Assert.True(entry.DoNotUseForParsing);
        var setEffect = Assert.Single(setReceipt.ActualEffects);
        Assert.Equal("false", setEffect.Before["value"]);
        Assert.Equal("true", setEffect.After["value"]);

        var clearProposal = BuildProposal(LexEntryDoNotUseForParsingOperationKinds.ClearDoNotUseForParsing, target, new { });
        var clearDryRun = ProposalDryRunner.Run(_cache, clearProposal);
        var clearReceipt = ProposalApplier.Apply(_cache, clearProposal, clearDryRun.Anchor, "motif-tests");

        Assert.False(clearReceipt.AlreadyApplied);
        Assert.False(entry.DoNotUseForParsing);
    }

    [Fact]
    public void ClearGloss_TheNewVerbOnThePinnedField_RoundTripsThroughDryRunAndApply()
    {
        // The new verb this slice adds to the pre-existing hand-written kind: setGloss's own tests
        // (ProposalDryRunnerTests/ProposalApplierTests) stay untouched, so clearGloss is proven here
        // instead, against the same real fixture and the same LexicalSenseOperationKinds this
        // regenerates.
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var target = CanonicalId.FromGuid(sense.Guid);

        var clearProposal = BuildProposal(LexicalSenseOperationKinds.ClearGloss, target, new { ws = wsTag });
        var dryRun = ProposalDryRunner.Run(_cache, clearProposal);
        var receipt = ProposalApplier.Apply(_cache, clearProposal, dryRun.Anchor, "motif-tests");

        Assert.False(receipt.AlreadyApplied);
        Assert.True(string.IsNullOrEmpty(sense.Gloss.get_String(wsHandle).Text));
        var effect = Assert.Single(receipt.ActualEffects);
        Assert.Equal(originalGloss, effect.Before[wsTag]);
        Assert.False(effect.After.ContainsKey(wsTag));
    }

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
        // clear's payload is { "ws": ... } only — "text" (legal for set) is not an allowed property
        // of clear, proving the two payload shapes are independently closed, not just "closed
        // against a shared allow-list."
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

    [Fact]
    public void DerivedCachePoisoningGuard_FlagsTheTwoRealAssessPoisonsCacheFields()
    {
        // manifest/liblcm-inventory.tsv's AssessPoisonsCache=yes for LexEntry.CitationForm and
        // MoForm.Form, both now live (dispatchable) kinds — the guard must know about their real,
        // ADR-0023-derived names, not just the stale pre-MOT-4 placeholders it already carried.
        Assert.True(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(LexEntryCitationFormOperationKinds.SetCitationForm));
        Assert.True(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(LexEntryCitationFormOperationKinds.ClearCitationForm));
        Assert.True(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(MoFormFormOperationKinds.SetForm));
        Assert.True(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(MoFormFormOperationKinds.ClearForm));

        // A field with AssessPoisonsCache=no must not be flagged.
        Assert.False(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(LexEntryCommentOperationKinds.SetComment));
    }

    private ILexEntry FindAnyEntry() =>
        _cache.ServiceLocator.GetInstance<ILexEntryRepository>().AllInstances().First();

    private ILexEntry FindEntryWithLexemeForm() =>
        _cache.ServiceLocator.GetInstance<ILexEntryRepository>().AllInstances()
            .First(e => e.LexemeFormOA is not null);

    private (ILexSense Sense, int WsHandle, string WsTag, string Gloss) FindSenseWithKnownGloss()
    {
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();

        foreach (var sense in senseRepo.AllInstances())
        {
            foreach (var wsHandle in sense.Gloss.AvailableWritingSystemIds)
            {
                var text = sense.Gloss.get_String(wsHandle).Text;
                if (!string.IsNullOrEmpty(text))
                {
                    var wsTag = _cache.WritingSystemFactory.GetStrFromWs(wsHandle);
                    return (sense, wsHandle, wsTag, text);
                }
            }
        }

        throw new InvalidOperationException(
            "Expected the real TestLangProj fixture to contain at least one LexSense with a non-empty gloss.");
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
