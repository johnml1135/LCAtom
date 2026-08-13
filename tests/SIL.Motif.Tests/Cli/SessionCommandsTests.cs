using System;
using System.IO;
using SIL.Motif.Cli;
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

    private void AssertGlossOnDisk(Guid senseGuid, string wsTag, string expectedGloss)
    {
        using var cache = new FwDataProjectLoader().LoadScratchCache(_fwDataPath);
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(expectedGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);
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
