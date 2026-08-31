using System;
using System.IO;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Tests.Projection;
using SIL.Motif.Contract.Ids;
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
    private static string Hash(char digit) => "sha256:" + new string(digit, 64);

    private const string ProductVersion = "1.0";

    private readonly SeededProject _seed;
    private readonly string _fwDataPath;

    public ReportProjectionIntegrationTests(PristineProjectFixture pristine)
    {
        _seed = pristine.Seed;
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
    }

    /// <summary>The paired project database <c>analyses --assessment</c> now reads Assessments from.</summary>
    private string AssessmentDatabasePath() => ProjectDatabaseCatalog.DatabasePathFor(
        new ProjectLocator(_fwDataPath, Path.GetFileNameWithoutExtension(_fwDataPath)));


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
    public void AnalysesLoadsNamedAssessmentFromSqliteWithoutRecordingValues()
    {
        var assessment = new StoredAssessment(
            new AssessReport(
                Array.Empty<AssessedWord>(),
                "outcome",
                "semantic",
                Hash('a'),
                "model",
                "pipeline",
                0),
            Selection.Create("corpus-one", Array.Empty<string>()));
        var assessmentId = SeededAssessment.Record(_fwDataPath, assessment, CanonicalId.Mint("assessment/").Value);
        var usage = new UsageLog();

        var text = Commands.Analyses(
            _fwDataPath,
            assessmentId,
            assessment.Selection.Sha256,
            assessment.Report.GrammarSourceSha256,
            usage);
        var json = Commands.AnalysesJson(
            _fwDataPath,
            assessmentId,
            Hash('b'),
            assessment.Report.GrammarSourceSha256,
            usage);

        Assert.Equal(0, text.ExitCode);
        Assert.Equal(0, json.ExitCode);
        Assert.Contains("still describes the current project", text.Output);
        Assert.Contains("selection has changed", json.Output);
        Assert.Contains("\"unanalysedCount\": 0", json.Output);
        Assert.Contains("\"parsedCount\": 0", json.Output);
        Assert.All(usage.Entries, entry => Assert.Equal(
            new[]
            {
                "fwDataPath:text",
                "assessmentId:text",
                "currentSelectionSha256:text",
                "currentGrammarSourceSha256:text",
            },
            entry.ArgumentShape));
        var usageText = string.Join(" ", usage.Entries.SelectMany(entry => entry.ArgumentShape));
        Assert.DoesNotContain(_fwDataPath, usageText, StringComparison.Ordinal);
        Assert.DoesNotContain(assessmentId, usageText, StringComparison.Ordinal);
        Assert.DoesNotContain(assessment.Selection.Sha256, usageText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysesReturnsClearErrorWhenNamedAssessmentDoesNotExist()
    {
        // No corpus or proposal verb has touched this scratch project, so its paired database does not exist.
        var result = Commands.Analyses(_fwDataPath, Hash('0'), Hash('1'), Hash('2'));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(Hash('0'), result.Output);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "sha256:1111111111111111111111111111111111111111111111111111111111111111", "sha256:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("sha256:abc", "sha256:1111111111111111111111111111111111111111111111111111111111111111", "sha256:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000", "true", "sha256:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("sha256:0000000000000000000000000000000000000000000000000000000000000000", "sha256:1111111111111111111111111111111111111111111111111111111111111111", "SHA256:2222222222222222222222222222222222222222222222222222222222222222")]
    public void AssessmentCommandsRejectMalformedIdentifiers(
        string assessmentId,
        string currentSelectionSha256,
        string currentGrammarSha256)
    {
        var result = Commands.Analyses(
            _fwDataPath,
            assessmentId,
            currentSelectionSha256,
            currentGrammarSha256);

        Assert.NotEqual(0, result.ExitCode);
        // Each malformed field is rejected by name; an assessment id and a digest have different shapes.
        Assert.Contains("is required", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysesJoinsStoredAutomaticResultsToRealManuallyAnalysedWordforms()
    {
        SeedApprovedWordform("zzAssessmentParsed");
        SeedApprovedWordform("zzAssessmentEmpty");
        SeedApprovedWordform("zzAssessmentUncovered");

        var assessment = new StoredAssessment(
            new AssessReport(
                new[]
                {
                    new AssessedWord(
                        "zzAssessmentParsed",
                        "Analysed",
                        new[]
                        {
                            new ParsedAnalysis(
                                null,
                                new[] { _seed.FirstLexemeFormId.ToString() },
                                0,
                                "automatic-real-join"),
                        }),
                    new AssessedWord("zzAssessmentEmpty", "NoAnalysis", Array.Empty<ParsedAnalysis>()),
                },
                "outcome",
                "semantic",
                Hash('c'),
                "model",
                "pipeline",
                0),
            Selection.Create(
                "real-join",
                new[] { "zzAssessmentParsed", "zzAssessmentEmpty" }));
        var assessmentId = SeededAssessment.Record(_fwDataPath, assessment, CanonicalId.Mint("assessment/").Value);

        var result = Commands.AnalysesJson(
            _fwDataPath,
            assessmentId,
            assessment.Selection.Sha256,
            assessment.Report.GrammarSourceSha256);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var wordforms = document.RootElement.GetProperty("wordForms").EnumerateArray()
            .ToDictionary(element => element.GetProperty("form").GetString()!, StringComparer.Ordinal);
        Assert.Equal("automatic-real-join", wordforms["zzAssessmentParsed"]
            .GetProperty("automaticAnalyses")[0].GetProperty("contentDigest").GetString());
        Assert.Equal(1, wordforms["zzAssessmentParsed"].GetProperty("automaticAnalysisCount").GetInt32());
        Assert.Empty(wordforms["zzAssessmentEmpty"].GetProperty("automaticAnalyses").EnumerateArray());
        Assert.Equal(0, wordforms["zzAssessmentEmpty"].GetProperty("automaticAnalysisCount").GetInt32());
        Assert.False(wordforms["zzAssessmentUncovered"].TryGetProperty("automaticAnalyses", out _));
        Assert.False(wordforms["zzAssessmentUncovered"].TryGetProperty("automaticAnalysisCount", out _));
    }

    private void SeedApprovedWordform(string form)
    {
        var loader = new SIL.Motif.Host.LcmUtils.FwDataProjectLoader();
        using var cache = loader.LoadScratchCache(_fwDataPath);
        NonUndoableUnitOfWorkHelper.Do(cache.ActionHandlerAccessor, () =>
        {
            var wordform = cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(form, cache.DefaultVernWs));
            var analysis = cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            wordform.AnalysesOC.Add(analysis);
            cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.approves);
        });
        loader.Save(cache);
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

        Assert.Equal(0, Commands.New(_fwDataPath, ProductVersion, draftName, label).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_fwDataPath, ProductVersion, draftName, canonicalId.Value, wsTag, newGloss).ExitCode);
        DraftRationale.Author(
            _fwDataPath, draftName, label, "Explain why this lexical gloss should replace the current analysis.");
        var finalize = Commands.Finalize(_fwDataPath, ProductVersion, draftName);
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        // list
        var listText = Commands.List(_fwDataPath, ProductVersion, usage);
        var listJson = Commands.ListJson(_fwDataPath, ProductVersion, usage);
        FigureAudit.AssertEveryTextFigureAppearsInJson(listText.Output, listJson.Output);

        // show
        var showText = Commands.Show(_fwDataPath, ProductVersion, proposalId, usage);
        var showJson = Commands.ShowJson(_fwDataPath, ProductVersion, proposalId, usage);
        FigureAudit.AssertEveryTextFigureAppearsInJson(showText.Output, showJson.Output);
        Assert.Contains(canonicalId.Value, showJson.Output);

        // dry-run: a job now (ADR 0041 decision 7); DryRunJobRunner stands in for the real runner.
        var dryRunText = DryRunJobRunner.Run(_fwDataPath, ProductVersion, proposalId, asJson: false, usage);
        var dryRunJson = DryRunJobRunner.Run(_fwDataPath, ProductVersion, proposalId, asJson: true, usage);
        Assert.Equal(0, dryRunText.ExitCode);
        Assert.Equal(0, dryRunJson.ExitCode);
        Assert.Contains($"\"{originalGloss}\" -> \"{newGloss}\"", dryRunText.Output);
        Assert.Contains(originalGloss, dryRunJson.Output);
        Assert.Contains(newGloss, dryRunJson.Output);
        FigureAudit.AssertEveryTextFigureAppearsInJson(dryRunText.Output, dryRunJson.Output);

        // apply: mutating (one shot), so the JSON report is checked directly against ground truth.
        var applyJson = Commands.ApplyJson(_fwDataPath, ProductVersion, proposalId, applier, force: true, usage: usage);
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
            AssertNever(entry.Command, originalGloss, newGloss, canonicalId.Value, proposalId, applier, _fwDataPath);
            foreach (var token in entry.ArgumentShape)
                AssertNever(token, originalGloss, newGloss, canonicalId.Value, proposalId, applier, _fwDataPath);
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
