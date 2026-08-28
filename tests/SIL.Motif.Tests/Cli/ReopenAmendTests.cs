using System;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The store keys revisions by <c>intentDigest</c> (write-once, never revisited) and the Proposal by
/// its frozen <c>proposalId</c> (a movable <c>currentIntentDigest</c> pointer), exactly git's
/// object/ref split — so <c>reopen</c> + re-<c>finalize</c> (an amend) can move that pointer to a new
/// revision without ever mutating the previous one (ADR 0004, decision 2).
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ReopenAmendTests
{
    private const string ProductVersion = "1.0";

    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public ReopenAmendTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void Commit_Reopen_Amend_KeepsId_MovesDigest_RetainsBothObjectVersions_ResetsStatusToProposed()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = CanonicalId.FromGuid(senseGuid);

        // --- commit v1 ---
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, "v1", "first label").ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(_fwDataPath, ProductVersion, "v1", canonicalId.Value, wsTag, originalGloss + " v1").ExitCode);
        DraftRationale.Author(
            _fwDataPath, "v1", "Clarify the first sense gloss", "Replace the ambiguous gloss with the intended analysis.");
        var firstFinalize = Commands.Finalize(_fwDataPath, ProductVersion, "v1");
        Assert.Equal(0, firstFinalize.ExitCode);
        Assert.Contains("Finalized draft", firstFinalize.Output);

        var proposalId = ExtractProposalId(firstFinalize.Output);
        var firstDigest = ExtractIntentDigest(firstFinalize.Output);

        Assert.Equal("proposed", GetRecord(proposalId).Status);
        Assert.Equal(1, CountRevisions(proposalId));

        // Simulate a real prior "applied" status, so amend's reset to "proposed" is a real transition.
        SetStatusRaw(proposalId, "applied");
        Assert.Equal("applied", GetRecord(proposalId).Status);

        // --- reopen: loads v1's content into a NEW draft carrying the SAME frozen proposalId ---
        var reopenResult = Commands.Reopen(_fwDataPath, ProductVersion, "v2", proposalId);
        Assert.Equal(0, reopenResult.ExitCode);
        Assert.Contains(proposalId, reopenResult.Output);
        Assert.True(DraftExists("v2"));
        var reopenedDraft = ReadDraft("v2");
        Assert.Equal("Clarify the first sense gloss", reopenedDraft.Label);
        Assert.Equal("Replace the ambiguous gloss with the intended analysis.", reopenedDraft.Comment);

        // Amend the content: add a second operation so the intent digest necessarily moves.
        Assert.Equal(
            0,
            Commands.AddSetGloss(_fwDataPath, ProductVersion, "v2", canonicalId.Value, wsTag, originalGloss + " v2 (amended)").ExitCode);

        // --- finalize the reopened draft: an amend, not a fresh commit ---
        var amendFinalize = Commands.Finalize(_fwDataPath, ProductVersion, "v2");
        Assert.Equal(0, amendFinalize.ExitCode);
        Assert.Contains("Amended draft", amendFinalize.Output);
        Assert.False(DraftExists("v2")); // draft consumed, same as a normal finalize

        var amendedProposalId = ExtractProposalId(amendFinalize.Output);
        var secondDigest = ExtractIntentDigest(amendFinalize.Output);

        // (1) The id is unchanged.
        Assert.Equal(proposalId, amendedProposalId);

        // (2) The digest changed (different content: two operations, not one).
        Assert.NotEqual(firstDigest, secondDigest);

        // (3) BOTH revision versions exist — the amend never touched the original write-once revision.
        Assert.Equal(2, CountRevisions(proposalId));
        Assert.Contains(originalGloss + " v1", GetRevisionJson(proposalId, firstDigest));
        Assert.Contains(originalGloss + " v2 (amended)", GetRevisionJson(proposalId, secondDigest));

        // (4) Status reset to proposed: approval/anchors are effect-digest-scoped, so content changes invalidate them.
        var afterAmend = GetRecord(proposalId);
        Assert.Equal("proposed", afterAmend.Status);
        Assert.Equal(secondDigest, afterAmend.IntentDigest);
        Assert.Equal("Clarify the first sense gloss", afterAmend.Label);
        Assert.Equal("Replace the ambiguous gloss with the intended analysis.", afterAmend.Comment);

        // Only one Proposals row exists for this id: it's the movable pointer, updated in place, never re-created.
        Assert.Equal(1, CountProposalRows(proposalId));
    }

    [Fact]
    public void Show_OlderManifestWithoutRationale_RemainsReadable()
    {
        var target = CanonicalId.FromGuid(_seed.FirstSenseId).Value;
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, "legacy", null).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_fwDataPath, ProductVersion, "legacy", target, "en", "a legacy gloss").ExitCode);
        DraftRationale.Author(
            _fwDataPath, "legacy", "Clarify a legacy gloss", "Create a manifest that can model an older stored record.");
        var finalized = Commands.Finalize(_fwDataPath, ProductVersion, "legacy");
        var proposalId = ExtractProposalId(finalized.Output);
        ClearLabelAndComment(proposalId);

        Assert.Equal(0, Commands.Show(_fwDataPath, ProductVersion, proposalId).ExitCode);
        Assert.Equal(0, Commands.ShowJson(_fwDataPath, ProductVersion, proposalId).ExitCode);
    }

    [Fact]
    public void Reopen_UnknownProposalId_Fails()
    {
        var bogusId = CanonicalId.Mint().Value;
        var result = Commands.Reopen(_fwDataPath, ProductVersion, "some-draft", bogusId);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private ProposalRecord GetRecord(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).Get(CanonicalId.Parse(proposalId));
    }

    private bool DraftExists(string draftName)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).DraftNameExists(draftName);
    }

    private DraftDocument ReadDraft(string draftName)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var json = new ProposalRepository(database).GetDraft(draftName).ProposalJson!;
        return JsonSerializer.Deserialize<DraftDocument>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>Sets a Proposal's status directly, bypassing the transition rules.</summary>
    private void SetStatusRaw(string proposalId, string status)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        new ProposalRepository(database).SetStatus(
            CanonicalId.Parse(proposalId), status, supersededBy: null, clearDecision: false);
    }

    private void ClearLabelAndComment(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Proposals SET Label = NULL, Comment = NULL WHERE ProposalId = $id;";
        command.Parameters.AddWithValue("$id", proposalId);
        command.ExecuteNonQuery();
    }

    private string GetRevisionJson(string proposalId, string intentDigest)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ProposalJson FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;";
        command.Parameters.AddWithValue("$id", proposalId);
        command.Parameters.AddWithValue("$digest", intentDigest);
        var bytes = (byte[])command.ExecuteScalar()!;
        return System.Text.Encoding.UTF8.GetString(bytes);
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

    private int CountProposalRows(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Proposals WHERE ProposalId = $id;";
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
