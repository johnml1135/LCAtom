using System;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Generator;
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
}
