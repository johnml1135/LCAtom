using System;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Generator;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

[Collection(LcmCacheTestCollection.Name)]
public sealed class AnalysisCommandDispatchTests : IDisposable
{
    private readonly string _fwDataPath;
    private readonly string _storeDir;

    public AnalysisCommandDispatchTests(PristineProjectFixture pristine)
    {
        using var scratch = pristine.NewScratch();
        _fwDataPath = scratch.ProjectId.Path;
        _storeDir = Path.Combine(
            Path.GetTempPath(), "motif-analysis-dispatch-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnalysesJsonFlagDispatchesToStructuredOutputAndRecordsUsageShape()
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = $"analyses --project \"{_fwDataPath}\" --store \"{_storeDir}\" --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("\"assessmentState\"", output);
        var usagePath = Path.Combine(_storeDir, "usage.jsonl");
        var entry = Assert.Single(UsageLogFile.ReadAll(usagePath));
        Assert.Equal("analyses", entry.Command);
        Assert.Equal(new[] { "fwDataPath:text" }, entry.ArgumentShape);
        Assert.DoesNotContain(_fwDataPath, File.ReadAllText(usagePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AssessmentFlagsDispatchToStoredAssessmentAndRecordOnlyShapes()
    {
        var assessment = new StoredAssessment(
            new AssessReport(
                Array.Empty<AssessedWord>(), "outcome", "semantic", "sha256:grammar", "model", "pipeline", 0),
            CorpusDescriptor.Create("dispatch-corpus", Array.Empty<string>()));
        var assessmentId = new SqliteAssessmentStore(Path.Combine(_storeDir, "motif.db")).Save(assessment);

        var result = Run(
            $"analyses --project \"{_fwDataPath}\" --assessment \"{assessmentId}\" " +
            $"--current-corpus-sha256 \"{assessment.Corpus.Sha256}\" " +
            $"--current-grammar-sha256 \"{assessment.Report.GrammarSourceSha256}\" " +
            $"--store \"{_storeDir}\" --json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("still describes the current project", result.Output);
        var entry = Assert.Single(UsageLogFile.ReadAll(Path.Combine(_storeDir, "usage.jsonl")));
        Assert.Equal(
            new[]
            {
                "fwDataPath:text",
                "assessmentId:text",
                "currentCorpusSha256:text",
                "currentGrammarSourceSha256:text",
            },
            entry.ArgumentShape);
        var persistedUsage = File.ReadAllText(Path.Combine(_storeDir, "usage.jsonl"));
        Assert.DoesNotContain(assessmentId, persistedUsage, StringComparison.Ordinal);
        Assert.DoesNotContain(assessment.Corpus.Sha256, persistedUsage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--assessment assessment-only")]
    [InlineData("--assessment assessment --current-corpus-sha256 corpus-only")]
    [InlineData("--current-corpus-sha256 corpus --current-grammar-sha256 grammar")]
    public void PartialAssessmentFlagGroupReturnsUsage(string partialFlags)
    {
        var result = Run($"analyses --project \"{_fwDataPath}\" {partialFlags} --store \"{_storeDir}\"");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--assessment", result.Error);
        Assert.Contains("--current-corpus-sha256", result.Error);
        Assert.Contains("--current-grammar-sha256", result.Error);
    }

    private static (int ExitCode, string Output, string Error) Run(string arguments)
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
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
