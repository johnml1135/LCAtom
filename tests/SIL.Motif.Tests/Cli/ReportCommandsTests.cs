using System;
using System.IO;
using System.Linq;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Drives the real <c>report</c> verb end to end against a paired store, with no <c>.fwdata</c> content
/// and no Assessor anywhere in the process — proving the registry, the refusal-naming-a-reason contract,
/// and that a stored rendering survives being read back with nothing that could produce it available.
/// </summary>
public sealed class ReportCommandsTests : IDisposable
{
    private const string ProductVersion = "1.0";
    private const string GrammarSha = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-report-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public ReportCommandsTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "sample.fwdata");
        File.WriteAllText(_project, "<languageproject/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ListKinds_ListsCoverageAndCorrectness()
    {
        var result = ReportCommands.ListKinds(asJson: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("coverage", result.Output, StringComparison.Ordinal);
        Assert.Contains("correctness", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForAKindOutsideTheRegistry_Refuses()
    {
        var assessmentId = RecordAssessment("ParseTime", ("a", true), ("b", false));

        var result = ReportCommands.Produce(_project, ProductVersion, assessmentId, "nonsense-kind", null, null, false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("nonsense-kind", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorrectnessReportOverAParseTimeAssessment_RefusesNamingTheReason()
    {
        var assessmentId = RecordAssessment("ParseTime", ("a", true), ("b", false));

        var result = ReportCommands.Produce(_project, ProductVersion, assessmentId, "correctness", null, null, false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ParseTime", result.Output, StringComparison.Ordinal);
        Assert.Contains("did not collect", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACoverageReport_IsComputedStoredAndReadableWithNoAssessorAnywhereInTheProcess()
    {
        var assessmentId = RecordAssessment("ParseTime", ("motifa", true), ("motifb", false));

        var jsonResult = ReportCommands.Produce(_project, ProductVersion, assessmentId, "coverage", null, null, true);
        Assert.Equal(0, jsonResult.ExitCode);
        var response = ProjectionJson.Deserialize<ReportResponse>(jsonResult.Output)!;
        Assert.Equal("coverage", response.Kind);
        Assert.Contains("50.0%", response.Text, StringComparison.Ordinal);

        // Read the stored row back through a fresh handle, bypassing ReportCommands and any Assessor.
        var stored = ReadStoredReport(response.ReportId);
        Assert.NotNull(stored);
        Assert.Equal("coverage", stored!.Kind);
        Assert.Contains("50.0%", stored.RenderedText, StringComparison.Ordinal);
    }

    private string RecordAssessment(string kind, params (string Word, bool Analysed)[] words)
    {
        var corpus = CorpusDescriptor.Create("test", words.Select(w => w.Word));
        var assessmentId = CanonicalId.Mint("assessment/").Value;
        var assessedWords = words
            .Select(w => new AssessedWord(
                w.Word, w.Analysed ? "analysed" : "no-analysis",
                w.Analysed
                    ? new[] { new ParsedAnalysis(null, Array.Empty<string>(), 0, "digest") }
                    : Array.Empty<ParsedAnalysis>()))
            .ToArray();

        var result = ProjectStoreCommand.Run(_project, ProductVersion, (database, _) =>
        {
            new AssessmentRepository(database).Record(new NewAssessmentRecord(
                AssessmentId: assessmentId,
                ProposalId: null,
                ProposalIntentDigest: null,
                Assessor: "pangloss",
                Kind: kind,
                ScopeJson: """{"words":[],"engine":"fast","collect":[],"perWordLimitMs":1000}""",
                ScopeDigest: "sha256:" + new string('a', 64),
                TokeniserName: "none",
                TokeniserVersion: "1",
                BaselineToken: "{}",
                Corpus: corpus,
                OutcomeDigest: "sha256:" + new string('b', 64),
                SemanticDigest: "sha256:" + new string('c', 64),
                GrammarSourceSha256: GrammarSha,
                ModelFingerprint: "model",
                Pipeline: "pipeline",
                DiagnosticCount: 0,
                Words: assessedWords));
            return new CommandResult(0, string.Empty);
        });
        Assert.Equal(0, result.ExitCode);
        return assessmentId;
    }

    private ReportRecord? ReadStoredReport(string reportId)
    {
        ReportRecord? stored = null;
        var result = ProjectStoreCommand.Run(_project, ProductVersion, (database, _) =>
        {
            stored = new ReportRepository(database).Get(reportId);
            return new CommandResult(0, string.Empty);
        });
        Assert.Equal(0, result.ExitCode);
        return stored;
    }
}
