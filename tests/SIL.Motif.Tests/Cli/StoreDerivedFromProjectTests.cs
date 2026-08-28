using System;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Generator;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// The defect ADR 0041 decision 2 exists to close: <c>--store</c> defaulted to <c>./.motif</c>, so a
/// Proposal's address was the working directory a command happened to run from rather than the
/// project it was about. These pin that the paired database is derived from <c>--project</c> alone.
/// </summary>
public sealed class StoreDerivedFromProjectTests : IDisposable
{
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), "motif-store-location-" + Guid.NewGuid().ToString("N"));
    private readonly string _fwDataPath;
    private readonly string _cwdA;
    private readonly string _cwdB;

    public StoreDerivedFromProjectTests()
    {
        Directory.CreateDirectory(_projectDir);
        _fwDataPath = Path.Combine(_projectDir, "Project.fwdata");
        File.WriteAllText(_fwDataPath, string.Empty);

        _cwdA = Path.Combine(_projectDir, "cwd-a");
        _cwdB = Path.Combine(_projectDir, "cwd-b");
        Directory.CreateDirectory(_cwdA);
        Directory.CreateDirectory(_cwdB);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void DatabasePathFor_ResolvesTheSamePath_RegardlessOfTheCallersWorkingDirectory()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_cwdA);
            var fromCwdA = ProjectDatabaseCatalog.DatabasePathFor(new ProjectLocator(_fwDataPath, "Project"));

            Directory.SetCurrentDirectory(_cwdB);
            var fromCwdB = ProjectDatabaseCatalog.DatabasePathFor(new ProjectLocator(_fwDataPath, "Project"));

            Assert.Equal(fromCwdA, fromCwdB);
            Assert.Equal(Path.Combine(_projectDir, "Project.motif.db"), fromCwdA);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void AProposalCommittedFromOneWorkingDirectory_IsVisibleFromAnother()
    {
        var target = CanonicalId.Mint().Value;

        Assert.Equal(
            0,
            RunCli(_cwdA, $"new --project \"{_fwDataPath}\" --draft d --label \"a label\"").ExitCode);
        Assert.Equal(
            0,
            RunCli(
                _cwdB,
                $"add-set-gloss --project \"{_fwDataPath}\" --draft d --target {target} --ws en --text hi")
                .ExitCode);
        Assert.Equal(
            0,
            RunCli(_cwdA, $"label --project \"{_fwDataPath}\" --draft d \"a short description\"").ExitCode);
        Assert.Equal(
            0,
            RunCli(_cwdB, $"comment --project \"{_fwDataPath}\" --draft d \"an extended explanation\"").ExitCode);

        var finalize = RunCli(_cwdA, $"finalize --project \"{_fwDataPath}\" --draft d");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        var listFromB = RunCli(_cwdB, $"list --project \"{_fwDataPath}\"");
        Assert.Equal(0, listFromB.ExitCode);
        Assert.Contains(proposalId, listFromB.Output);

        var showFromB = RunCli(_cwdB, $"show --project \"{_fwDataPath}\" {proposalId}");
        Assert.Equal(0, showFromB.ExitCode);
        Assert.Contains(proposalId, showFromB.Output);

        // The Proposal lives beside the project, not in either working directory a command ran from.
        var databasePath = ProjectDatabaseCatalog.DatabasePathFor(new ProjectLocator(_fwDataPath, "Project"));
        Assert.True(File.Exists(databasePath));
    }

    /// <summary>
    /// The break the ADR 0041 amendment records: <c>add-corpus</c> writing a corpus keyed by the
    /// working directory while <c>promote-gloss</c> reads the corpus keyed by the project would make a
    /// corpus invisible the moment the two commands run from different directories. Both are keyed by
    /// <c>--project</c> alone, so the corpus is visible from anywhere.
    /// </summary>
    [Fact]
    public void ACorpusAddedFromOneWorkingDirectory_IsVisibleToPromoteGlossFromAnother()
    {
        var target = CanonicalId.Mint().Value;

        var addCorpus = RunCli(
            _cwdA,
            $"add-corpus --project \"{_fwDataPath}\" --id wiki-testlang --description \"Testlang dump\" " +
            "--tokeniser whitespace-and-punctuation --tokeniser-version 1");
        Assert.Equal(0, addCorpus.ExitCode);

        Assert.Equal(0, RunCli(_cwdB, $"new --project \"{_fwDataPath}\" --draft d").ExitCode);

        var promote = RunCli(
            _cwdB,
            $"promote-gloss --project \"{_fwDataPath}\" --draft d --target {target} --ws en --text hello " +
            "--corpus wiki-testlang");

        Assert.Equal(0, promote.ExitCode);
        Assert.Contains("promoted from corpus 'wiki-testlang'", promote.Output);
    }

    private static string ExtractProposalId(string output)
    {
        const string marker = "-> Proposal ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from output: {output}");
        return output.Substring(start, end - start);
    }

    private static (int ExitCode, string Output, string Error) RunCli(string workingDirectory, string arguments)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "The CLI did not exit within its bound.");
        return (process.ExitCode, output, error);
    }
}
