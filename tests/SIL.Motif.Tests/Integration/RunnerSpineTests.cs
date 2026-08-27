using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Generator;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Integration;

/// <summary>
/// One real <c>motif</c> process queues work, one real runner process does it, and a third process reads
/// the result — coordinating through nothing but the paired database.
/// </summary>
/// <remarks>
/// Every other suite covers a seam. This covers whether the product runs: two executables that have never
/// met, a job that has never been claimed, and a Baseline barrier that has never been constructed outside
/// a test. It is slow by the standards of this suite and cheap by the standards of what it replaces —
/// finding out after shipping.
/// </remarks>
[Collection(LcmCacheTestCollection.Name)]
public sealed class RunnerSpineTests : IDisposable
{
    private readonly PristineProjectFixture _projects;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-spine-" + Guid.NewGuid().ToString("N"));
    private readonly string _ownerNamespace = "motif-spine-" + Guid.NewGuid().ToString("N");
    private readonly List<Process> _started = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _log = new();

    public RunnerSpineTests(PristineProjectFixture projects)
    {
        _projects = projects;
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AQueuedJobIsClaimedAndCompletedByARealRunnerProcess()
    {
        var project = _projects.CopyProjectFile();

        var jobId = Cli($"baseline-refresh --project \"{project}\"").Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.Equal("queued", StatusOf(project, jobId));

        RunRunnerToCompletion(project);

        // The row moved because a different process moved it; nothing in this one touched the queue.
        Assert.NotEqual("queued", StatusOf(project, jobId));
        Assert.NotEqual("running", StatusOf(project, jobId));
    }

    [Fact]
    public void AKilledRunnerLeavesItsJobReclaimableRatherThanStranded()
    {
        var project = _projects.CopyProjectFile();
        var jobId = Cli($"baseline-refresh --project \"{project}\"").Output.Trim();

        // Killing before it claims would prove nothing, so wait until the row is genuinely held.
        var killed = StartRunner(project, leaseSeconds: 1);
        Assert.True(WaitUntilRunning(project, jobId),
            "The runner never claimed the job. Runner said: " + string.Join(" | ", _log));
        Kill(killed);
        Thread.Sleep(1500);

        RunRunnerToCompletion(project);

        var final = Show(project, jobId);
        Assert.NotEqual("queued", final.Status);
        Assert.NotEqual("running", final.Status);
        // Only ClaimNext taking an expired lease increments this, so it is what proves a reclaim.
        Assert.True(final.Attempt > 1, $"Expected a reclaimed attempt, saw attempt {final.Attempt}.");
    }

    private void RunRunnerToCompletion(string project)
    {
        var runner = StartRunner(project, leaseSeconds: 30);
        if (!runner.WaitForExit(30000))
        {
            try { runner.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail("The runner did not exit. Runner said: " + string.Join(" | ", _log));
        }
    }

    private Process StartRunner(string project, int leaseSeconds)
    {
        var executable = Path.Combine(RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Worker", "bin", "Debug",
            "net10.0", "SIL.Motif.Worker.exe");
        Assert.True(File.Exists(executable), "The runner was not built: " + executable);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--idle-ms");
        start.ArgumentList.Add("500");
        start.Environment[RunnerOptions.RootVariable] = _root;
        start.Environment[RunnerOptions.NamespaceVariable] = _ownerNamespace;
        start.Environment[RunnerOptions.LeaseVariable] = leaseSeconds.ToString();
        var process = Process.Start(start)!;
        _started.Add(process);
        // Drained on background threads: an unread pipe that fills would block the runner mid-job.
        _ = Task.Run(() => _log.Add("out: " + process.StandardOutput.ReadToEnd()));
        _ = Task.Run(() => _log.Add("err: " + process.StandardError.ReadToEnd()));
        return process;
    }

    private bool WaitUntilRunning(string project, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Show(project, jobId).Status == "running") return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private string StatusOf(string project, string jobId) => Show(project, jobId).Status;

    private JobView Show(string project, string jobId)
    {
        var shown = Cli($"jobs show {jobId} --project \"{project}\" --json");
        Assert.Equal(0, shown.ExitCode);
        using var document = JsonDocument.Parse(shown.Output);
        return new JobView(document.RootElement.GetProperty("status").GetString()!,
            document.RootElement.GetProperty("attempt").GetInt32());
    }

    private sealed record JobView(string Status, int Attempt);

    private static bool Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); process.WaitForExit(10000); }
        catch (InvalidOperationException) { }
        return true;
    }

    private static CliRun Cli(string arguments)
    {
        var executable = Path.Combine(RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug",
            "net10.0", "motif.exe");
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
        Assert.True(process.WaitForExit(120000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
