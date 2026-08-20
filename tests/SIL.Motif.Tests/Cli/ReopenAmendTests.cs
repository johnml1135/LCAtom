using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Projection.Store;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The store keys objects by <c>intentDigest</c> (write-once, never revisited) and
/// manifests by the frozen <c>proposalId</c> (a movable <c>currentIntentDigest</c> pointer), exactly
/// git's object/ref split — so <c>reopen</c> + re-<c>finalize</c> (an amend) can move that pointer to
/// a new object without ever mutating the previous one
/// (ADR 0004, decision 2).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ReopenAmendTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public ReopenAmendTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;

        // The scratch root is the project folder's parent -- a sibling location for the CLI's own store.
        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(_fwDataPath))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    [Fact]
    public void Commit_Reopen_Amend_KeepsId_MovesDigest_RetainsBothObjectVersions_ResetsStatusToProposed()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = CanonicalId.FromGuid(senseGuid);

        // --- commit v1 ---
        Assert.Equal(0, Commands.New(_storeDir, "v1", "first label").ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(_storeDir, "v1", canonicalId.Value, wsTag, originalGloss + " v1").ExitCode);
        DraftRationale.Author(
            _storeDir, "v1", "Clarify the first sense gloss", "Replace the ambiguous gloss with the intended analysis.");
        var firstFinalize = Commands.Finalize(_storeDir, "v1");
        Assert.Equal(0, firstFinalize.ExitCode);
        Assert.Contains("Finalized draft", firstFinalize.Output);

        var proposalId = ExtractProposalId(firstFinalize.Output);
        var firstDigest = ExtractIntentDigest(firstFinalize.Output);

        var store = new ProposalStore(_storeDir);
        var manifestPath = store.ManifestPath(proposalId);
        Assert.Contains("\"status\": \"proposed\"", File.ReadAllText(manifestPath));
        Assert.True(File.Exists(store.ObjectPath(firstDigest)));

        // Simulate a real prior "applied" status, so amend's reset to "proposed" is a real transition.
        var manifestBeforeAmend = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, manifestBeforeAmend.Replace("\"proposed\"", "\"applied\""));
        Assert.Contains("\"status\": \"applied\"", File.ReadAllText(manifestPath));

        // --- reopen: loads v1's content into a NEW draft carrying the SAME frozen proposalId ---
        var reopenResult = Commands.Reopen(_storeDir, "v2", proposalId);
        Assert.Equal(0, reopenResult.ExitCode);
        Assert.Contains(proposalId, reopenResult.Output);
        var draftPath = Path.Combine(_storeDir, "drafts", "v2.json");
        Assert.True(File.Exists(draftPath));
        var reopenedDraft = ReadDraft(draftPath);
        Assert.Equal("Clarify the first sense gloss", reopenedDraft.Label);
        Assert.Equal("Replace the ambiguous gloss with the intended analysis.", reopenedDraft.Comment);

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

        // (3) BOTH object versions exist on disk — the amend never touched the original write-once object.
        var firstObjectPath = store.ObjectPath(firstDigest);
        var secondObjectPath = store.ObjectPath(secondDigest);
        Assert.True(File.Exists(firstObjectPath));
        Assert.True(File.Exists(secondObjectPath));
        Assert.NotEqual(firstObjectPath, secondObjectPath);
        Assert.Contains(originalGloss + " v1", File.ReadAllText(firstObjectPath));
        Assert.Contains(originalGloss + " v2 (amended)", File.ReadAllText(secondObjectPath));

        // (4) Status reset to proposed: approval/anchors are effect-digest-scoped, so content changes invalidate them.
        var manifestAfterAmend = File.ReadAllText(manifestPath);
        Assert.Contains("\"status\": \"proposed\"", manifestAfterAmend);
        Assert.Contains(secondDigest, manifestAfterAmend);
        Assert.Contains("Clarify the first sense gloss", manifestAfterAmend);
        Assert.Contains("Replace the ambiguous gloss with the intended analysis.", manifestAfterAmend);
        Assert.DoesNotContain("\"status\": \"applied\"", manifestAfterAmend);

        // Only one manifest file exists for this id: it's the movable pointer, updated in place, never re-created.
        Assert.True(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(store.ManifestsDirectory, "*.json"));

        // Exactly two objects exist in the store (one per committed version).
        Assert.Equal(2, Directory.GetFiles(store.ObjectsDirectory, "*.json").Length);
    }

    [Fact]
    public void RationaleOnlyAmend_PersistsBothFields_WithoutMovingTheIntentDigest()
    {
        const string initialLabel = "Clarify the first sense gloss";
        const string initialComment = "Replace the ambiguous gloss with the intended analysis.";
        const string revisedLabel = "Explain the first sense analysis";
        const string revisedComment = "Give reviewers the linguistic reason this gloss is the intended one.";
        var target = CanonicalId.FromGuid(_seed.FirstSenseId).Value;

        Assert.Equal(0, Commands.New(_storeDir, "original", null).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "original", target, "en", "a clarified gloss").ExitCode);
        DraftRationale.Author(_storeDir, "original", initialLabel, initialComment);
        var firstFinalize = Commands.Finalize(_storeDir, "original");
        Assert.Equal(0, firstFinalize.ExitCode);
        var proposalId = ExtractProposalId(firstFinalize.Output);
        var firstDigest = ExtractIntentDigest(firstFinalize.Output);

        Assert.Equal(0, Commands.Reopen(_storeDir, "rationale-amend", proposalId).ExitCode);
        var reopened = ReadDraft(Path.Combine(_storeDir, "drafts", "rationale-amend.json"));
        Assert.Equal(initialLabel, reopened.Label);
        Assert.Equal(initialComment, reopened.Comment);
        DraftRationale.Author(_storeDir, "rationale-amend", revisedLabel, revisedComment);

        var amend = Commands.Finalize(_storeDir, "rationale-amend");

        Assert.Equal(0, amend.ExitCode);
        Assert.Equal(firstDigest, ExtractIntentDigest(amend.Output));
        var manifest = ReadManifest(proposalId);
        Assert.Equal(firstDigest, manifest.CurrentIntentDigest);
        Assert.Equal(revisedLabel, manifest.Label);
        Assert.Equal(revisedComment, manifest.Comment);
        Assert.Single(Directory.GetFiles(new ProposalStore(_storeDir).ObjectsDirectory, "*.json"));

        var showText = Commands.Show(_storeDir, proposalId);
        var showJson = Commands.ShowJson(_storeDir, proposalId);
        Assert.Equal(0, showText.ExitCode);
        Assert.Equal(0, showJson.ExitCode);
        Assert.Contains(revisedLabel, showText.Output);
        Assert.Contains(revisedComment, showText.Output);
        Assert.Contains(revisedLabel, showJson.Output);
        Assert.Contains(revisedComment, showJson.Output);
    }

    [Fact]
    public void Show_OlderManifestWithoutRationale_RemainsReadable()
    {
        var target = CanonicalId.FromGuid(_seed.FirstSenseId).Value;
        Assert.Equal(0, Commands.New(_storeDir, "legacy", null).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "legacy", target, "en", "a legacy gloss").ExitCode);
        DraftRationale.Author(
            _storeDir, "legacy", "Clarify a legacy gloss", "Create a manifest that can model an older stored record.");
        var finalized = Commands.Finalize(_storeDir, "legacy");
        var proposalId = ExtractProposalId(finalized.Output);
        var manifestPath = new ProposalStore(_storeDir).ManifestPath(proposalId);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest.Remove("label");
        manifest.Remove("comment");
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Assert.Equal(0, Commands.Show(_storeDir, proposalId).ExitCode);
        Assert.Equal(0, Commands.ShowJson(_storeDir, proposalId).ExitCode);
    }

    [Fact]
    public void Reopen_UnknownProposalId_Fails()
    {
        var bogusId = CanonicalId.Mint().Value;
        var result = Commands.Reopen(_storeDir, "some-draft", bogusId);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
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

    private static readonly JsonSerializerOptions StoreJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static DraftDocument ReadDraft(string path) =>
        JsonSerializer.Deserialize<DraftDocument>(File.ReadAllText(path), StoreJsonOptions)!;

    private ManifestDocument ReadManifest(string proposalId) =>
        JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(new ProposalStore(_storeDir).ManifestPath(proposalId)), StoreJsonOptions)!;
}
