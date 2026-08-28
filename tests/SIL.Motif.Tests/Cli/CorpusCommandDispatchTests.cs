using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Cli;
using SIL.Motif.Generator;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Cli;

public sealed class CorpusCommandDispatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "motif-corpus-dispatch-tests", Guid.NewGuid().ToString("N"));
    private readonly string _fwDataPath;
    private readonly string _workerRoot;

    public CorpusCommandDispatchTests()
    {
        Directory.CreateDirectory(_root);
        _fwDataPath = Path.Combine(_root, "Project.fwdata");
        File.WriteAllText(_fwDataPath, string.Empty);
        _workerRoot = Path.Combine(_root, "worker-root");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
        var entry = Assert.Single(ReadUsage());
        Assert.Equal("corpora", entry.Command);
        Assert.Equal(new[] { "fwDataPath:text" }, entry.ArgumentShape);
    }

    [Fact]
    public void ShowCorpusJsonFlagDispatchesToStructuredOutputAndRecordsOnlyArgumentShape()
    {
        const string corpusId = "dispatch-corpus";
        const string description = "Private dispatch corpus";
        var add = CorpusCommands.AddCorpus(
            _fwDataPath, "1.0", corpusId, description, "https://private.test/corpus", "private-licence",
            LicenceCapabilities.Unknown(), "test-tokeniser", "1", "private notes");
        Assert.Equal(0, add.ExitCode);
        var sourcePath = Path.Combine(_root, "private-source.txt");
        File.WriteAllText(sourcePath, "private document text");
        Assert.Equal(
            0,
            CorpusCommands.AddDocument(
                _fwDataPath, "1.0", corpusId, "private-document", sourcePath, "Private document", null, null)
                .ExitCode);

        var (exitCode, output, error) = Run($"show-corpus {corpusId}");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains($"\"corpusId\": \"{corpusId}\"", output);
        Assert.Contains($"\"description\": \"{description}\"", output);
        Assert.DoesNotContain("private document text", output);

        var entry = Assert.Single(ReadUsage());
        Assert.Equal("show-corpus", entry.Command);
        Assert.Equal(new[] { "fwDataPath:text", "corpusId:text" }, entry.ArgumentShape);
    }

    /// <summary>Reads back what the spawned CLI recorded into this test's isolated machine store.</summary>
    private IReadOnlyList<UsageLogEntry> ReadUsage()
    {
        using var machine = MachineDatabase.Open(_workerRoot);
        return new MachineUsageLog(machine).ReadAll();
    }

    private (int ExitCode, string Output, string Error) Run(string command)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = $"{command} --project \"{_fwDataPath}\" --json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment[RunnerOptions.RootVariable] = _workerRoot;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
