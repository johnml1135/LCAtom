using System;
using System.IO;
using System.Text.Json.Nodes;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Proof, on a real project: drives the CLI command handlers directly (never shells out to
/// the built executable) through the full <c>new -&gt; add-set-gloss -&gt; finalize -&gt; dry-run -&gt;
/// apply -&gt; log</c> loop against a real sense's <c>CanonicalId.FromGuid(sense.Guid)</c>, proving
/// the paired project database and the thin CLI drive the real Contract/Runner/Host.
/// </summary>
/// <remarks>
/// A workflow test, not an end-to-end one: no process boundary is crossed here. Whether the shipped
/// executables do this is <c>RunnerSpineTests</c>' subject. <c>dry-run</c> is a job (ADR 0041 decision 7),
/// so <see cref="RunDryRun"/> stands in for the real runner: it records a Baseline pointing at this
/// project's own saved file (this suite never captures a separate Baseline bundle) and drains exactly
/// the one queued job through the real <see cref="DryRunJobHandler"/> before rendering it exactly as
/// <c>--wait</c> does.
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ProposalWorkflowTests
{
    private const string ProductVersion = "1.0";

    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public ProposalWorkflowTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    [Fact]
    public void FullLoop_New_AddSetGloss_Finalize_DryRun_Apply_Log_DrivesRealProjectEndToEnd()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised sense, Stage E CLI)";
        const string draftName = "stage-e-demo";
        const string label = "Stage E end-to-end demo";
        const string shortDescription = "Clarify the first sense gloss";
        const string extendedExplanation = "Replace the ambiguous gloss with the intended analysis.";
        const string applier = "motif-cli-tests";

        // --- new ---
        var newResult = Commands.New(_fwDataPath, ProductVersion, draftName, label);
        Assert.Equal(0, newResult.ExitCode);
        Assert.Contains("Created draft", newResult.Output);
        Assert.True(DraftExists(draftName));

        // --- add-set-gloss ---
        var addResult = Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, canonicalId.Value, wsTag, newGloss);
        Assert.Equal(0, addResult.ExitCode);
        Assert.Contains("lexical/lexSense/setGloss", addResult.Output);
        DraftRationale.Author(_fwDataPath, draftName, shortDescription, extendedExplanation);

        // --- finalize ---
        var finalizeResult = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        Assert.False(DraftExists(draftName)); // draft cleared on finalize

        var proposalId = ExtractProposalId(finalizeResult.Output);
        var intentDigest = ExtractIntentDigest(finalizeResult.Output);
        var committed = GetRecord(proposalId);
        Assert.Equal(intentDigest, committed.IntentDigest);
        Assert.Equal("proposed", committed.Status);

        // --- list ---
        var listResult = Commands.List(_fwDataPath, ProductVersion);
        Assert.Equal(0, listResult.ExitCode);
        Assert.Contains(proposalId, listResult.Output);
        Assert.Contains("proposed", listResult.Output);

        // --- show ---
        var showResult = Commands.Show(_fwDataPath, ProductVersion, proposalId);
        Assert.Equal(0, showResult.ExitCode);
        Assert.Contains(proposalId, showResult.Output);
        Assert.Contains(canonicalId.Value, showResult.Output);
        Assert.Contains(shortDescription, showResult.Output);
        Assert.Contains(extendedExplanation, showResult.Output);
        var showJsonResult = Commands.ShowJson(_fwDataPath, ProductVersion, proposalId);
        Assert.Equal(0, showJsonResult.ExitCode);
        Assert.Contains(shortDescription, showJsonResult.Output);
        Assert.Contains(extendedExplanation, showJsonResult.Output);

        // --- dry-run: real before/after from LibLCM, non-mutating ---
        var dryRunResult = RunDryRun(proposalId);
        Assert.Equal(0, dryRunResult.ExitCode);
        Assert.Contains(canonicalId.Value, dryRunResult.Output);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", dryRunResult.Output);
        Assert.Contains("effectDigest: sha256:", dryRunResult.Output);

        // The dry run must not have mutated the project: gloss unchanged when re-read from disk.
        AssertGlossOnDisk(senseGuid, wsTag, originalGloss);

        // --- apply: real commit + save ---
        var applyResult = Commands.Apply(_fwDataPath, ProductVersion, proposalId, applier, force: true);
        Assert.Equal(0, applyResult.ExitCode);
        Assert.Contains("Applied Proposal", applyResult.Output);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", applyResult.Output);
        Assert.Contains("applied-log entry:", applyResult.Output);
        Assert.Contains(applier, applyResult.Output);
        var appliedRecord = GetRecord(proposalId);
        Assert.Equal("applied", appliedRecord.Status);
        Assert.Equal(shortDescription, appliedRecord.Label);
        Assert.Equal(extendedExplanation, appliedRecord.Comment);

        // Apply persists: re-open the saved project from disk and check the gloss + one applied-log entry.
        AssertGlossOnDisk(senseGuid, wsTag, newGloss);
        AssertAppliedLogEntryCount(1);

        // --- log ---
        var logResult = Commands.Log(_fwDataPath);
        Assert.Equal(0, logResult.ExitCode);
        Assert.Contains(applier, logResult.Output);
        Assert.Contains("1 Motif entry", logResult.Output);

        // --- apply again: idempotent, no duplicate log entry, no re-mutation ---
        var secondApplyResult = Commands.Apply(_fwDataPath, ProductVersion, proposalId, applier, force: true);
        Assert.Equal(0, secondApplyResult.ExitCode);
        Assert.Contains("already applied", secondApplyResult.Output, StringComparison.OrdinalIgnoreCase);

        AssertGlossOnDisk(senseGuid, wsTag, newGloss);
        AssertAppliedLogEntryCount(1);
    }

    [Fact]
    public void FileDryRun_PreparesOneScratchWithPrerequisites_AndReportsOnlyTheDependentEffect()
    {
        var target = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(_seed.FirstSenseId);
        var intermediateGloss = SeededProject.FirstGloss + " prerequisite file";
        var finalGloss = SeededProject.FirstGloss + " dependent file";
        var prerequisiteId = FinalizeSetGloss("file-prerequisite", target.Value, intermediateGloss);
        var dependentId = FinalizeSetGloss(
            "file-dependent", target.Value, finalGloss, prerequisiteId);

        var result = RunDryRun(dependentId);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"{intermediateGloss}\" -> \"{finalGloss}\"", result.Output);
        Assert.DoesNotContain(
            $"\"{SeededProject.FirstGloss}\" -> \"{intermediateGloss}\"", result.Output);
        AssertGlossOnDisk(_seed.FirstSenseId, NewLangProjFixture.AnalysisTag, SeededProject.FirstGloss);
    }

    /// <remarks>
    /// The receipt boundary: <c>apply</c> commits and saves the mutation to the real project (a
    /// durable, observable fact on disk) before it ever tries to record "applied" in the store. The
    /// paired database is made read-only right beforehand, so that write is the one that fails -- this
    /// must not be reported the way a rolled-back apply is, since nothing here rolled back. See
    /// <see cref="NeedsReconciliationException"/> and <see cref="ReconciliationBoundary.ReceiptRecording"/>.
    /// </remarks>
    [Fact]
    public void Apply_ManifestWriteFails_AfterAGenuineCommitAndSave_ReportsReconciliation_NotRollback()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (receipt boundary test)";
        const string draftName = "receipt-boundary-demo";
        const string applier = "motif-cli-tests";

        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, null).ExitCode);
        Assert.Equal(
            0, Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        DraftRationale.Author(
            _fwDataPath, draftName, "Clarify the first sense gloss", "Record the intended analysis before applying the proposal.");
        var finalizeResult = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        var proposalId = ExtractProposalId(finalizeResult.Output);

        Assert.Equal(0, RunDryRun(proposalId).ExitCode);

        var dbPath = PairedDatabasePath();
        File.SetAttributes(dbPath, FileAttributes.ReadOnly);
        try
        {
            var applyResult = Commands.Apply(_fwDataPath, ProductVersion, proposalId, applier, force: true);

            Assert.NotEqual(0, applyResult.ExitCode);
            Assert.Contains("proposal store failed", applyResult.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rolled back", applyResult.Output, StringComparison.OrdinalIgnoreCase);

            // The load-bearing proof: the mutation genuinely committed and saved despite the report above.
            AssertGlossOnDisk(senseGuid, wsTag, newGloss);
            AssertAppliedLogEntryCount(1);
        }
        finally
        {
            File.SetAttributes(dbPath, FileAttributes.Normal);
        }

        // The store itself was left exactly as dry-run wrote it -- never touched by the failed write.
        Assert.Equal("proposed", GetRecord(proposalId).Status);
    }

    [Fact]
    public void UnknownCommands_And_MissingArguments_ReturnNonZeroWithClearErrors()
    {
        var missingDraft = Commands.AddSetGloss(
            _fwDataPath, ProductVersion, "does-not-exist", "agent_AAECAwQFBgcICQoLDA0ODw", "en", "x");
        Assert.NotEqual(0, missingDraft.ExitCode);
        Assert.Contains("not found", missingDraft.Output, StringComparison.OrdinalIgnoreCase);

        var badTarget = Commands.New(_fwDataPath, ProductVersion, "bad-target-draft", null);
        Assert.Equal(0, badTarget.ExitCode);
        var invalidTarget = Commands.AddSetGloss(
            _fwDataPath, ProductVersion, "bad-target-draft", "not-a-canonical-id", "en", "x");
        Assert.NotEqual(0, invalidTarget.ExitCode);
        Assert.Contains("not a valid canonical id", invalidTarget.Output);
        DraftRationale.Author(
            _fwDataPath, "bad-target-draft", "Test an empty proposal", "Keep the no-operations refusal independently observable.");

        var emptyFinalize = Commands.Finalize(_fwDataPath, ProductVersion, "bad-target-draft");
        Assert.NotEqual(0, emptyFinalize.ExitCode);
        Assert.Contains("no operations", emptyFinalize.Output);

        var missingProposal = Commands.Show(_fwDataPath, ProductVersion, "agent_AAECAwQFBgcICQoLDA0ODw");
        Assert.NotEqual(0, missingProposal.ExitCode);
        Assert.Contains("not found", missingProposal.Output, StringComparison.OrdinalIgnoreCase);

        var missingProjectPath = Path.Combine(Path.GetDirectoryName(_fwDataPath)!, "does-not-exist.fwdata");
        var missingProject = Commands.Log(missingProjectPath);
        Assert.NotEqual(0, missingProject.ExitCode);
        Assert.Contains("not found", missingProject.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drives <c>dry-run</c>'s job through <see cref="DryRunJobRunner"/> — see the class remarks.</summary>
    private CommandResult RunDryRun(string proposalId, bool asJson = false) =>
        DryRunJobRunner.Run(_fwDataPath, ProductVersion, proposalId, asJson);

    private void AssertGlossOnDisk(Guid senseGuid, string wsTag, string expectedGloss)
    {
        using var cache = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(expectedGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);
    }

    private void AssertAppliedLogEntryCount(int expectedCount)
    {
        using var cache = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        Assert.Equal(expectedCount, ProjectAppliedLog.ReadAll(cache).Count);
    }

    private string FinalizeSetGloss(
        string draftName, string targetId, string text, params string[] prerequisiteIds)
    {
        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, null).ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(
                _fwDataPath, ProductVersion, draftName, targetId, NewLangProjFixture.AnalysisTag, text).ExitCode);
        if (prerequisiteIds.Length > 0)
        {
            using var database = ProjectMotifDatabase.Open(_fwDataPath);
            var repository = new ProposalRepository(database);
            var draftJson = repository.GetDraft(draftName).ProposalJson!;
            var draft = JsonNode.Parse(draftJson)!.AsObject();
            draft["requires"] = new JsonArray(
                prerequisiteIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            repository.SaveDraft(draftName, draft.ToJsonString());
        }

        DraftRationale.Author(
            _fwDataPath, draftName, "Prepare a dependent gloss", "Establish the lexical state required by later proposals.");
        var finalized = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalized.ExitCode);
        return ExtractProposalId(finalized.Output);
    }

    private bool DraftExists(string draftName)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).DraftNameExists(draftName);
    }

    private ProposalRecord GetRecord(string proposalId)
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return new ProposalRepository(database).Get(CanonicalId.Parse(proposalId));
    }

    private string PairedDatabasePath()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        return database.FullPath;
    }

    private static string ExtractProposalId(string finalizeOutput)
    {
        // "Finalized draft 'name' -> Proposal <id> (status: proposed)."
        const string marker = "-> Proposal ";
        var start = finalizeOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in finalize output: {finalizeOutput}");
        start += marker.Length;
        var end = finalizeOutput.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from finalize output: {finalizeOutput}");
        return finalizeOutput.Substring(start, end - start);
    }

    private static string ExtractIntentDigest(string commandOutput)
    {
        // "  intentDigest: sha256:<hex>"
        const string marker = "intentDigest: ";
        var start = commandOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {commandOutput}");
        start += marker.Length;
        var end = commandOutput.IndexOfAny(new[] { '\r', '\n' }, start);
        Assert.True(end > start, $"Could not parse intentDigest from output: {commandOutput}");
        return commandOutput.Substring(start, end - start).Trim();
    }
}
