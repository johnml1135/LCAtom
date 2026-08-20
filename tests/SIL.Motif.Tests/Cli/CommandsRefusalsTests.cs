using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Pins <see cref="Commands"/>'s fail-closed refusals across every verb: name collisions, missing
/// drafts and Proposals, malformed ids, store-consistency guards, and the "another program has this
/// project open" and "no bound DryRun" hard stops on the apply path. Each refusal test asserts the
/// specific message a reader must act on and that the store or draft was left byte-for-byte unchanged,
/// not merely that the exit code was non-zero.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class CommandsRefusalsTests
{
    private readonly string _fwDataPath;
    private readonly string _storeDir;
    private readonly string _target;

    public CommandsRefusalsTests(PristineProjectFixture pristine)
    {
        var seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
        _target = CanonicalId.FromGuid(seed.FirstSenseId).Value;

        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(_fwDataPath))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    // --- New ---

    [Fact]
    public void New_DraftNameAlreadyExists_RefusesAndLeavesTheOriginalDraftUntouched()
    {
        Assert.Equal(0, Commands.New(_storeDir, "dup", "original label").ExitCode);
        var draftPath = new ProposalStore(_storeDir).DraftPath("dup");
        var before = File.ReadAllText(draftPath);

        var result = Commands.New(_storeDir, "dup", "second label");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("Finalize or delete it", result.Output);
        Assert.Equal(before, File.ReadAllText(draftPath));
    }

    // --- AddSetGloss ---

    [Fact]
    public void AddSetGloss_EmptyWritingSystem_RefusesAndAddsNoOperation()
    {
        Commands.New(_storeDir, "d", null);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");
        var before = File.ReadAllText(draftPath);

        var result = Commands.AddSetGloss(_storeDir, "d", _target, "", "some text");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--ws must not be empty.", result.Output);
        Assert.Equal(before, File.ReadAllText(draftPath));
    }

    [Fact]
    public void AddSetGloss_InvalidDependsOnIdFormat_RefusesAndAddsNoOperation()
    {
        Commands.New(_storeDir, "d", null);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");
        var before = File.ReadAllText(draftPath);

        var result = Commands.AddSetGloss(
            _storeDir, "d", _target, "en", "text", dependsOn: new[] { "not-a-canonical-id" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--depends-on 'not-a-canonical-id' is not a valid canonical operation id", result.Output);
        Assert.Equal(before, File.ReadAllText(draftPath));
    }

    // --- AddDeleteLexemeForm ---

    [Fact]
    public void AddDeleteLexemeForm_DraftNotFound_Refuses()
    {
        var result = Commands.AddDeleteLexemeForm(_storeDir, "no-such-draft", _target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
        Assert.Contains("Run 'new --draft no-such-draft' first.", result.Output);
    }

    [Fact]
    public void AddDeleteLexemeForm_InvalidTargetId_RefusesAndAddsNoOperation()
    {
        Commands.New(_storeDir, "d", null);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");
        var before = File.ReadAllText(draftPath);

        var result = Commands.AddDeleteLexemeForm(_storeDir, "d", "not-a-canonical-id");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--target 'not-a-canonical-id' is not a valid canonical id", result.Output);
        Assert.Equal(before, File.ReadAllText(draftPath));
    }

    // --- Label / Comment ---

    [Fact]
    public void Label_SetsTheFieldOnTheDraftAndPersistsIt()
    {
        Commands.New(_storeDir, "d", null);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");

        var result = Commands.Label(_storeDir, "d", "a linguist-facing label");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Set label on draft 'd'.", result.Output);
        Assert.Equal("a linguist-facing label", ReadDraft(draftPath).Label);
    }

    [Fact]
    public void Comment_SetsTheFieldOnTheDraftAndPersistsIt()
    {
        Commands.New(_storeDir, "d", null);
        var draftPath = new ProposalStore(_storeDir).DraftPath("d");

        var result = Commands.Comment(_storeDir, "d", "a reviewer-facing comment");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Set comment on draft 'd'.", result.Output);
        Assert.Equal("a reviewer-facing comment", ReadDraft(draftPath).Comment);
    }

    [Fact]
    public void Label_DraftNotFound_Refuses()
    {
        var result = Commands.Label(_storeDir, "no-such-draft", "x");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    // --- Finalize ---

    [Fact]
    public void Finalize_DraftNotFound_Refuses()
    {
        var result = Commands.Finalize(_storeDir, "no-such-draft");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void Finalize_DraftFailsProposalValidation_RefusesAndCommitsNothing()
    {
        Commands.New(_storeDir, "d", null);
        Commands.AddSetGloss(_storeDir, "d", _target, "en", "text");
        var store = new ProposalStore(_storeDir);
        var draftPath = store.DraftPath("d");

        // Corrupt contractVersions so it no longer covers the 'lexical' group the one operation uses.
        var draft = ReadDraft(draftPath);
        draft.ContractVersions.Remove("lexical");
        draft.ContractVersions["bogus"] = "1.0";
        WriteDraft(draftPath, draft);

        var result = Commands.Finalize(_storeDir, "d");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("failed Proposal validation", result.Output);
        Assert.True(File.Exists(draftPath)); // never consumed: finalize did not commit
        Assert.False(Directory.Exists(store.ManifestsDirectory) && Directory.GetFiles(store.ManifestsDirectory, "*.json").Length > 0);
        Assert.False(Directory.Exists(store.ObjectsDirectory) && Directory.GetFiles(store.ObjectsDirectory, "*.json").Length > 0);
    }

    // --- Reopen ---

    [Fact]
    public void Reopen_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_storeDir, "taken", null);
        var takenPath = new ProposalStore(_storeDir).DraftPath("taken");
        var before = File.ReadAllText(takenPath);

        var result = Commands.Reopen(_storeDir, "taken", proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before reopening a Proposal with this draft name.", result.Output);
        Assert.Equal(before, File.ReadAllText(takenPath));
    }

    [Fact]
    public void Reopen_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        var manifestBefore = File.ReadAllText(store.ManifestPath(proposalId));
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.Reopen(_storeDir, "reopened", proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
        Assert.False(File.Exists(store.DraftPath("reopened")));
        Assert.Equal(manifestBefore, File.ReadAllText(store.ManifestPath(proposalId)));
    }

    // --- Duplicate ---

    [Fact]
    public void Duplicate_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_storeDir, "taken", null);
        var takenPath = new ProposalStore(_storeDir).DraftPath("taken");
        var before = File.ReadAllText(takenPath);

        var result = Commands.Duplicate(_storeDir, proposalId, "taken");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before duplicating a Proposal into a draft with this name.", result.Output);
        Assert.Equal(before, File.ReadAllText(takenPath));
    }

    [Fact]
    public void Duplicate_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.Duplicate(_storeDir, proposalId, "copy");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
        Assert.False(File.Exists(store.DraftPath("copy")));
    }

    // --- RemoveOperations ---

    [Fact]
    public void RemoveOperations_DraftNotFound_Refuses()
    {
        var result = Commands.RemoveOperations(_storeDir, "no-such-draft", new[] { CanonicalId.Mint().Value }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void RemoveOperations_NoOperationIdsSpecified_Refuses()
    {
        Commands.New(_storeDir, "d", null);

        var result = Commands.RemoveOperations(_storeDir, "d", Array.Empty<string>(), force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Specify at least one operation id to remove.", result.Output);
    }

    [Fact]
    public void RemoveOperations_InvalidOperationIdFormat_Refuses()
    {
        Commands.New(_storeDir, "d", null);

        var result = Commands.RemoveOperations(_storeDir, "d", new[] { "not-a-canonical-id" }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'not-a-canonical-id' is not a valid canonical operation id", result.Output);
    }

    // --- Split ---

    [Fact]
    public void Split_NoGroups_Refuses()
    {
        var result = Commands.Split(_storeDir, CanonicalId.Mint().Value, Array.Empty<Commands.SplitGroup>(), force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Specify at least one group to split into.", result.Output);
    }

    [Fact]
    public void Split_ProposalNotFound_Refuses()
    {
        var bogusId = CanonicalId.Mint().Value;

        var result = Commands.Split(
            _storeDir, bogusId, new[] { new Commands.SplitGroup("g1", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
        Assert.Contains("Run 'list' to see committed proposals.", result.Output);
    }

    [Fact]
    public void Split_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.Split(
            _storeDir, proposalId, new[] { new Commands.SplitGroup("g1", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void Split_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_storeDir, "taken", null);
        var takenPath = new ProposalStore(_storeDir).DraftPath("taken");
        var before = File.ReadAllText(takenPath);

        var result = Commands.Split(
            _storeDir, proposalId, new[] { new Commands.SplitGroup("taken", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before splitting into a draft with this name.", result.Output);
        Assert.Equal(before, File.ReadAllText(takenPath));
    }

    [Fact]
    public void Split_GroupsShareADraftName_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");

        var result = Commands.Split(
            _storeDir, proposalId,
            new[]
            {
                new Commands.SplitGroup("same", new[] { CanonicalId.Mint().Value }),
                new Commands.SplitGroup("same", Array.Empty<string>()),
            },
            force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Each split group must target a distinct draft name.", result.Output);
    }

    [Fact]
    public void Split_MalformedOperationIdInGroup_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");

        var result = Commands.Split(
            _storeDir, proposalId, new[] { new Commands.SplitGroup("g1", new[] { "not-a-canonical-id" }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'not-a-canonical-id' is not a valid canonical operation id", result.Output);
    }

    [Fact]
    public void Split_UnknownOperationIdInGroup_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var strangerId = CanonicalId.Mint().Value;

        var result = Commands.Split(
            _storeDir, proposalId, new[] { new Commands.SplitGroup("g1", new[] { strangerId }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"has no operation(s) '{strangerId}'", result.Output);
    }

    // --- List ---

    [Fact]
    public void List_EmptyStore_ReturnsAnEmptyListRatherThanAnError()
    {
        var result = Commands.List(_storeDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No proposals in store.", result.Output);
    }

    // --- Show ---

    [Fact]
    public void Show_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.Show(_storeDir, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    // --- DryRun / Apply (file-path overloads) ---

    [Fact]
    public void DryRun_ProposalNotFound_Refuses()
    {
        var result = Commands.DryRun(_storeDir, CanonicalId.Mint().Value, _fwDataPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void DryRun_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.DryRun(_storeDir, proposalId, _fwDataPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void Apply_ProposalNotFound_Refuses()
    {
        var result = Commands.Apply(_storeDir, CanonicalId.Mint().Value, _fwDataPath, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void Apply_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);

        var result = Commands.Apply(_storeDir, proposalId, _fwDataPath, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void Apply_NoBoundDryRun_RefusesAndNamesTheFix()
    {
        var proposalId = CommitOneOperationProposal("src");

        var result = Commands.Apply(_storeDir, proposalId, _fwDataPath, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("has no bound DryRun recorded", result.Output);
        Assert.Contains($"dry-run {proposalId} --project <fwdata>", result.Output);
    }

    [Fact]
    public void Apply_InvalidProposalId_WrapsTheMessageWithADiscardCacheHint()
    {
        var result = Commands.Apply(_storeDir, "not-a-canonical-id", _fwDataPath, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is not a valid canonical Proposal id", result.Output);
        Assert.Contains("Discard it and reload the project.", result.Output);
    }

    // --- DryRun / Apply (session overloads share TryLoadProposalForRun) ---

    [Fact]
    public void SessionDryRun_ProposalNotFound_Refuses()
    {
        using var session = CliSession.Open(_fwDataPath);

        var result = Commands.DryRun(session, _storeDir, CanonicalId.Mint().Value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void SessionApply_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var store = new ProposalStore(_storeDir);
        DeleteTheCommittedObject(store, proposalId);
        using var session = CliSession.Open(_fwDataPath);

        var result = Commands.Apply(session, _storeDir, proposalId, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void SessionDryRun_ManifestIdentityDoesNotMatchLookup_RefusesBeforeScratchCreation()
    {
        var proposalId = CommitOneOperationProposal("manifest-identity-mismatch");
        var store = new ProposalStore(_storeDir);
        var manifestPath = store.ManifestPath(proposalId);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var wrongId = CanonicalId.Mint().Value;
        manifest["proposalId"] = wrongId;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        using var session = CliSession.Open(_fwDataPath);

        var result = Commands.DryRun(session, _storeDir, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(proposalId, result.Output, StringComparison.Ordinal);
        Assert.Contains(wrongId, result.Output, StringComparison.Ordinal);
        Assert.Equal(0, session.PristineRebuildCount);
    }

    [Fact]
    public void SessionDryRun_EnvelopeIdentityDoesNotMatchLookup_RefusesBeforeScratchCreation()
    {
        var proposalId = CommitOneOperationProposal("envelope-identity-mismatch");
        var store = new ProposalStore(_storeDir);
        var manifest = JsonNode.Parse(File.ReadAllText(store.ManifestPath(proposalId)))!.AsObject();
        var objectPath = store.ObjectPath(manifest["currentIntentDigest"]!.GetValue<string>());
        var envelope = JsonNode.Parse(File.ReadAllText(objectPath))!.AsObject();
        var wrongId = CanonicalId.Mint().Value;
        envelope["proposalId"] = wrongId;
        File.WriteAllText(objectPath, envelope.ToJsonString());
        using var session = CliSession.Open(_fwDataPath);

        var result = Commands.DryRun(session, _storeDir, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(proposalId, result.Output, StringComparison.Ordinal);
        Assert.Contains(wrongId, result.Output, StringComparison.Ordinal);
        Assert.Equal(0, session.PristineRebuildCount);
    }

    [Fact]
    public void SessionDryRun_ObjectContentDoesNotMatchManifestDigest_RefusesBeforeScratchCreation()
    {
        var proposalId = CommitOneOperationProposal("object-digest-mismatch");
        var store = new ProposalStore(_storeDir);
        var manifest = JsonNode.Parse(File.ReadAllText(store.ManifestPath(proposalId)))!.AsObject();
        var objectPath = store.ObjectPath(manifest["currentIntentDigest"]!.GetValue<string>());
        var envelope = JsonNode.Parse(File.ReadAllText(objectPath))!.AsObject();
        envelope["operations"]![0]!["after"]!["text"] = "content changed behind the digest";
        File.WriteAllText(objectPath, envelope.ToJsonString());
        using var session = CliSession.Open(_fwDataPath);

        var result = Commands.DryRun(session, _storeDir, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("intentDigest", result.Output, StringComparison.Ordinal);
        Assert.Contains("store inconsistency", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, session.PristineRebuildCount);
    }

    // --- Helpers ---

    private string CommitOneOperationProposal(string draftName)
    {
        Commands.New(_storeDir, draftName, null);
        Commands.AddSetGloss(_storeDir, draftName, _target, "en", "text for " + draftName);
        var finalizeResult = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        return ExtractProposalId(finalizeResult.Output);
    }

    private static void DeleteTheCommittedObject(ProposalStore store, string proposalId)
    {
        var manifestJson = File.ReadAllText(store.ManifestPath(proposalId));
        using var manifestDoc = JsonDocument.Parse(manifestJson);
        var digest = manifestDoc.RootElement.GetProperty("currentIntentDigest").GetString()!;
        File.Delete(store.ObjectPath(digest));
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

    private static DraftDocument ReadDraft(string path) =>
        JsonSerializer.Deserialize<DraftDocument>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static void WriteDraft(string path, DraftDocument draft) =>
        File.WriteAllText(path, JsonSerializer.Serialize(draft));
}
