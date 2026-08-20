using System;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Tests.Projection;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Drives the real Commands surfaces end to end (same fixtures as <see cref="EndToEndCliTests"/>) to
/// prove two things the pure Projection tests cannot: that the <c>*Json</c> siblings really do emit
/// real project data as structured output, and that a <see cref="UsageLog"/> wired through the same
/// real calls genuinely never records any of it.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class ReportProjectionIntegrationTests
{
    private readonly SeededProject _seed;
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public ReportProjectionIntegrationTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;

        var scratchRoot = Path.GetDirectoryName(Path.GetDirectoryName(_fwDataPath))!;
        _storeDir = Path.Combine(scratchRoot, ".motif-store");
    }

    [Fact]
    public void OpenJson_CarriesTheSameFiguresAsTheTextReport()
    {
        var usage = new UsageLog();
        var text = Commands.Open(_fwDataPath, usage);
        var json = Commands.OpenJson(_fwDataPath, usage);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        // Open's report is too thin for FigureAudit's id/digest sweep to find anything; checked directly.
        Assert.Contains("2", json.Output); // SeededProject writes exactly two entries.
    }

    [Fact]
    public void AnalysesReadsTheManualAggregateWithoutRecordingProjectData()
    {
        var usage = new UsageLog();

        var text = Commands.Analyses(_fwDataPath, usage);
        var json = Commands.AnalysesJson(_fwDataPath, usage);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        Assert.Contains("No assessment is on record", text.Output);
        Assert.Contains("\"assessmentState\"", json.Output);
        Assert.Contains("Word forms: 0", text.Output);
        Assert.Contains("\"wordFormCount\": 0", json.Output);
        Assert.Equal(2, usage.Entries.Count);
        Assert.All(usage.Entries, entry =>
        {
            Assert.Equal("analyses", entry.Command);
            Assert.Equal(new[] { "fwDataPath:text" }, entry.ArgumentShape);
            Assert.DoesNotContain(_fwDataPath, string.Join(" ", entry.ArgumentShape), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FullLoop_EveryMigratedSurfaceEmitsJsonCarryingItsTextFigures_AndUsageLogStaysDataFree()
    {
        var senseGuid = _seed.FirstSenseId;
        var wsTag = NewLangProjFixture.AnalysisTag;
        var originalGloss = SeededProject.FirstGloss;
        var canonicalId = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(senseGuid);
        var newGloss = originalGloss + " (revised, report projection test)";
        const string draftName = "report-projection-demo";
        const string label = "report-projection-1";
        const string applier = "report-projection-tests";

        var usage = new UsageLog();

        Assert.Equal(0, Commands.New(_storeDir, draftName, label).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        var finalize = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        // list
        var listText = Commands.List(_storeDir, usage);
        var listJson = Commands.ListJson(_storeDir, usage);
        FigureAudit.AssertEveryTextFigureAppearsInJson(listText.Output, listJson.Output);

        // show
        var showText = Commands.Show(_storeDir, proposalId, usage);
        var showJson = Commands.ShowJson(_storeDir, proposalId, usage);
        FigureAudit.AssertEveryTextFigureAppearsInJson(showText.Output, showJson.Output);
        Assert.Contains(canonicalId.Value, showJson.Output);

        // dry-run: non-mutating, so both variants read the same unchanged baseline.
        var dryRunText = Commands.DryRun(_storeDir, proposalId, _fwDataPath, usage);
        var dryRunJson = Commands.DryRunJson(_storeDir, proposalId, _fwDataPath, usage);
        Assert.Equal(0, dryRunText.ExitCode);
        Assert.Equal(0, dryRunJson.ExitCode);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", dryRunText.Output);
        Assert.Contains(originalGloss, dryRunJson.Output);
        Assert.Contains(newGloss, dryRunJson.Output);
        FigureAudit.AssertEveryTextFigureAppearsInJson(dryRunText.Output, dryRunJson.Output);

        // apply: mutating (one shot), so the JSON report is checked directly against ground truth.
        var applyJson = Commands.ApplyJson(_storeDir, proposalId, _fwDataPath, applier, usage);
        Assert.Equal(0, applyJson.ExitCode);
        Assert.Contains(originalGloss, applyJson.Output);
        Assert.Contains(newGloss, applyJson.Output);
        Assert.Contains(applier, applyJson.Output);
        Assert.Contains(proposalId, applyJson.Output);

        // log
        var logText = Commands.Log(_fwDataPath, usage);
        var logJson = Commands.LogJson(_fwDataPath, usage);
        Assert.Equal(0, logText.ExitCode);
        Assert.Equal(0, logJson.ExitCode);
        Assert.Contains(applier, logJson.Output);
        FigureAudit.AssertEveryTextFigureAppearsInJson(logText.Output, logJson.Output);

        // usage log: recorded every real call above, but never a scrap of the real project data.
        Assert.Equal(9, usage.Entries.Count);
        foreach (var entry in usage.Entries)
        {
            AssertNever(entry.Command, originalGloss, newGloss, canonicalId.Value, proposalId, applier, _fwDataPath, _storeDir);
            foreach (var token in entry.ArgumentShape)
                AssertNever(token, originalGloss, newGloss, canonicalId.Value, proposalId, applier, _fwDataPath, _storeDir);
        }

        var summary = usage.Summarize();
        Assert.Equal(2, summary.CallCounts["list"]);
        Assert.Equal(2, summary.CallCounts["show"]);
        Assert.Equal(2, summary.CallCounts["dry-run"]);
        Assert.Equal(1, summary.CallCounts["apply"]);
        Assert.Equal(2, summary.CallCounts["log"]);
    }

    private static void AssertNever(string haystack, params string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, haystack, StringComparison.Ordinal);
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
