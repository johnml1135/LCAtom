using System.Text.Json;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Host.LcmUtils;
using SIL.LCAtom.Model.Snapshot;
using SIL.LCAtom.Runner.Assessment;
using SIL.LCAtom.Runner.Operations;
using SIL.LCAtom.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.LCAtom.Tests.Runner;

/// <summary>
/// Stage C proof, on a real project: <see cref="ChangeSetAssessor.Assess"/> reads back real LibLCM
/// state (not the intent) to compute expected effects for a <c>lexical/sense/setGloss</c> change
/// set, is deterministic, and never leaves a mutation committed. See
/// docs/change-set-contract.md, "Assessment" / "Expected effects", and
/// docs/adr/0006-engine-reality-apply-readback-preflight.md.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ChangeSetAssessorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LcmCache _cache;

    public ChangeSetAssessorTests()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.LCAtom.Tests", Guid.NewGuid().ToString("N"));
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
    public void Assess_SetGloss_ReportsRealBeforeAndAfterGloss_IsDeterministic_AndDoesNotMutateProject()
    {
        var (sense, wsHandle, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var newGloss = originalGloss + " (revised sense)";
        var canonicalId = CanonicalId.FromGuid(sense.Guid);

        var changeSet = BuildSetGlossChangeSet(canonicalId, wsTag, newGloss);

        var firstAssessment = ChangeSetAssessor.Assess(_cache, changeSet);
        var secondAssessment = ChangeSetAssessor.Assess(_cache, changeSet);

        // (a) + "an effect is produced for the right canonical id/field": exactly one expected
        // effect, keyed by the sense's own canonical id and the gloss field, whose before/after
        // are the real read-back values, not an echo of the authored intent.
        var effect = Assert.Single(firstAssessment.ExpectedEffects);
        Assert.Equal(canonicalId, effect.CanonicalId);
        Assert.Equal(SnapshotFields.LexSenseGloss, effect.Field);
        Assert.Equal(originalGloss, effect.Before[wsTag]);
        Assert.Equal(newGloss, effect.After[wsTag]);

        // (b) the effect digest is stable across two identical assessments.
        Assert.StartsWith("sha256:", firstAssessment.EffectDigest);
        Assert.Equal(firstAssessment.EffectDigest, secondAssessment.EffectDigest);
        Assert.Equal(firstAssessment.IntentDigest, secondAssessment.IntentDigest);

        // (c) the project's actual gloss is UNCHANGED after assess: non-mutating rollback proven
        // by reading the live object again, not by trusting the returned Assessment.
        Assert.Equal(originalGloss, sense.Gloss.get_String(wsHandle).Text);
    }

    [Fact]
    public void Assess_UnknownTarget_Throws()
    {
        var bogusTarget = CanonicalId.FromGuid(Guid.NewGuid());
        var changeSet = BuildSetGlossChangeSet(bogusTarget, "en", "does not matter");

        Assert.ThrowsAny<Exception>(() => ChangeSetAssessor.Assess(_cache, changeSet));
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
}
