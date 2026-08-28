using System;
using System.Diagnostics;
using System.IO;
using SIL.Motif.Generator;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Every invocation that names a project upserts it into the machine store's <c>KnownProjects</c> on the
/// way past (ADR 0041 decision 4) — there is no separate <c>motif register</c> verb.
/// </summary>
public sealed class KnownProjectRegistrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-known-project-tests", Guid.NewGuid().ToString("N"));
    private readonly string _fwDataPath;
    private readonly string _workerRoot;

    public KnownProjectRegistrationTests()
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
    public void AnUnusableMachineStoreWarnsRatherThanFailingTheVerbOrGoingQuiet()
    {
        // A directory where the machine database belongs: registration cannot succeed.
        Directory.CreateDirectory(Path.Combine(_workerRoot, "motif.db"));

        var result = Run($"new --project \"{_fwDataPath}\" --draft d");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("could not be recorded for background work", result.Error, StringComparison.Ordinal);
        // The sweep is what would have run this project's jobs, so the warning names that consequence.
        Assert.Contains("will not run until it is", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningAProjectVerbRecordsTheProjectInKnownProjects()
    {
        var result = Run($"new --project \"{_fwDataPath}\" --draft d");
        Assert.Equal(0, result.ExitCode);

        using var machine = MachineDatabase.Open(_workerRoot);
        var recorded = Assert.Single(new KnownProjectRegistry(machine).List());
        Assert.Equal(_fwDataPath, recorded.FullFwDataPath, ignoreCase: true);
    }

    [Fact]
    public void RunningTheSameProjectTwiceUpdatesLastSeenRatherThanDuplicating()
    {
        Assert.Equal(0, Run($"new --project \"{_fwDataPath}\" --draft d").ExitCode);
        Assert.Equal(0, Run($"list --project \"{_fwDataPath}\"").ExitCode);

        using var machine = MachineDatabase.Open(_workerRoot);
        Assert.Single(new KnownProjectRegistry(machine).List());
    }

    private (int ExitCode, string Output, string Error) Run(string arguments)
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
        start.Environment[RunnerOptions.RootVariable] = _workerRoot;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
