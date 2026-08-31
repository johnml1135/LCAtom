using System;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Projection.Store;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The status transitions that survive the removal of approval: <c>defer</c>, <c>reject</c> and
/// <c>supersede</c> as explicit moves, each refusing from a status it is not legal from. Nothing here
/// authorises an apply — a Proposal is applied because it is ready, not because someone signed it.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ProposalStatusTransitionTests
{
    private const string ProductVersion = "1.0";

    private readonly string _fwDataPath;

    public ProposalStatusTransitionTests(PristineProjectFixture pristine)
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
        new ProposalRepository(database).SetStatus(CanonicalId.Parse(proposalId), status, supersededBy: null);
    }

    [Fact]
    public void Defer_MovesAProposedProposalToDeferred()
    {
        var id = CommitFreshProposal();

        Assert.Equal(0, Commands.Defer(_fwDataPath, ProductVersion, id).ExitCode);

        Assert.Equal(ManifestStatus.Deferred, GetRecord(id).Status);
    }

    [Fact]
    public void Reject_IsAllowedFromProposedAndFromDeferred()
    {
        var proposed = CommitFreshProposal("straight");
        Assert.Equal(0, Commands.Reject(_fwDataPath, ProductVersion, proposed).ExitCode);
        Assert.Equal(ManifestStatus.Rejected, GetRecord(proposed).Status);

        var deferred = CommitFreshProposal("later");
        Assert.Equal(0, Commands.Defer(_fwDataPath, ProductVersion, deferred).ExitCode);
        Assert.Equal(0, Commands.Reject(_fwDataPath, ProductVersion, deferred).ExitCode);
        Assert.Equal(ManifestStatus.Rejected, GetRecord(deferred).Status);
    }

    [Fact]
    public void Reject_ANotFoundProposal_Refuses()
    {
        var result = Commands.Reject(_fwDataPath, ProductVersion, CanonicalId.Mint().Value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reject_AnAlreadyAppliedProposal_Refuses_NamingTheDisallowedTransition()
    {
        var id = CommitFreshProposal();
        SetStatusRaw(id, ManifestStatus.Applied);

        var result = Commands.Reject(_fwDataPath, ProductVersion, id);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("applied", result.Output);
        Assert.Contains("rejected", result.Output);
        Assert.Equal(ManifestStatus.Applied, GetRecord(id).Status); // left untouched
    }

    [Fact]
    public void Supersede_NamesTheReplacement()
    {
        var id = CommitFreshProposal("old");
        var replacementId = CommitFreshProposal("new");

        var result = Commands.Supersede(_fwDataPath, ProductVersion, id, replacementId);

        Assert.Equal(0, result.ExitCode);
        var record = GetRecord(id);
        Assert.Equal(ManifestStatus.Superseded, record.Status);
        Assert.Equal(replacementId, record.SupersededBy);
    }

    [Fact]
    public void Supersede_IsAllowedFromRejected_SoAReplacementCanPointAtHoweverItEnded()
    {
        var id = CommitFreshProposal("old");
        Assert.Equal(0, Commands.Reject(_fwDataPath, ProductVersion, id).ExitCode);
        var replacementId = CommitFreshProposal("new");

        Assert.Equal(0, Commands.Supersede(_fwDataPath, ProductVersion, id, replacementId).ExitCode);

        Assert.Equal(ManifestStatus.Superseded, GetRecord(id).Status);
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
