using System.Linq;
using System.Text.Json;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Host.LcmUtils;
using SIL.LCAtom.Runner.Apply;
using SIL.LCAtom.Runner.AppliedLog;
using SIL.LCAtom.Runner.Operations;
using SIL.LCAtom.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.LCAtom.Tests.Apply;

/// <summary>
/// Stage D proof, on a real project: <see cref="ChangeSetApplier.Apply"/> commits a
/// <c>lexical/sense/setGloss</c> Change Set, writes exactly one applied-log entry inside the same
/// unit of work, and the result survives a real save + reopen from disk — not merely an in-memory
/// assertion. Also proves idempotence (a second Apply of the same Change Set against the reopened
/// project does nothing) and that a distinct Change Set adds a second, distinct log entry. See
/// docs/change-set-contract.md, "Application Receipt", and docs/applied-log.md.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ChangeSetApplierTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private LcmCache _cache;

    public ChangeSetApplierTests()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.LCAtom.Tests", Guid.NewGuid().ToString("N"));
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
    public void Apply_SetGloss_CommitsAndPersistsAcrossReopen_IsIdempotent_AndUnionsWithADistinctChangeSet()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss(_cache);
        var canonicalId = CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised sense, Stage D)";

        // Baseline: whatever CmResource entries the real fixture already carries (foreign to
        // LCAtom) must be left completely untouched by everything below.
        var foreignEntriesBefore = ReadForeignResourceVersions(_cache);

        var changeSet = BuildSetGlossChangeSet(canonicalId, wsTag, newGloss);
        const string applierIdentity = "lcatom-tests";
        const string description = "Stage D test: revise sense gloss";

        // --- 1. First apply: a real commit, not a rollback. ---
        var receipt = ChangeSetApplier.Apply(_cache, changeSet, applierIdentity, description);

        Assert.False(receipt.AlreadyApplied);
        Assert.Equal(changeSet.ChangeSetId, receipt.ChangeSetId);
        var effect = Assert.Single(receipt.ActualEffects);
        Assert.Equal(canonicalId, effect.CanonicalId);
        Assert.Equal(newGloss, effect.After[wsTag]);
        Assert.Equal(receipt.AppliedLogEntry.ChangeSetId, changeSet.ChangeSetId.ToGuid());
        Assert.Equal(applierIdentity, receipt.AppliedLogEntry.User);

        // Gloss is changed in the live (not-yet-saved) cache.
        var wsHandle = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(newGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);

        // Exactly one LCAtom entry has been written; the pre-existing foreign entries are intact.
        var loggedAfterFirstApply = ProjectAppliedLog.ReadAll(_cache);
        var firstEntry = Assert.Single(loggedAfterFirstApply);
        Assert.Equal(changeSet.ChangeSetId.ToGuid(), firstEntry.ChangeSetId);
        Assert.Equal(applierIdentity, firstEntry.User);
        Assert.Equal(description, firstEntry.Description);
        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);

        // --- 2. Persist, then re-open the project from disk: the actual Stage D proof. ---
        _loader.Save(_cache);
        _cache.Dispose();
        _cache = _loader.LoadCache(_fwDataPath);

        var wsHandleAfterReopen = _cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepoAfterReopen = _cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        var reopenedSense = senseRepoAfterReopen.GetObject(senseGuid);

        // (a) The gloss really is the new value after a from-disk reload.
        Assert.Equal(newGloss, reopenedSense.Gloss.get_String(wsHandleAfterReopen).Text);

        // (b) Exactly one LCAtom applied-log entry exists, with the right changeSetId GUID, intent
        // digest, and user.
        var loggedAfterReopen = ProjectAppliedLog.ReadAll(_cache);
        var reopenedEntry = Assert.Single(loggedAfterReopen);
        Assert.Equal(changeSet.ChangeSetId.ToGuid(), reopenedEntry.ChangeSetId);
        Assert.Equal(receipt.AppliedLogEntry.IntentDigest, reopenedEntry.IntentDigest);
        Assert.Equal(applierIdentity, reopenedEntry.User);
        Assert.Equal(description, reopenedEntry.Description);

        // (c) No other CmResource entries were disturbed.
        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);
        Assert.Equal(
            foreignEntriesBefore.Count + 1,
            _cache.LangProject.LexDbOA.ResourcesOC.Count);

        // --- 3. Idempotence: re-apply the SAME change set against the reopened cache. ---
        var reapplyReceipt = ChangeSetApplier.Apply(_cache, changeSet, applierIdentity, description);

        Assert.True(reapplyReceipt.AlreadyApplied);
        Assert.Empty(reapplyReceipt.ActualEffects);
        Assert.Equal(newGloss, senseRepoAfterReopen.GetObject(senseGuid).Gloss.get_String(wsHandleAfterReopen).Text);

        var loggedAfterReapply = ProjectAppliedLog.ReadAll(_cache);
        var onlyEntryAfterReapply = Assert.Single(loggedAfterReapply);
        Assert.Equal(reopenedEntry.TimestampUtc, onlyEntryAfterReapply.TimestampUtc); // untouched, not rewritten

        // --- 4. A distinct Change Set adds a second, distinct log entry (union-friendly). ---
        var secondGloss = newGloss + " v2";
        var secondChangeSet = BuildSetGlossChangeSet(canonicalId, wsTag, secondGloss);
        const string secondDescription = "Stage D test: second, distinct change set";

        var secondReceipt = ChangeSetApplier.Apply(_cache, secondChangeSet, applierIdentity, secondDescription);

        Assert.False(secondReceipt.AlreadyApplied);
        Assert.Equal(secondGloss, senseRepoAfterReopen.GetObject(senseGuid).Gloss.get_String(wsHandleAfterReopen).Text);

        var loggedAfterSecondApply = ProjectAppliedLog.ReadAll(_cache);
        Assert.Equal(2, loggedAfterSecondApply.Count);
        Assert.Contains(loggedAfterSecondApply, e => e.ChangeSetId == changeSet.ChangeSetId.ToGuid());
        Assert.Contains(loggedAfterSecondApply, e => e.ChangeSetId == secondChangeSet.ChangeSetId.ToGuid());
        Assert.NotEqual(
            loggedAfterSecondApply.Single(e => e.ChangeSetId == changeSet.ChangeSetId.ToGuid()).IntentDigest,
            loggedAfterSecondApply.Single(e => e.ChangeSetId == secondChangeSet.ChangeSetId.ToGuid()).IntentDigest);

        AssertForeignResourcesUntouched(_cache, foreignEntriesBefore);
        Assert.Equal(
            foreignEntriesBefore.Count + 2,
            _cache.LangProject.LexDbOA.ResourcesOC.Count);
    }

    [Fact]
    public void Apply_UnknownTarget_ThrowsAndRollsBack_AndWritesNoAppliedLogEntry()
    {
        var bogusTarget = CanonicalId.FromGuid(Guid.NewGuid());
        var changeSet = BuildSetGlossChangeSet(bogusTarget, "en", "does not matter");

        Assert.ThrowsAny<Exception>(() => ChangeSetApplier.Apply(_cache, changeSet, "lcatom-tests"));

        // The failed apply must leave no applied-log entry at all (docs/applied-log.md, "Atomicity").
        Assert.Empty(ProjectAppliedLog.ReadAll(_cache));
    }

    /// <summary>
    /// Enumerates senses via the real <see cref="ILexSenseRepository"/> and picks the first one
    /// with a non-empty gloss alternative, reading the current gloss text straight back from
    /// LibLCM (never hardcoded).
    /// </summary>
    private static (Guid SenseGuid, string WsTag, string Gloss) FindSenseWithKnownGloss(LcmCache cache)
    {
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();

        foreach (var sense in senseRepo.AllInstances())
        {
            foreach (var wsHandle in sense.Gloss.AvailableWritingSystemIds)
            {
                var text = sense.Gloss.get_String(wsHandle).Text;
                if (!string.IsNullOrEmpty(text))
                {
                    var wsTag = cache.WritingSystemFactory.GetStrFromWs(wsHandle);
                    return (sense.Guid, wsTag, text);
                }
            }
        }

        throw new InvalidOperationException(
            "Expected the real TestLangProj fixture to contain at least one LexSense with a non-empty gloss.");
    }

    private static ChangeSetEnvelope BuildSetGlossChangeSet(CanonicalId target, string wsTag, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = wsTag, text });
        using var afterDocument = JsonDocument.Parse(afterJson);

        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: target,
            after: afterDocument.RootElement.Clone());

        return new ChangeSetEnvelope(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            changeSetId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }

    /// <summary>Snapshots every current (necessarily foreign, before any Apply) resource's Version.</summary>
    private static List<Guid> ReadForeignResourceVersions(LcmCache cache) =>
        cache.LangProject.LexDbOA.ResourcesOC.Select(r => r.Version).ToList();

    private static void AssertForeignResourcesUntouched(LcmCache cache, List<Guid> expectedForeignVersions)
    {
        var currentForeign = cache.LangProject.LexDbOA.ResourcesOC
            .Where(r => r.Name is null || !r.Name.StartsWith("LCAtom|", StringComparison.Ordinal))
            .Select(r => r.Version)
            .ToList();

        Assert.Equal(expectedForeignVersions.OrderBy(v => v), currentForeign.OrderBy(v => v));
    }
}
