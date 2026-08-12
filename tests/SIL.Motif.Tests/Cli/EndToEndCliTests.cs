using System;
using System.IO;
using System.Linq;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Proof, on a real project: drives the CLI command handlers directly (never shells out to
/// the built executable) through the full <c>new -&gt; add-set-gloss -&gt; finalize -&gt; dry-run -&gt;
/// apply -&gt; log</c> loop against a real sense's <c>CanonicalId.FromGuid(sense.Guid)</c>, proving
/// the files store (drafts/objects/manifests) and the thin CLI drive the real Contract/Runner/Host
/// end to end.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
[Trait("Fixture", "FieldWorks")]
public sealed class EndToEndCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public EndToEndCliTests()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Cli", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _storeDir = Path.Combine(_tempRoot, ".motif-store");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup; a locked native handle should not fail the test
        }
    }

    [Fact]
    public void FullLoop_New_AddSetGloss_Finalize_DryRun_Apply_Log_DrivesRealProjectEndToEnd()
    {
        var (senseGuid, wsTag, originalGloss) = FindSenseWithKnownGloss();
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised sense, Stage E CLI)";
        const string draftName = "stage-e-demo";
        const string label = "Stage E end-to-end demo";
        const string applier = "motif-cli-tests";

        // --- new ---
        var newResult = Commands.New(_storeDir, draftName, label);
        Assert.Equal(0, newResult.ExitCode);
        Assert.Contains("Created draft", newResult.Output);
        var draftPath = Path.Combine(_storeDir, "drafts", draftName + ".json");
        Assert.True(File.Exists(draftPath));

        // --- add-set-gloss ---
        var addResult = Commands.AddSetGloss(_storeDir, draftName, canonicalId.Value, wsTag, newGloss);
        Assert.Equal(0, addResult.ExitCode);
        Assert.Contains("lexical/lexSense/setGloss", addResult.Output);

        // --- finalize ---
        var finalizeResult = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalizeResult.ExitCode);
        Assert.False(File.Exists(draftPath)); // draft deleted on finalize

        var proposalId = ExtractProposalId(finalizeResult.Output);
        var intentDigest = ExtractIntentDigest(finalizeResult.Output);
        // Objects are keyed by intentDigest (content-addressed, write-once), not by proposalId.
        var objectPath = new ProposalStore(_storeDir).ObjectPath(intentDigest);
        var manifestPath = Path.Combine(_storeDir, "manifests", proposalId + ".json");
        Assert.True(File.Exists(objectPath));
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("\"status\": \"proposed\"", File.ReadAllText(manifestPath));
        Assert.Contains(intentDigest, File.ReadAllText(manifestPath));

        // --- list ---
        var listResult = Commands.List(_storeDir);
        Assert.Equal(0, listResult.ExitCode);
        Assert.Contains(proposalId, listResult.Output);
        Assert.Contains("proposed", listResult.Output);

        // --- show ---
        var showResult = Commands.Show(_storeDir, proposalId);
        Assert.Equal(0, showResult.ExitCode);
        Assert.Contains(proposalId, showResult.Output);
        Assert.Contains(canonicalId.Value, showResult.Output);

        // --- dry-run: real before/after from LibLCM, non-mutating ---
        var dryRunResult = Commands.DryRun(_storeDir, proposalId, _fwDataPath);
        Assert.Equal(0, dryRunResult.ExitCode);
        Assert.Contains(canonicalId.Value, dryRunResult.Output);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", dryRunResult.Output);
        Assert.Contains("effectDigest: sha256:", dryRunResult.Output);

        // The dry run must not have mutated the project: gloss unchanged when re-read from disk.
        AssertGlossOnDisk(senseGuid, wsTag, originalGloss);

        // --- apply: real commit + save ---
        var applyResult = Commands.Apply(_storeDir, proposalId, _fwDataPath, applier);
        Assert.Equal(0, applyResult.ExitCode);
        Assert.Contains("Applied Proposal", applyResult.Output);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", applyResult.Output);
        Assert.Contains("applied-log entry:", applyResult.Output);
        Assert.Contains(applier, applyResult.Output);
        Assert.Contains("\"status\": \"applied\"", File.ReadAllText(manifestPath));

        // Apply persists: re-open the saved project from disk and check the gloss + one applied-log entry.
        AssertGlossOnDisk(senseGuid, wsTag, newGloss);
        AssertAppliedLogEntryCount(1);

        // --- log ---
        var logResult = Commands.Log(_fwDataPath);
        Assert.Equal(0, logResult.ExitCode);
        Assert.Contains(applier, logResult.Output);
        Assert.Contains("1 Motif entry", logResult.Output);

        // --- apply again: idempotent, no duplicate log entry, no re-mutation ---
        var secondApplyResult = Commands.Apply(_storeDir, proposalId, _fwDataPath, applier);
        Assert.Equal(0, secondApplyResult.ExitCode);
        Assert.Contains("already applied", secondApplyResult.Output, StringComparison.OrdinalIgnoreCase);

        AssertGlossOnDisk(senseGuid, wsTag, newGloss);
        AssertAppliedLogEntryCount(1);
    }

    [Fact]
    public void UnknownCommands_And_MissingArguments_ReturnNonZeroWithClearErrors()
    {
        var missingDraft = Commands.AddSetGloss(_storeDir, "does-not-exist", "agent_AAECAwQFBgcICQoLDA0ODw", "en", "x");
        Assert.NotEqual(0, missingDraft.ExitCode);
        Assert.Contains("not found", missingDraft.Output, StringComparison.OrdinalIgnoreCase);

        var badTarget = Commands.New(_storeDir, "bad-target-draft", null);
        Assert.Equal(0, badTarget.ExitCode);
        var invalidTarget = Commands.AddSetGloss(_storeDir, "bad-target-draft", "not-a-canonical-id", "en", "x");
        Assert.NotEqual(0, invalidTarget.ExitCode);
        Assert.Contains("not a valid canonical id", invalidTarget.Output);

        var emptyFinalize = Commands.Finalize(_storeDir, "bad-target-draft");
        Assert.NotEqual(0, emptyFinalize.ExitCode);
        Assert.Contains("no operations", emptyFinalize.Output);

        var missingProposal = Commands.Show(_storeDir, "agent_AAECAwQFBgcICQoLDA0ODw");
        Assert.NotEqual(0, missingProposal.ExitCode);
        Assert.Contains("not found", missingProposal.Output, StringComparison.OrdinalIgnoreCase);

        var missingProject = Commands.Log(Path.Combine(_tempRoot, "does-not-exist.fwdata"));
        Assert.NotEqual(0, missingProject.ExitCode);
        Assert.Contains("not found", missingProject.Output, StringComparison.OrdinalIgnoreCase);
    }

    private (Guid SenseGuid, string WsTag, string Gloss) FindSenseWithKnownGloss()
    {
        using var cache = new FwDataProjectLoader().LoadCache(_fwDataPath);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();

        foreach (var sense in senseRepo.AllInstances())
        {
            foreach (var wsHandle in sense.Gloss.AvailableWritingSystemIds)
            {
                var text = sense.Gloss.get_String(wsHandle).Text;
                if (!string.IsNullOrEmpty(text))
                {
                    var wsTag = cache.WritingSystemFactory.GetStrFromWs(wsHandle);
                    return (sense.Guid, wsTag, text);
                }
            }
        }

        throw new InvalidOperationException(
            "Expected the real TestLangProj fixture to contain at least one LexSense with a non-empty gloss.");
    }

    private void AssertGlossOnDisk(Guid senseGuid, string wsTag, string expectedGloss)
    {
        using var cache = new FwDataProjectLoader().LoadCache(_fwDataPath);
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(wsTag);
        var senseRepo = cache.ServiceLocator.GetInstance<ILexSenseRepository>();
        Assert.Equal(expectedGloss, senseRepo.GetObject(senseGuid).Gloss.get_String(wsHandle).Text);
    }

    private void AssertAppliedLogEntryCount(int expectedCount)
    {
        using var cache = new FwDataProjectLoader().LoadCache(_fwDataPath);
        Assert.Equal(expectedCount, ProjectAppliedLog.ReadAll(cache).Count);
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
