using System;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Generator;
using SIL.Motif.Projection.Usage;
using Xunit;

namespace SIL.Motif.Tests.Cli;

public sealed class CorpusCommandDispatchTests : IDisposable
{
    private readonly string _storeDir = Path.Combine(
        Path.GetTempPath(), "motif-corpus-dispatch-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storeDir)) Directory.Delete(_storeDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CorporaJsonFlagDispatchesToStructuredOutputAndRecordsUsage()
    {
        var (exitCode, output, error) = Run("corpora");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            "{" + Environment.NewLine + "  \"corpora\": []" + Environment.NewLine + "}" + Environment.NewLine,
            output);
        var entry = Assert.Single(UsageLogFile.ReadAll(Path.Combine(_storeDir, "usage.jsonl")));
        Assert.Equal("corpora", entry.Command);
        Assert.Equal(new[] { "storeDir:text" }, entry.ArgumentShape);
        Assert.DoesNotContain(_storeDir, File.ReadAllText(Path.Combine(_storeDir, "usage.jsonl")));
    }

    [Fact]
    public void ShowCorpusJsonFlagDispatchesToStructuredOutputAndRecordsOnlyArgumentShape()
    {
        const string corpusId = "dispatch-corpus";
        const string description = "Private dispatch corpus";
        var add = CorpusCommands.AddCorpus(
            _storeDir, corpusId, description, "https://private.test/corpus", "private-licence",
            LicenceCapabilities.Unknown(), "test-tokeniser", "1", "private notes");
        Assert.Equal(0, add.ExitCode);
        var sourcePath = Path.Combine(_storeDir, "private-source.txt");
        Directory.CreateDirectory(_storeDir);
        File.WriteAllText(sourcePath, "private document text");
        Assert.Equal(
            0,
            CorpusCommands.AddDocument(
                _storeDir, corpusId, "private-document", sourcePath, "Private document", null, null).ExitCode);

        var (exitCode, output, error) = Run($"show-corpus {corpusId}");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains($"\"corpusId\": \"{corpusId}\"", output);
        Assert.Contains($"\"description\": \"{description}\"", output);
        Assert.DoesNotContain("private document text", output);

        var usagePath = Path.Combine(_storeDir, "usage.jsonl");
        var entry = Assert.Single(UsageLogFile.ReadAll(usagePath));
        Assert.Equal("show-corpus", entry.Command);
        Assert.Equal(new[] { "storeDir:text", "corpusId:text" }, entry.ArgumentShape);
        var persistedUsage = File.ReadAllText(usagePath);
        Assert.DoesNotContain(_storeDir, persistedUsage);
        Assert.DoesNotContain(corpusId, persistedUsage);
        Assert.DoesNotContain(description, persistedUsage);
        Assert.DoesNotContain("private notes", persistedUsage);
        Assert.DoesNotContain("private document text", persistedUsage);
    }

    private (int ExitCode, string Output, string Error) Run(string command)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = $"{command} --store \"{_storeDir}\" --json",
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
