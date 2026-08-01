using System;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Defect 1 proof: the store keys objects by <c>intentDigest</c> (write-once, never revisited) and
/// manifests by the frozen <c>proposalId</c> (a movable <c>currentIntentDigest</c> pointer), exactly
/// git's object/ref split — so <c>reopen</c> + re-<c>finalize</c> (an amend) can move that pointer to
/// a new object without ever mutating the previous one. See
/// docs/stage2-change-management.md, S1/S3, and docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
/// decision 2.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ReopenAmendTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public ReopenAmendTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Reopen", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _storeDir = Path.Combine(_tempRoot, ".motif-store");
    }

    public void Dispose()
    {
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
    public void Commit_Reopen_Amend_KeepsId_MovesDigest_RetainsBothObjectVersions_ResetsStatusToProposed()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var canonicalId = CanonicalId.FromGuid(senseGuid);

        // --- commit v1 ---
        Assert.Equal(0, Commands.New(_storeDir, "v1", "first label").ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(_storeDir, "v1", canonicalId.Value, wsTag, originalGloss + " v1").ExitCode);
        var firstFinalize = Commands.Finalize(_storeDir, "v1");
        Assert.Equal(0, firstFinalize.ExitCode);
        Assert.Contains("Finalized draft", firstFinalize.Output);

        var proposalId = ExtractProposalId(firstFinalize.Output);
        var firstDigest = ExtractIntentDigest(firstFinalize.Output);

        var store = new ProposalStore(_storeDir);
        var manifestPath = store.ManifestPath(proposalId);
        Assert.Contains("\"status\": \"proposed\"", File.ReadAllText(manifestPath));
        Assert.True(File.Exists(store.ObjectPath(firstDigest)));

        // Simulate this Proposal having since been applied (a real prior status), so amend's
        // "status reset to proposed" is a real transition, not a no-op.
        var manifestBeforeAmend = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifestBeforeAmend.Replace("\"proposed\"", "\"applied\""));
        Assert.Contains("\"status\": \"applied\"", File.ReadAllText(manifestPath));

        // --- reopen: loads v1's content into a NEW draft carrying the SAME frozen proposalId ---
        var reopenResult = Commands.Reopen(_storeDir, "v2", proposalId);
        Assert.Equal(0, reopenResult.ExitCode);
        Assert.Contains(proposalId, reopenResult.Output);
        var draftPath = Path.Combine(_storeDir, "drafts", "v2.json");
        Assert.True(File.Exists(draftPath));

        // Amend the content: add a second operation so the intent digest necessarily moves.
        Assert.Equal(
            0,
            Commands.AddSetGloss(_storeDir, "v2", canonicalId.Value, wsTag, originalGloss + " v2 (amended)").ExitCode);

        // --- finalize the reopened draft: an amend, not a fresh commit ---
        var amendFinalize = Commands.Finalize(_storeDir, "v2");
        Assert.Equal(0, amendFinalize.ExitCode);
        Assert.Contains("Amended draft", amendFinalize.Output);
        Assert.False(File.Exists(draftPath)); // draft consumed, same as a normal finalize

        var amendedProposalId = ExtractProposalId(amendFinalize.Output);
        var secondDigest = ExtractIntentDigest(amendFinalize.Output);

        // (1) The id is unchanged.
        Assert.Equal(proposalId, amendedProposalId);

        // (2) The digest changed (different content: two operations, not one).
        Assert.NotEqual(firstDigest, secondDigest);

        // (3) BOTH object versions exist on disk — the amend never touched, deleted, or revisited
        // the original write-once object.
        var firstObjectPath = store.ObjectPath(firstDigest);
        var secondObjectPath = store.ObjectPath(secondDigest);
        Assert.True(File.Exists(firstObjectPath));
        Assert.True(File.Exists(secondObjectPath));
        Assert.NotEqual(firstObjectPath, secondObjectPath);
        Assert.Contains(originalGloss + " v1", File.ReadAllText(firstObjectPath));
        Assert.Contains(originalGloss + " v2 (amended)", File.ReadAllText(secondObjectPath));

        // (4) Status reset to proposed (approval/anchors are effect-digest-scoped; any content
        // change invalidates them).
        var manifestAfterAmend = File.ReadAllText(manifestPath);
        Assert.Contains("\"status\": \"proposed\"", manifestAfterAmend);
        Assert.Contains(secondDigest, manifestAfterAmend);
        Assert.DoesNotContain("\"status\": \"applied\"", manifestAfterAmend);

        // Only one manifest file exists for this id throughout (the manifest is the movable
        // pointer; it was never re-created, only updated in place).
        Assert.True(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(store.ManifestsDirectory, "*.json"));

        // Exactly two objects exist in the store (one per committed version).
        Assert.Equal(2, Directory.GetFiles(store.ObjectsDirectory, "*.json").Length);
    }

    [Fact]
    public void Reopen_UnknownProposalId_Fails()
    {
        var bogusId = CanonicalId.Mint().Value;
        var result = Commands.Reopen(_storeDir, "some-draft", bogusId);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private (Guid SenseGuid, string WsTag, string Gloss) FindSenseWithKnownGloss()
    {
        using var cache = new FwDataProjectLoader().LoadCache(_fwDataPath);
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

    private static string ExtractProposalId(string output)
    {
        const string marker = "-> Proposal ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from output: {output}");
        return output.Substring(start, end - start);
    }

    private static string ExtractIntentDigest(string output)
    {
        const string marker = "intentDigest: ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOfAny(new[] { '\r', '\n' }, start);
        Assert.True(end > start, $"Could not parse intentDigest from output: {output}");
        return output.Substring(start, end - start).Trim();
    }
}
