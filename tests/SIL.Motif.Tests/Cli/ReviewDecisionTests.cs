using System;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Projection.Store;
using SIL.Motif.Tests.TestFixtures;
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
    private readonly string _storeDir;

    public ReviewDecisionTests(PristineProjectFixture pristine)
    {
        using var scratch = pristine.NewScratch();
        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(scratch.ProjectId.Path))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    private string CommitFreshProposal(string draftName = "d")
    {
        Assert.Equal(0, Commands.New(_storeDir, draftName, null).ExitCode);
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, draftName, target, "en", "a gloss").ExitCode);
        var finalize = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalize.ExitCode);
        return ExtractProposalId(finalize.Output);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ManifestJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private ManifestDocument ReadManifest(string proposalId) =>
        System.Text.Json.JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(new ProposalStore(_storeDir).ManifestPath(proposalId)), ManifestJsonOptions)!;

    [Fact]
    public void Approve_RecordsADecisionLabelledWithItsActor_AndMovesStatus()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_storeDir, id, "human", "a-linguist", "looks correct");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("approved", result.Output);
        var manifest = ReadManifest(id);
        Assert.Equal(ManifestStatus.Approved, manifest.Status);
        Assert.NotNull(manifest.Decision);
        Assert.Equal(DecisionOutcome.Approved, manifest.Decision!.Outcome);
        Assert.Equal(DecisionActorType.Human, manifest.Decision.ActorType);
        Assert.Equal("a-linguist", manifest.Decision.ActorId);
        Assert.Equal("looks correct", manifest.Decision.Comment);
        Assert.Equal(manifest.CurrentIntentDigest, manifest.Decision.BoundIntentDigest);
    }

    [Fact]
    public void Approve_WithAnUnlabelledActorType_Refuses_AndRecordsNoDecision()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_storeDir, id, "definitely-human", "someone");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("actorType", result.Output);
        Assert.Null(ReadManifest(id).Decision);
        Assert.Equal(ManifestStatus.Proposed, ReadManifest(id).Status);
    }

    [Fact]
    public void Approve_WithNoActorId_Refuses()
    {
        var id = CommitFreshProposal();

        var result = Commands.Approve(_storeDir, id, "ai", "");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("actorId", result.Output);
    }

    [Fact]
    public void Reject_FromApproved_IsAllowed_AndReplacesTheDecision()
    {
        var id = CommitFreshProposal();
        Assert.Equal(0, Commands.Approve(_storeDir, id, "ai", "weak-model").ExitCode);

        var result = Commands.Reject(_storeDir, id, "human", "a-reviewer", "actually wrong");

        Assert.Equal(0, result.ExitCode);
        var manifest = ReadManifest(id);
        Assert.Equal(ManifestStatus.Rejected, manifest.Status);
        Assert.Equal(DecisionOutcome.Rejected, manifest.Decision!.Outcome);
        Assert.Equal("a-reviewer", manifest.Decision.ActorId);
    }

    [Fact]
    public void Approve_ANotFoundProposal_Refuses()
    {
        var result = Commands.Approve(_storeDir, CanonicalId.Mint().Value, "human", "someone");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Defer_ThenApprove_RoundTrips()
    {
        var id = CommitFreshProposal();

        Assert.Equal(0, Commands.Defer(_storeDir, id).ExitCode);
        Assert.Equal(ManifestStatus.Deferred, ReadManifest(id).Status);

        Assert.Equal(0, Commands.Approve(_storeDir, id, "human", "a-linguist").ExitCode);
        Assert.Equal(ManifestStatus.Approved, ReadManifest(id).Status);
    }

    [Fact]
    public void Approve_AnAlreadyAppliedProposal_Refuses_NamingTheDisallowedTransition()
    {
        var id = CommitFreshProposal();
        var manifestPath = new ProposalStore(_storeDir).ManifestPath(id);
        var manifest = ReadManifest(id);
        manifest.Status = ManifestStatus.Applied;
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));

        var result = Commands.Approve(_storeDir, id, "human", "a-linguist");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("applied", result.Output);
        Assert.Contains("approved", result.Output);
        Assert.Equal(ManifestStatus.Applied, ReadManifest(id).Status); // left untouched
    }

    [Fact]
    public void Supersede_NamesTheReplacement_AndClearsAnyPriorDecision()
    {
        var id = CommitFreshProposal("old");
        Assert.Equal(0, Commands.Approve(_storeDir, id, "human", "a-linguist").ExitCode);
        var replacementId = CommitFreshProposal("new");

        var result = Commands.Supersede(_storeDir, id, replacementId);

        Assert.Equal(0, result.ExitCode);
        var manifest = ReadManifest(id);
        Assert.Equal(ManifestStatus.Superseded, manifest.Status);
        Assert.Equal(replacementId, manifest.SupersededBy);
        Assert.Null(manifest.Decision);
    }

    [Fact]
    public void Amend_DropsAnyRecordedDecision_TheSameWayItDropsTheBoundAnchor()
    {
        var id = CommitFreshProposal();
        Assert.Equal(0, Commands.Approve(_storeDir, id, "ai", "weak-model").ExitCode);
        Assert.NotNull(ReadManifest(id).Decision);

        Assert.Equal(0, Commands.Reopen(_storeDir, "amend", id).ExitCode);
        var target = CanonicalId.Mint().Value;
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "amend", target, "en", "a second gloss").ExitCode);
        var amend = Commands.Finalize(_storeDir, "amend");
        Assert.Equal(0, amend.ExitCode);

        var manifest = ReadManifest(id);
        Assert.Equal(ManifestStatus.Proposed, manifest.Status);
        Assert.Null(manifest.Decision);
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
