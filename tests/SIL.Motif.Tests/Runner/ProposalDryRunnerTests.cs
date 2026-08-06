using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Stage C proof, on a real project: <see cref="ProposalDryRunner.Run"/> reads back real LibLCM
/// state (not the intent) to compute expected effects for a <c>lexical/lexSense/setGloss</c> change
/// set, is deterministic, and never leaves a mutation committed. See
/// docs/change-set-contract.md, "DryRun" / "Expected effects", and
/// docs/adr/0006-engine-reality-apply-readback-preflight.md.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ProposalDryRunnerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;

    public ProposalDryRunnerTests()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests", Guid.NewGuid().ToString("N"));
        var fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = new FwDataProjectLoader().LoadCache(fwDataPath);
    }

    public void Dispose()
    {
        _cache.Dispose();
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
    public void DryRun_SetGloss_ReportsRealBeforeAndAfterGloss_IsDeterministic_AndDoesNotMutateProject()
    {
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var newGloss = originalGloss + " (revised sense)";
        var canonicalId = CanonicalId.FromGuid(sense.Guid);

        var proposal = BuildSetGlossProposal(canonicalId, wsTag, newGloss);

        var firstDryRun = ScratchDryRun.Of(_cache, proposal);
        var secondDryRun = ScratchDryRun.Of(_cache, proposal);

        // (a) + "an effect is produced for the right canonical id/field": exactly one expected
        // effect, keyed by the sense's own canonical id and the gloss field, whose before/after
        // are the real read-back values, not an echo of the authored intent.
        var effect = Assert.Single(firstDryRun.ExpectedEffects);
        Assert.Equal(canonicalId, effect.CanonicalId);
        Assert.Equal(SnapshotFields.LexSenseGloss, effect.Field);
        Assert.Equal(originalGloss, effect.Before[wsTag]);
        Assert.Equal(newGloss, effect.After[wsTag]);

        // (b) the effect digest is stable across two identical dryRuns.
        Assert.StartsWith("sha256:", firstDryRun.EffectDigest);
        Assert.Equal(firstDryRun.EffectDigest, secondDryRun.EffectDigest);
        Assert.Equal(firstDryRun.IntentDigest, secondDryRun.IntentDigest);

        // (c) the project's actual gloss is UNCHANGED after the dry run: non-mutating rollback proven
        // by reading the live object again, not by trusting the returned DryRun.
        Assert.Equal(originalGloss, sense.Gloss.get_String(wsHandle).Text);
    }

    [Fact]
    public void DryRun_UnknownTarget_Throws()
    {
        var bogusTarget = CanonicalId.FromGuid(Guid.NewGuid());
        var proposal = BuildSetGlossProposal(bogusTarget, "en", "does not matter");

        Assert.ThrowsAny<Exception>(() => ScratchDryRun.Of(_cache, proposal));
    }

    // --- The scratch is single-use, and it is really mutated. ---

    /// <remarks>
    /// Two tests named DryRun_SetGloss_UnflaggedKind_DoesNotMarkCachePoisoned and
    /// DryRun_FlaggedKind_MarksCachePoisoned stood here, guarding a hand-maintained list of fields whose
    /// derived caches a rollback would leave stale. Both are gone with the rollback
    /// (docs/adr/0016-scratch-cache-copy-not-undo.md, amended 2026-08-06). What replaces them is not
    /// another classification but the two properties the new design actually rests on: the scratch is
    /// genuinely mutated (so read-back is real), and it can only be used once (so a baseline is always
    /// a baseline the live project was really in).
    /// </remarks>
    [Fact]
    public void DryRun_MutatesTheScratchAndDoesNotRevertIt()
    {
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var newGloss = originalGloss + " (written into the scratch)";
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(sense.Guid), wsTag, newGloss);

        // Hold the scratch's own cache reference so its post-run state can be read. DryRunScratch does
        // not hand it back — production has no reason to read a scratch after the run — but whoever
        // built it already has it, which is enough for a test and adds no API.
        var scratchRoot = Path.Combine(_tempRoot, "scratch-inspect");
        var scratchCache = new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot);
        using var scratch = DryRunScratch.Adopt(scratchCache, "test scratch, inspected after the run");

        var dryRun = ProposalDryRunner.Run(scratch, proposal);

        // The scratch really holds the new value: nothing reverted it, which is the whole point —
        // rollback is what skipped LibLCM's forward-only setter hooks and left derived caches stale.
        var scratchWsHandle = scratchCache.WritingSystemFactory.GetWsFromStr(wsTag);
        var scratchSense = scratchCache.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(sense.Guid);
        Assert.Equal(newGloss, scratchSense.Gloss.get_String(scratchWsHandle).Text);

        // And the live cache is untouched, for a better reason than before: the DryRun was never here.
        Assert.Equal(originalGloss, sense.Gloss.get_String(wsHandle).Text);
        Assert.Equal(newGloss, Assert.Single(dryRun.ExpectedEffects).After[wsTag]);
    }

    [Fact]
    public void DryRunScratch_RefusesASecondRun()
    {
        var (sense, _, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(sense.Guid), wsTag, originalGloss + " x");

        var scratchRoot = Path.Combine(_tempRoot, "scratch-single-use");
        using var scratch = DryRunScratch.Adopt(
            new ScratchCacheFactory().CreateFromFileCopy(_cache.ProjectId.Path, scratchRoot),
            "test scratch, reused on purpose");

        ProposalDryRunner.Run(scratch, proposal);

        // A second run here would read a baseline that already contains the first run's mutation, so
        // its footprint digest would describe a state the live project was never in — the same defect
        // as rolling a scratch back, just quieter. Refused rather than silently wrong.
        var reuse = Assert.Throws<InvalidOperationException>(() => ProposalDryRunner.Run(scratch, proposal));
        Assert.Contains("single-use", reuse.Message);
    }

    /// <summary>
    /// Enumerates senses via the real <see cref="ILexSenseRepository"/> and picks the first one
    /// with a non-empty gloss alternative, reading the current gloss text straight back from
    /// LibLCM (never hardcoded), matching the "enumerate senses, pick one with a known gloss"
    /// brief.
    /// </summary>
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

    private static Proposal BuildSetGlossProposal(CanonicalId target, string wsTag, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = wsTag, text });
        using var afterDocument = JsonDocument.Parse(afterJson);

        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: afterDocument.RootElement.Clone());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }
}
