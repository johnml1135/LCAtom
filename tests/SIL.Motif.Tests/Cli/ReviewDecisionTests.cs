using System;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Projection.Store;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Statuses are decisions, per ADR 0031 decisions 3, 4, 7: <c>defer</c>/<c>approve</c>/<c>reject</c>/
/// <c>supersede</c> as explicit transitions, each refusing from a status it is not legal from, and an
/// amend dropping any recorded Decision the same way it already drops the bound DryRun anchor.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ReviewDecisionTests
{
    private const string ProductVersion = "1.0";

    private readonly string _fwDataPath;

    public ReviewDecisionTests(PristineProjectFixture pristine)
    {
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    private string CommitFreshProposal(string draftName = "d")
    {
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, null).ExitCode);
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, target, "en", "a gloss").ExitCode);
        DraftRationale.Author(
            _fwDataPath, draftName, "Clarify a lexical analysis", "Record the intended gloss so reviewers can assess the change.");
        var finalize = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalize.ExitCode);
        return ExtractProposalId(finalize.Output);
    }

    private ProposalRecord GetRecord(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).Get(CanonicalId.Parse(proposalId));
    }

    private void SetStatusRaw(string proposalId, string status)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        new ProposalRepository(database).SetStatus(
            CanonicalId.Parse(proposalId), status, supersededBy: null, clearDecision: false);
    }

    [Fact]
    public void Approve_RecordsADecisionLabelledWithItsActor_AndMovesStatus()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_fwDataPath, ProductVersion, id, "human", "a-linguist", "looks correct");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("approved", result.Output);
        var record = GetRecord(id);
        Assert.Equal(ManifestStatus.Approved, record.Status);
        Assert.NotNull(record.Decision);
        Assert.Equal(DecisionOutcome.Approved, record.Decision!.Outcome);
        Assert.Equal(DecisionActorType.Human, record.Decision.ActorType);
        Assert.Equal("a-linguist", record.Decision.ActorId);
        Assert.Equal("looks correct", record.Decision.Comment);
        Assert.Equal(record.IntentDigest, record.Decision.IntentDigest);
        Assert.Equal("Clarify a lexical analysis", record.Label);
        Assert.Equal("Record the intended gloss so reviewers can assess the change.", record.Comment);
    }

    [Fact]
    public void Approve_WithAnUnlabelledActorType_Refuses_AndRecordsNoDecision()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_fwDataPath, ProductVersion, id, "definitely-human", "someone");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("actorType", result.Output);
        var record = GetRecord(id);
        Assert.Null(record.Decision);
        Assert.Equal(ManifestStatus.Proposed, record.Status);
    }

    [Fact]
    public void Approve_WithNoActorId_Refuses()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_fwDataPath, ProductVersion, id, "ai", "");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("actorId", result.Output);
    }

    [Fact]
    public void Reject_FromApproved_IsAllowed_AndReplacesTheDecision()
    {
        var id = CommitFreshProposal();
        Assert.Equal(0, Commands.Approve(_fwDataPath, ProductVersion, id, "ai", "weak-model").ExitCode);

        var result = Commands.Reject(_fwDataPath, ProductVersion, id, "human", "a-reviewer", "actually wrong");

        Assert.Equal(0, result.ExitCode);
        var record = GetRecord(id);
        Assert.Equal(ManifestStatus.Rejected, record.Status);
        Assert.Equal(DecisionOutcome.Rejected, record.Decision!.Outcome);
        Assert.Equal("a-reviewer", record.Decision.ActorId);
    }

    [Fact]
    public void Approve_ANotFoundProposal_Refuses()
    {
        var result = Commands.Approve(_fwDataPath, ProductVersion, CanonicalId.Mint().Value, "human", "someone");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Defer_ThenApprove_RoundTrips()
    {
        var id = CommitFreshProposal();

        Assert.Equal(0, Commands.Defer(_fwDataPath, ProductVersion, id).ExitCode);
        Assert.Equal(ManifestStatus.Deferred, GetRecord(id).Status);

        Assert.Equal(0, Commands.Approve(_fwDataPath, ProductVersion, id, "human", "a-linguist").ExitCode);
        Assert.Equal(ManifestStatus.Approved, GetRecord(id).Status);
    }

    [Fact]
    public void Approve_AnAlreadyAppliedProposal_Refuses_NamingTheDisallowedTransition()
    {
        var id = CommitFreshProposal();
        SetStatusRaw(id, ManifestStatus.Applied);

        var result = Commands.Approve(_fwDataPath, ProductVersion, id, "human", "a-linguist");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("applied", result.Output);
        Assert.Contains("approved", result.Output);
        Assert.Equal(ManifestStatus.Applied, GetRecord(id).Status); // left untouched
    }

    [Fact]
    public void Supersede_NamesTheReplacement_AndClearsAnyPriorDecision()
    {
        var id = CommitFreshProposal("old");
        Assert.Equal(0, Commands.Approve(_fwDataPath, ProductVersion, id, "human", "a-linguist").ExitCode);
        var replacementId = CommitFreshProposal("new");

        var result = Commands.Supersede(_fwDataPath, ProductVersion, id, replacementId);

        Assert.Equal(0, result.ExitCode);
        var record = GetRecord(id);
        Assert.Equal(ManifestStatus.Superseded, record.Status);
        Assert.Equal(replacementId, record.SupersededBy);
        Assert.Null(record.Decision);
    }

    [Fact]
    public void Amend_DropsAnyRecordedDecision_TheSameWayItDropsTheBoundAnchor()
    {
        var id = CommitFreshProposal();
        Assert.Equal(0, Commands.Approve(_fwDataPath, ProductVersion, id, "ai", "weak-model").ExitCode);
        Assert.NotNull(GetRecord(id).Decision);

        Assert.Equal(0, Commands.Reopen(_fwDataPath, ProductVersion, "amend", id).ExitCode);
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.AddSetGloss(_fwDataPath, ProductVersion, "amend", target, "en", "a second gloss").ExitCode);
        var amend = Commands.Finalize(_fwDataPath, ProductVersion, "amend");
        Assert.Equal(0, amend.ExitCode);

        var record = GetRecord(id);
        Assert.Equal(ManifestStatus.Proposed, record.Status);
        Assert.Null(record.Decision);
    }

    private static string ExtractProposalId(string finalizeOutput)
    {
        const string marker = "-> Proposal ";
        var start = finalizeOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in finalize output: {finalizeOutput}");
        start += marker.Length;
        var end = finalizeOutput.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from finalize output: {finalizeOutput}");
        return finalizeOutput.Substring(start, end - start);
    }
}
