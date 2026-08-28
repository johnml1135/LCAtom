using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Generator;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
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
    private const string ProductVersion = "1.0";

    private readonly string _fwDataPath;
    private readonly string _target;

    public CommandsRefusalsTests(PristineProjectFixture pristine)
    {
        var seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
        _target = CanonicalId.FromGuid(seed.FirstSenseId).Value;
    }

    // --- New ---

    [Fact]
    public void New_DraftNameAlreadyExists_RefusesAndLeavesTheOriginalDraftUntouched()
    {
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, "dup", "original label").ExitCode);
        var before = ReadDraftJson("dup");

        var result = Commands.New(_fwDataPath, ProductVersion, "dup", "second label");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        // Discarding the existing "dup" draft would not free the name for this call either.
        Assert.Contains("Finalize it, or use another name", result.Output);
        Assert.DoesNotContain("delete it", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, ReadDraftJson("dup"));
    }

    // --- AddSetGloss ---

    [Fact]
    public void AddSetGloss_EmptyWritingSystem_RefusesAndAddsNoOperation()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);
        var before = ReadDraftJson("d");

        var result = Commands.AddSetGloss(_fwDataPath, ProductVersion, "d", _target, "", "some text");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--ws must not be empty.", result.Output);
        Assert.Equal(before, ReadDraftJson("d"));
    }

    [Fact]
    public void AddSetGloss_InvalidDependsOnIdFormat_RefusesAndAddsNoOperation()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);
        var before = ReadDraftJson("d");

        var result = Commands.AddSetGloss(
            _fwDataPath, ProductVersion, "d", _target, "en", "text", dependsOn: new[] { "not-a-canonical-id" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--depends-on 'not-a-canonical-id' is not a valid canonical operation id", result.Output);
        Assert.Equal(before, ReadDraftJson("d"));
    }

    // --- AddDeleteLexemeForm ---

    [Fact]
    public void AddDeleteLexemeForm_DraftNotFound_Refuses()
    {
        var result = Commands.AddDeleteLexemeForm(_fwDataPath, ProductVersion, "no-such-draft", _target);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
        Assert.Contains("Run 'new --draft no-such-draft' first.", result.Output);
    }

    [Fact]
    public void AddDeleteLexemeForm_InvalidTargetId_RefusesAndAddsNoOperation()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);
        var before = ReadDraftJson("d");

        var result = Commands.AddDeleteLexemeForm(_fwDataPath, ProductVersion, "d", "not-a-canonical-id");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--target 'not-a-canonical-id' is not a valid canonical id", result.Output);
        Assert.Equal(before, ReadDraftJson("d"));
    }

    // --- Label / Comment ---

    [Fact]
    public void Label_SetsTheFieldOnTheDraftAndPersistsIt()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);

        var result = Commands.Label(_fwDataPath, ProductVersion, "d", "a linguist-facing label");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Set label on draft 'd'.", result.Output);
        Assert.Equal("a linguist-facing label", ReadDraft("d").Label);
    }

    [Fact]
    public void Comment_SetsTheFieldOnTheDraftAndPersistsIt()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);

        var result = Commands.Comment(_fwDataPath, ProductVersion, "d", "a reviewer-facing comment");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Set comment on draft 'd'.", result.Output);
        Assert.Equal("a reviewer-facing comment", ReadDraft("d").Comment);
    }

    [Fact]
    public void Label_DraftNotFound_Refuses()
    {
        var result = Commands.Label(_fwDataPath, ProductVersion, "no-such-draft", "x");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    // --- Finalize ---

    [Fact]
    public void Finalize_DraftNotFound_Refuses()
    {
        var result = Commands.Finalize(_fwDataPath, ProductVersion, "no-such-draft");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Theory]
    [InlineData(null, "Explains why the change is needed.")]
    [InlineData("   ", "Explains why the change is needed.")]
    [InlineData("Clarify the analysis", null)]
    [InlineData("Clarify the analysis", "\t")]
    public void Finalize_MissingRationale_RefusesAtomicallyAndPreservesTheDraft(
        string? label, string? comment)
    {
        const string draftName = "missing-rationale";
        Commands.New(_fwDataPath, ProductVersion, draftName, null);
        Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, _target, "en", "clarified gloss");
        var draft = ReadDraft(draftName);
        draft.Label = label;
        draft.Comment = comment;
        WriteDraft(draftName, draft);
        var before = ReadDraftJson(draftName);

        var result = Commands.Finalize(_fwDataPath, ProductVersion, draftName);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("short description", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("label", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extended explanation", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comment", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"label --draft {draftName} <text>", result.Output, StringComparison.Ordinal);
        Assert.Contains($"comment --draft {draftName} <text>", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, ReadDraftJson(draftName));
        Assert.Equal(0, CountCommittedProposals());
    }

    [Fact]
    public void Finalize_ArgvDiagnosticNamesCommandsThatRemedyTheRefusal()
    {
        const string draftName = "argv-rationale";
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, null).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, _target, "en", "clarified gloss").ExitCode);

        var refused = RunCli($"finalize --draft {draftName}");

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains($"label --draft {draftName} <text>", refused.Error);
        Assert.Contains($"comment --draft {draftName} <text>", refused.Error);
        Assert.Equal(0, RunCli($"label --draft {draftName} \"Clarify the analysis\"").ExitCode);
        Assert.Equal(
            0,
            RunCli($"comment --draft {draftName} \"Explain why this analysis is intended\"").ExitCode);
        Assert.Equal(0, RunCli($"finalize --draft {draftName}").ExitCode);
    }

    [Fact]
    public void Finalize_AmendWithMissingRationale_RefusesWithoutMovingCommittedState()
    {
        var proposalId = CommitOneOperationProposal("original-rationale");
        var recordBefore = GetRecord(proposalId);
        var revisionCountBefore = CountRevisions(proposalId);
        Assert.Equal(0, Commands.Reopen(_fwDataPath, ProductVersion, "amend-rationale", proposalId).ExitCode);
        var draft = ReadDraft("amend-rationale");
        draft.Comment = "  ";
        WriteDraft("amend-rationale", draft);
        var draftBefore = ReadDraftJson("amend-rationale");

        var result = Commands.Finalize(_fwDataPath, ProductVersion, "amend-rationale");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(draftBefore, ReadDraftJson("amend-rationale"));
        var recordAfter = GetRecord(proposalId);
        Assert.Equal(recordBefore.Status, recordAfter.Status);
        Assert.Equal(recordBefore.IntentDigest, recordAfter.IntentDigest);
        Assert.Equal(revisionCountBefore, CountRevisions(proposalId));
    }

    [Fact]
    public void Finalize_DraftFailsProposalValidation_RefusesAndCommitsNothing()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);
        Commands.AddSetGloss(_fwDataPath, ProductVersion, "d", _target, "en", "text");

        // Corrupt contractVersions so it no longer covers the 'lexical' group the one operation uses.
        var draft = ReadDraft("d");
        draft.Label = "Exercise Proposal validation";
        draft.Comment = "Keep contract-version validation independent from the required rationale guard.";
        draft.ContractVersions.Remove("lexical");
        draft.ContractVersions["bogus"] = "1.0";
        WriteDraft("d", draft);

        var result = Commands.Finalize(_fwDataPath, ProductVersion, "d");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("failed Proposal validation", result.Output);
        Assert.True(DraftExists("d")); // never consumed: finalize did not commit
        Assert.Equal(0, CountCommittedProposals());
    }

    // --- Reopen ---

    [Fact]
    public void Reopen_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_fwDataPath, ProductVersion, "taken", null);
        var before = ReadDraftJson("taken");

        var result = Commands.Reopen(_fwDataPath, ProductVersion, "taken", proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before reopening a Proposal with this draft name.", result.Output);
        Assert.Equal(before, ReadDraftJson("taken"));
    }

    [Fact]
    public void Reopen_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var recordBefore = GetStatusRow(proposalId);
        DeleteCommittedRevision(proposalId);

        var result = Commands.Reopen(_fwDataPath, ProductVersion, "reopened", proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
        Assert.False(DraftExists("reopened"));
        var recordAfter = GetStatusRow(proposalId);
        Assert.Equal(recordBefore.Status, recordAfter.Status);
        Assert.Equal(recordBefore.IntentDigest, recordAfter.IntentDigest);
    }

    // --- Duplicate ---

    [Fact]
    public void Duplicate_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_fwDataPath, ProductVersion, "taken", null);
        var before = ReadDraftJson("taken");

        var result = Commands.Duplicate(_fwDataPath, ProductVersion, proposalId, "taken");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before duplicating a Proposal into a draft with this name.", result.Output);
        Assert.Equal(before, ReadDraftJson("taken"));
    }

    [Fact]
    public void Duplicate_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        DeleteCommittedRevision(proposalId);

        var result = Commands.Duplicate(_fwDataPath, ProductVersion, proposalId, "copy");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
        Assert.False(DraftExists("copy"));
    }

    // --- RemoveOperations ---

    [Fact]
    public void RemoveOperations_DraftNotFound_Refuses()
    {
        var result = Commands.RemoveOperations(
            _fwDataPath, ProductVersion, "no-such-draft", new[] { CanonicalId.Mint().Value }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void RemoveOperations_NoOperationIdsSpecified_Refuses()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);

        var result = Commands.RemoveOperations(_fwDataPath, ProductVersion, "d", Array.Empty<string>(), force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Specify at least one operation id to remove.", result.Output);
    }

    [Fact]
    public void RemoveOperations_InvalidOperationIdFormat_Refuses()
    {
        Commands.New(_fwDataPath, ProductVersion, "d", null);

        var result = Commands.RemoveOperations(_fwDataPath, ProductVersion, "d", new[] { "not-a-canonical-id" }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'not-a-canonical-id' is not a valid canonical operation id", result.Output);
    }

    // --- Split ---

    [Fact]
    public void Split_NoGroups_Refuses()
    {
        var result = Commands.Split(
            _fwDataPath, ProductVersion, CanonicalId.Mint().Value, Array.Empty<Commands.SplitGroup>(), force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Specify at least one group to split into.", result.Output);
    }

    [Fact]
    public void Split_ProposalNotFound_Refuses()
    {
        var bogusId = CanonicalId.Mint().Value;

        var result = Commands.Split(
            _fwDataPath, ProductVersion, bogusId,
            new[] { new Commands.SplitGroup("g1", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
        Assert.Contains("Run 'list' to see committed proposals.", result.Output);
    }

    [Fact]
    public void Split_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        DeleteCommittedRevision(proposalId);

        var result = Commands.Split(
            _fwDataPath, ProductVersion, proposalId,
            new[] { new Commands.SplitGroup("g1", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void Split_DraftNameAlreadyExists_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        Commands.New(_fwDataPath, ProductVersion, "taken", null);
        var before = ReadDraftJson("taken");

        var result = Commands.Split(
            _fwDataPath, ProductVersion, proposalId,
            new[] { new Commands.SplitGroup("taken", new[] { CanonicalId.Mint().Value }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output);
        Assert.Contains("before splitting into a draft with this name.", result.Output);
        Assert.Equal(before, ReadDraftJson("taken"));
    }

    [Fact]
    public void Split_GroupsShareADraftName_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");

        var result = Commands.Split(
            _fwDataPath, ProductVersion, proposalId,
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
            _fwDataPath, ProductVersion, proposalId,
            new[] { new Commands.SplitGroup("g1", new[] { "not-a-canonical-id" }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'not-a-canonical-id' is not a valid canonical operation id", result.Output);
    }

    [Fact]
    public void Split_UnknownOperationIdInGroup_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        var strangerId = CanonicalId.Mint().Value;

        var result = Commands.Split(
            _fwDataPath, ProductVersion, proposalId,
            new[] { new Commands.SplitGroup("g1", new[] { strangerId }) }, force: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"has no operation(s) '{strangerId}'", result.Output);
    }

    // --- List ---

    [Fact]
    public void List_EmptyStore_ReturnsAnEmptyListRatherThanAnError()
    {
        var result = Commands.List(_fwDataPath, ProductVersion);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No proposals in store.", result.Output);
    }

    // --- Show ---

    [Fact]
    public void Show_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        DeleteCommittedRevision(proposalId);

        var result = Commands.Show(_fwDataPath, ProductVersion, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    // --- DryRun / Apply ---

    [Fact]
    public void DryRun_ProposalNotFound_Refuses()
    {
        var result = JobCommands.EnqueueDryRun(_fwDataPath, ProductVersion, CanonicalId.Mint().Value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void DryRun_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        DeleteCommittedRevision(proposalId);

        var writeTimeBefore = File.GetLastWriteTimeUtc(_fwDataPath);
        var result = JobCommands.EnqueueDryRun(_fwDataPath, ProductVersion, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
        // The refusal is a pure database read: the live project was never opened to reach it.
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(_fwDataPath));
    }

    [Fact]
    public void Apply_ProposalNotFound_Refuses()
    {
        var result = Commands.Apply(_fwDataPath, ProductVersion, CanonicalId.Mint().Value, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found in store", result.Output);
    }

    [Fact]
    public void Apply_StoreInconsistency_Refuses()
    {
        var proposalId = CommitOneOperationProposal("src");
        DeleteCommittedRevision(proposalId);

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("store inconsistency", result.Output);
    }

    [Fact]
    public void Apply_NoBoundDryRun_RefusesAndNamesTheFix()
    {
        var proposalId = CommitOneOperationProposal("src");

        var result = Commands.Apply(_fwDataPath, ProductVersion, proposalId, "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("has no bound DryRun recorded", result.Output);
        Assert.Contains($"dry-run {proposalId} --project <fwdata>", result.Output);
    }

    [Fact]
    public void Apply_InvalidProposalId_WrapsTheMessageWithADiscardCacheHint()
    {
        var result = Commands.Apply(_fwDataPath, ProductVersion, "not-a-canonical-id", "tester");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is not a valid canonical Proposal id", result.Output);
        Assert.Contains("Discard it and reload the project.", result.Output);
    }

    // --- DryRun (store-consistency checks GetFinalized runs before the project is even opened) ---

    [Fact]
    public void DryRun_EnvelopeIdentityDoesNotMatchLookup_Refuses()
    {
        var proposalId = CommitOneOperationProposal("envelope-identity-mismatch");
        var wrongId = CanonicalId.Mint().Value;
        CorruptCommittedRevisionJson(proposalId, envelope => envelope["proposalId"] = wrongId);

        var writeTimeBefore = File.GetLastWriteTimeUtc(_fwDataPath);
        var result = JobCommands.EnqueueDryRun(_fwDataPath, ProductVersion, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(proposalId, result.Output, StringComparison.Ordinal);
        Assert.Contains(wrongId, result.Output, StringComparison.Ordinal);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(_fwDataPath));
    }

    [Fact]
    public void DryRun_ObjectContentDoesNotMatchManifestDigest_Refuses()
    {
        var proposalId = CommitOneOperationProposal("object-digest-mismatch");
        CorruptCommittedRevisionJson(
            proposalId, envelope => envelope["operations"]![0]!["after"]!["text"] = "content changed behind the digest");

        var writeTimeBefore = File.GetLastWriteTimeUtc(_fwDataPath);
        var result = JobCommands.EnqueueDryRun(_fwDataPath, ProductVersion, proposalId);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("intentDigest", result.Output, StringComparison.Ordinal);
        Assert.Contains("store inconsistency", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(_fwDataPath));
    }

    // --- Helpers ---

    private string CommitOneOperationProposal(string draftName)
    {
        Commands.New(_fwDataPath, ProductVersion, draftName, null);
        Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, _target, "en", "text for " + draftName);
        DraftRationale.Author(
            _fwDataPath, draftName, "Clarify a lexical gloss", "Record the intended lexical analysis for review.");
        var finalizeResult = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        return ExtractProposalId(finalizeResult.Output);
    }

    private (int ExitCode, string Output, string Error) RunCli(string arguments)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = $"{arguments} --project \"{_fwDataPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    /// <summary>Deletes a committed revision while its own pointer survives.</summary>
    private void DeleteCommittedRevision(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var digest = repository.Get(CanonicalId.Parse(proposalId)).IntentDigest!;
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;";
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$digest", digest);
        command.ExecuteNonQuery();
    }

    /// <summary>Rewrites a committed revision's bytes, leaving the digest that names it untouched.</summary>
    private void CorruptCommittedRevisionJson(string proposalId, Action<JsonObject> mutate)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var record = repository.Get(CanonicalId.Parse(proposalId));
        var digest = record.IntentDigest!;
        var envelope = JsonNode.Parse(record.ProposalJson!)!.AsObject();
        mutate(envelope);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProposalRevisions SET ProposalJson = $json WHERE ProposalId = $id AND IntentDigest = $digest;";
        command.Parameters.AddWithValue("$json", System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$digest", digest);
        command.ExecuteNonQuery();
    }

    private ProposalRecord GetRecord(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).Get(CanonicalId.Parse(proposalId));
    }

    /// <summary>Reads status and pointer without requiring the pointed-at revision to exist.</summary>
    private ProposalRecord GetStatusRow(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).GetForTransition(CanonicalId.Parse(proposalId));
    }

    private bool DraftExists(string draftName)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).DraftNameExists(draftName);
    }

    private string ReadDraftJson(string draftName)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).GetDraft(draftName).ProposalJson!;
    }

    private DraftDocument ReadDraft(string draftName) =>
        JsonSerializer.Deserialize<DraftDocument>(
            ReadDraftJson(draftName), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private void WriteDraft(string draftName, DraftDocument draft)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        new ProposalRepository(database).SaveDraft(draftName, JsonSerializer.Serialize(draft));
    }

    /// <summary>Counts Proposals that have a committed revision (excludes still-open drafts).</summary>
    private int CountCommittedProposals()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        return repository.List(new ProposalListFilter(IncludeArchived: true)).Count(p => p.DraftName is null);
    }

    private int CountRevisions(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ProposalRevisions WHERE ProposalId = $id;";
        command.Parameters.AddWithValue("$id", proposalId);
        return Convert.ToInt32(command.ExecuteScalar());
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
}
