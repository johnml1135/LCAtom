using System;
using System.IO;
using System.Text.Json.Nodes;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Proof that the session-backed <see cref="Commands.DryRun(CliSession,string,string)"/> and
/// <see cref="Commands.Apply(CliSession,string,string,string)"/> overloads drive the same files store
/// and produce the same observable effects as the path-based overloads
/// (<see cref="EndToEndCliTests"/>), while running against one open <see cref="CliSession"/> instead
/// of loading and disposing a cache per call.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class SessionCommandsTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public SessionCommandsTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;

        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(_fwDataPath))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    [Fact]
    public void DryRun_ThenApply_ThroughOneSession_MatchesTheFilesStoreAndPersistsTheEffect()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised via session)";
        const string draftName = "session-demo";
        const string applier = "session-commands-tests";

        Assert.Equal(0, Commands.New(_storeDir, draftName, null).ExitCode);
        Assert.Equal(
            0, Commands.AddSetGloss(_storeDir, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        DraftRationale.Author(
            _storeDir, draftName, "Clarify the first sense gloss", "Replace the ambiguous gloss with the intended analysis.");
        var finalizeResult = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        var proposalId = ExtractProposalId(finalizeResult.Output);
        var manifestPath = Path.Combine(_storeDir, "manifests", proposalId + ".json");

        using (var session = CliSession.Open(_fwDataPath))
        {
            var dryRunResult = Commands.DryRun(session, _storeDir, proposalId);
            Assert.Equal(0, dryRunResult.ExitCode);
            Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", dryRunResult.Output);
            Assert.Contains("bound-DryRun anchor recorded", dryRunResult.Output);

            // A second dry run over the same session must see the same effect (nothing changed in between).
            var secondDryRunResult = Commands.DryRun(session, _storeDir, proposalId);
            Assert.Equal(0, secondDryRunResult.ExitCode);
            Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", secondDryRunResult.Output);
            Assert.Equal(1, session.PristineRebuildCount);

            var applyResult = Commands.Apply(session, _storeDir, proposalId, applier);
            Assert.Equal(0, applyResult.ExitCode);
            Assert.Contains("Applied Proposal", applyResult.Output);
            Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", applyResult.Output);
        }

        Assert.Contains("\"status\": \"applied\"", File.ReadAllText(manifestPath));
        AssertGlossOnDisk(senseGuid, wsTag, newGloss);

        // Teardown released the lock: the project reopens cleanly outside the session.
        using var reopened = new FwDataProjectLoader().LoadCache(_fwDataPath);
        Assert.False(string.IsNullOrWhiteSpace(reopened.ProjectId.Name));
    }

    [Fact]
    public void SessionDryRun_PreparesOneScratchWithPrerequisites_AndReportsOnlyTheDependentEffect()
    {
        var target = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(_seed.FirstSenseId);
        var intermediateGloss = SeededProject.FirstGloss + " prerequisite session";
        var finalGloss = SeededProject.FirstGloss + " dependent session";
        var prerequisiteId = FinalizeSetGloss("session-prerequisite", target.Value, intermediateGloss);
        var dependentId = FinalizeSetGloss(
            "session-dependent", target.Value, finalGloss, prerequisiteId);

        using var session = CliSession.Open(_fwDataPath);
        var result = Commands.DryRun(session, _storeDir, dependentId);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"{intermediateGloss}\" -> \"{finalGloss}\"", result.Output);
        Assert.DoesNotContain(
            $"\"{SeededProject.FirstGloss}\" -> \"{intermediateGloss}\"", result.Output);
        Assert.Equal(SeededProject.FirstGloss, ReadGloss(session.LiveCache));
    }

    [Fact]
    public void AppliedPrerequisite_InvalidatesPristineBeforePlannerPrunesItFromDependentDryRun()
    {
        var prerequisiteTarget = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(_seed.FirstSenseId);
        var dependentTarget = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(_seed.SecondSenseId);
        var prerequisiteGloss = SeededProject.FirstGloss + " applied prerequisite";
        var dependentGloss = SeededProject.SecondGloss + " dependent after apply";
        var prerequisiteId = FinalizeSetGloss(
            "applied-prerequisite", prerequisiteTarget.Value, prerequisiteGloss);
        var dependentId = FinalizeSetGloss(
            "dependent-after-applied-prerequisite", dependentTarget.Value, dependentGloss, prerequisiteId);

        using var session = CliSession.Open(_fwDataPath);
        Assert.Equal(0, Commands.DryRun(session, _storeDir, prerequisiteId).ExitCode);
        Assert.Equal(1, session.PristineRebuildCount);
        Assert.Equal(0, Commands.Apply(session, _storeDir, prerequisiteId, "session-tests").ExitCode);

        var result = Commands.DryRun(session, _storeDir, dependentId);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"\"{SeededProject.SecondGloss}\" -> \"{dependentGloss}\"", result.Output);
        Assert.Equal(prerequisiteGloss, ReadGloss(session.LiveCache));
        Assert.Equal(2, session.PristineRebuildCount);
    }

    /// <remarks>
    /// The session-backed counterpart to
    /// <see cref="EndToEndCliTests.Apply_ManifestWriteFails_AfterAGenuineCommitAndSave_ReportsReconciliation_NotRollback"/>:
    /// the same receipt boundary, reached through <see cref="Commands.Apply(CliSession,string,string,string,UsageLog)"/>
    /// instead of the per-invocation path. <see cref="CliSession.Apply"/>'s own commit and save succeed
    /// normally here; only the manifest write that follows fails.
    /// </remarks>
    [Fact]
    public void Apply_ManifestWriteFails_AfterASessionCommitAndSave_ReportsReconciliation_NotRollback()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (session receipt boundary test)";
        const string draftName = "session-receipt-boundary-demo";
        const string applier = "session-commands-tests";

        Assert.Equal(0, Commands.New(_storeDir, draftName, null).ExitCode);
        Assert.Equal(
            0, Commands.AddSetGloss(_storeDir, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        DraftRationale.Author(
            _storeDir, draftName, "Clarify the first sense gloss", "Replace the ambiguous gloss with the intended analysis.");
        var finalizeResult = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        var proposalId = ExtractProposalId(finalizeResult.Output);
        var manifestPath = Path.Combine(_storeDir, "manifests", proposalId + ".json");

        using (var session = CliSession.Open(_fwDataPath))
        {
            Assert.Equal(0, Commands.DryRun(session, _storeDir, proposalId).ExitCode);

            File.SetAttributes(manifestPath, FileAttributes.ReadOnly);
            try
            {
                var applyResult = Commands.Apply(session, _storeDir, proposalId, applier);

                Assert.NotEqual(0, applyResult.ExitCode);
                Assert.Contains("proposal store failed", applyResult.Output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("rolled back", applyResult.Output, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.SetAttributes(manifestPath, FileAttributes.Normal);
            }
        }

        // The load-bearing proof: the mutation genuinely committed and saved despite the report above.
        AssertGlossOnDisk(senseGuid, wsTag, newGloss);
        Assert.Contains("\"status\": \"proposed\"", File.ReadAllText(manifestPath));
    }

    private void AssertGlossOnDisk(Guid senseGuid, string wsTag, string expectedGloss)
    {
        using var cache = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(expectedGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);
    }

    private string FinalizeSetGloss(
        string draftName, string targetId, string text, params string[] prerequisiteIds)
    {
        Assert.Equal(0, Commands.New(_storeDir, draftName, null).ExitCode);
        Assert.Equal(
            0,
            Commands.AddSetGloss(
                _storeDir, draftName, targetId, NewLangProjFixture.AnalysisTag, text).ExitCode);
        if (prerequisiteIds.Length > 0)
        {
            var draftPath = new ProposalStore(_storeDir).DraftPath(draftName);
            var draft = JsonNode.Parse(File.ReadAllText(draftPath))!.AsObject();
            draft["requires"] = new JsonArray(
                prerequisiteIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            File.WriteAllText(draftPath, draft.ToJsonString());
        }

        DraftRationale.Author(
            _storeDir, draftName, "Prepare a dependent gloss", "Establish the lexical state required by later proposals.");
        var finalized = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalized.ExitCode);
        return ExtractProposalId(finalized.Output);
    }

    private string ReadGloss(LcmCache cache)
    {
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(NewLangProjFixture.AnalysisTag);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        return senseRepo.GetObject(_seed.FirstSenseId).Gloss.get_String(wsHandle).Text;
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
