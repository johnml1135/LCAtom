using System.Text.Json;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Caching;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Runner;

/// <summary>
/// Stage C proof, on a real project: <see cref="ProposalDryRunner.Run"/> reads back real LibLCM
/// state (not the intent) to compute expected effects for a <c>lexical/sense/setGloss</c> change
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

        var firstDryRun = ProposalDryRunner.Run(_cache, proposal);
        var secondDryRun = ProposalDryRunner.Run(_cache, proposal);

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

        Assert.ThrowsAny<Exception>(() => ProposalDryRunner.Run(_cache, proposal));
    }

    // --- Defect 4: the "may poison a derived cache" guard. ---

    [Fact]
    public void DryRun_SetGloss_UnflaggedKind_DoesNotMarkCachePoisoned()
    {
        var (sense, _, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var proposal = BuildSetGlossProposal(CanonicalId.FromGuid(sense.Guid), wsTag, originalGloss + " x");

        Assert.False(CacheReusability.IsPoisoned(_cache, out _));
        ProposalDryRunner.Run(_cache, proposal);
        Assert.False(CacheReusability.IsPoisoned(_cache, out _));
    }

    [Fact]
    public void DryRun_FlaggedKind_MarksCachePoisoned()
    {
        // No operation handler exists yet for a flagged kind (Stage C/D implement only setGloss) —
        // the guard must still mark the cache before Run's dispatch loop rejects it as
        // unsupported, per DerivedCachePoisoningOperationKinds' remarks ("wired in ahead of the
        // operation kinds that will need it").
        var flaggedKind = "lexical/entry/setLexemeForm";
        Assert.True(DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(flaggedKind));

        var afterJson = JsonSerializer.Serialize(new { ws = "en", text = "does not matter" });
        using var afterDocument = JsonDocument.Parse(afterJson);
        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: flaggedKind,
            target: CanonicalId.FromGuid(Guid.NewGuid()),
            after: afterDocument.RootElement.Clone());
        var proposal = new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });

        Assert.False(CacheReusability.IsPoisoned(_cache, out _));

        // Not (yet) dispatchable — Run still correctly refuses an unsupported kind — but the
        // poisoning guard must have already run before that refusal.
        Assert.ThrowsAny<Exception>(() => ProposalDryRunner.Run(_cache, proposal));

        Assert.True(CacheReusability.IsPoisoned(_cache, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
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
