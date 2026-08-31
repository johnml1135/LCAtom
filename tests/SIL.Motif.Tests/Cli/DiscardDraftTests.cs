using System;
using System.Diagnostics;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Generator;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// A Draft is a <c>Proposals</c> row (ADR 0041 decision 3); <c>discard-draft</c> is the only verb that
/// removes one. A never-finalized Draft has no committed revision behind it, so its row is deleted
/// outright. A Draft <c>reopen</c> produced carries its source Proposal's committed <c>ProposalRevisions</c>
/// and <c>Decisions</c> forward under the same id instead, so discarding it clears only the two columns
/// <c>reopen</c> set — the exact inverse — leaving the Proposal exactly at its prior committed revision.
/// </summary>
public sealed class DiscardDraftTests : IDisposable
{
    private const string ProductVersion = "1.0";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-discard-draft-" + Guid.NewGuid().ToString("N"));

    public DiscardDraftTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Project, string.Empty);
    }

    private string Project => Path.Combine(_root, "project.fwdata");

    [Fact]
    public void DiscardingANeverFinalizedDraftRemovesItAndFreesTheNameImmediately()
    {
        Assert.Equal(0, Commands.New(Project, ProductVersion, "d", "a label").ExitCode);

        var discarded = Commands.DiscardDraft(Project, ProductVersion, "d");

        Assert.Equal(0, discarded.ExitCode);
        Assert.Contains("Discarded draft 'd'", discarded.Output);
        Assert.False(OpenRepository().DraftNameExists("d"));

        // Reusable immediately, not merely absent from a listing: a fresh 'new' under the same name succeeds.
        var recreated = Commands.New(Project, ProductVersion, "d", "second label");
        Assert.Equal(0, recreated.ExitCode);
    }

    [Fact]
    public void DiscardingAnUnknownNameRefusesNotFound()
    {
        var result = Commands.DiscardDraft(Project, ProductVersion, "nope");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, result.Reason);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscardingAFinalizedProposalsIdRefusesNotFoundJustLikeAnAbsentDraft()
    {
        // A finalized Proposal has no DraftName row, so this takes the same NotFound path as an unknown name.
        Assert.Equal(0, Commands.New(Project, ProductVersion, "d", null).ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(Project, ProductVersion, "d", CanonicalId.Mint().Value, "en", "hello").ExitCode);
        DraftRationale.Author(Project, "d", "a label", "a comment");
        var finalize = Commands.Finalize(Project, ProductVersion, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        var result = Commands.DiscardDraft(Project, ProductVersion, proposalId);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, result.Reason);
    }

    [Fact]
    public void DiscardingADraftReopenedFromAFinalizedProposalRevertsToItsPriorCommittedRevision()
    {
        Assert.Equal(0, Commands.New(Project, ProductVersion, "d", null).ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(Project, ProductVersion, "d", CanonicalId.Mint().Value, "en", "hello").ExitCode);
        DraftRationale.Author(Project, "d", "a label", "a comment");
        var finalize = Commands.Finalize(Project, ProductVersion, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);
        var before = OpenRepository().Get(CanonicalId.Parse(proposalId));
        Assert.Equal(0, Commands.Reopen(Project, ProductVersion, "reopened", proposalId).ExitCode);

        var result = Commands.DiscardDraft(Project, ProductVersion, "reopened");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Discarded draft 'reopened'", result.Output);
        Assert.Contains("remains at its prior committed revision", result.Output);

        // The draft name is gone, and the Proposal is listed as committed again, not as a draft.
        var repository = OpenRepository();
        Assert.False(repository.DraftNameExists("reopened"));
        var after = repository.Get(CanonicalId.Parse(proposalId));
        Assert.Null(after.DraftName);
        // Every committed revision behind it is exactly as it was before the reopen.
        Assert.Equal(before.IntentDigest, after.IntentDigest);
        Assert.Equal(before.ProposalJson, after.ProposalJson);
        Assert.Equal(before.Status, after.Status);

        // The name is free again, not merely absent from a listing: a fresh 'new' under it succeeds.
        Assert.Equal(0, Commands.New(Project, ProductVersion, "reopened", null).ExitCode);
    }

    [Fact]
    public void ShowRendersTheSameProposalAfterReopenThenDiscardAsItDidBeforeTheReopen()
    {
        Assert.Equal(0, Commands.New(Project, ProductVersion, "d", null).ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(Project, ProductVersion, "d", CanonicalId.Mint().Value, "en", "hello").ExitCode);
        DraftRationale.Author(Project, "d", "a label", "a comment");
        var finalize = Commands.Finalize(Project, ProductVersion, "d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);
        var shownBefore = Commands.Show(Project, ProductVersion, proposalId);
        Assert.Equal(0, shownBefore.ExitCode);

        Assert.Equal(0, Commands.Reopen(Project, ProductVersion, "reopened", proposalId).ExitCode);
        Assert.Equal(0, Commands.DiscardDraft(Project, ProductVersion, "reopened").ExitCode);

        var shownAfter = Commands.Show(Project, ProductVersion, proposalId);
        Assert.Equal(0, shownAfter.ExitCode);
        Assert.Equal(shownBefore.Output, shownAfter.Output);
    }

    /// <summary>Drives the real executable, since this is a new verb on the published argv surface.</summary>
    [Fact]
    public void ArgvDiscardDraftDispatchesAndFreesTheNameForANewDraftOfTheSameName()
    {
        Assert.Equal(0, Run($"new --project \"{Project}\" --draft argv-d").ExitCode);

        var discarded = Run($"discard-draft --project \"{Project}\" --draft argv-d");
        Assert.Equal(0, discarded.ExitCode);
        Assert.Equal(string.Empty, discarded.Error);
        Assert.Contains("Discarded draft 'argv-d'", discarded.Output);

        var recreated = Run($"new --project \"{Project}\" --draft argv-d");
        Assert.Equal(0, recreated.ExitCode);
    }

    [Fact]
    public void ArgvDiscardDraftOfAnUnknownNameRefusesWithNotFoundExitCode()
    {
        var result = Run($"discard-draft --project \"{Project}\" --draft nope --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, Envelope(result.Error).Reason);
    }

    [Fact]
    public void ArgvOmittingTheDraftFlagIsAUsageFailureNamingTheVerb()
    {
        var result = Run($"discard-draft --project \"{Project}\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: motif discard-draft --project <fwdata> --draft <name>", result.Error, StringComparison.Ordinal);
    }

    private IProposalRepository OpenRepository() => new ProposalRepository(ProjectMotifDatabase.Open(Project));

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

    private static FailureEnvelope Envelope(string stderr) =>
        ProjectionJson.Deserialize<FailureEnvelope>(stderr)!;

    private static CliRun Run(string arguments)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
