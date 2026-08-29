using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Cli;
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

        RunRunnerToCompletion();

        // The row moved because a different process moved it; nothing in this one touched the queue.
        Assert.NotEqual("queued", StatusOf(project, jobId));
        Assert.NotEqual("running", StatusOf(project, jobId));
        // A completed refresh that published nothing is the failure a job status cannot show.
        Assert.True(PublishedBaselineCount(project) == 1,
            $"Expected one published Baseline, found {PublishedBaselineCount(project)}. " +
            $"Job: {StatusOf(project, jobId)} — {ResultOf(project, jobId)}");
    }

    /// <summary>
    /// The reason Task 8 exists: one runner started with no project of its own must sweep every project
    /// the CLI has pointed it at, not just the first one asked about. Two Known projects, one job queued
    /// in each, one runner — both must reach terminal.
    /// </summary>
    [Fact]
    public void TwoQueuedJobsInTwoDifferentProjectsBothReachTerminalThroughOneRunnerSweep()
    {
        var first = _projects.CopyProjectFile();
        var second = _projects.CopyProjectFile();

        // Registered in this order, but nothing here says the sweep must visit them in this order.
        var secondJobId = Cli($"baseline-refresh --project \"{second}\"").Output.Trim();
        var firstJobId = Cli($"baseline-refresh --project \"{first}\"").Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(firstJobId));
        Assert.False(string.IsNullOrWhiteSpace(secondJobId));

        RunRunnerToCompletion();

        Assert.NotEqual("queued", StatusOf(first, firstJobId));
        Assert.NotEqual("running", StatusOf(first, firstJobId));
        Assert.NotEqual("queued", StatusOf(second, secondJobId));
        Assert.NotEqual("running", StatusOf(second, secondJobId));
        Assert.True(PublishedBaselineCount(first) == 1,
            $"Project 1 expected one published Baseline, found {PublishedBaselineCount(first)}. " +
            $"Job: {StatusOf(first, firstJobId)} — {ResultOf(first, firstJobId)}");
        Assert.True(PublishedBaselineCount(second) == 1,
            $"Project 2 expected one published Baseline, found {PublishedBaselineCount(second)}. " +
            $"Job: {StatusOf(second, secondJobId)} — {ResultOf(second, secondJobId)}");
    }

    /// The job's own record of why it failed; no verb surfaces ResultJson yet.
    private static string ResultOf(string project, string jobId) =>
        Scalar(project, "SELECT ResultJson FROM Jobs WHERE JobId = '" + jobId + "';")?.ToString() ?? "(none)";

    /// Reads the Baselines table directly: no verb reports it yet, and the job status cannot.
    private static int PublishedBaselineCount(string project)
    {
        var database = Path.Combine(Path.GetDirectoryName(project)!,
            Path.GetFileNameWithoutExtension(project) + ".motif.db");
        if (!File.Exists(database)) return 0;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Baselines;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static object? Scalar(string project, string sql)
    {
        var database = Path.Combine(Path.GetDirectoryName(project)!,
            Path.GetFileNameWithoutExtension(project) + ".motif.db");
        if (!File.Exists(database)) return null;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void AKilledRunnerLeavesItsJobReclaimableRatherThanStranded()
    {
        var project = _projects.CopyProjectFile();
        var jobId = Cli($"baseline-refresh --project \"{project}\"").Output.Trim();

        // Killing before it claims would prove nothing, so wait until the row is genuinely held.
        var killed = StartRunner(leaseSeconds: 1);
        Assert.True(WaitUntilRunning(project, jobId),
            "The runner never claimed the job. Runner said: " + string.Join(" | ", _log));
        Kill(killed);
        Thread.Sleep(1500);

        RunRunnerToCompletion();

        var final = Show(project, jobId);
        Assert.NotEqual("queued", final.Status);
        Assert.NotEqual("running", final.Status);
        // Only ClaimNext taking an expired lease increments this, so it is what proves a reclaim.
        Assert.True(final.Attempt > 1, $"Expected a reclaimed attempt, saw attempt {final.Attempt}.");
    }

    /// <summary>
    /// ADR 0041 decision 7's "must not simply fail" for a Dry Run job against a project with no
    /// published Baseline, driven by the real runner rather than a direct handler call — and Task 8's
    /// two inherited findings closed together: parking asks for the Baseline it needs (a
    /// <c>baseline-refresh</c> the sweep enqueues on its own), and once that publishes, the parked row
    /// is claimable again and runs to completion. The runner is awaited to exit rather than killed,
    /// because a project with a permanently parked row would be the one thing that never lets it.
    /// </summary>
    [Fact]
    public void ADryRunJobAgainstAProjectWithNoPublishedBaselineParksThenRunsOnceTheSweepRefreshesTheBaseline()
    {
        var project = _projects.CopyProjectFile();
        var target = SIL.Motif.Contract.Ids.CanonicalId.FromGuid(_projects.Seed.FirstSenseId).Value;
        Assert.Equal(0, Cli($"new --project \"{project}\" --draft d").ExitCode);
        Assert.Equal(0, Cli(
            $"add-set-gloss --project \"{project}\" --draft d --target {target} --ws en --text hello").ExitCode);
        Assert.Equal(0, Cli($"label --project \"{project}\" --draft d \"a label\"").ExitCode);
        Assert.Equal(0, Cli($"comment --project \"{project}\" --draft d \"a comment\"").ExitCode);
        var finalized = Cli($"finalize --project \"{project}\" --draft d");
        Assert.Equal(0, finalized.ExitCode);
        var proposalId = ExtractProposalId(finalized.Output);

        var jobId = Cli($"dry-run --project \"{project}\" {proposalId}").Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.Equal("queued", StatusOf(project, jobId));

        var runner = StartRunner(leaseSeconds: 30);
        var sawWaitingForBaseline = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string status;
        do
        {
            status = StatusOf(project, jobId);
            if (status == "waiting-for-baseline") sawWaitingForBaseline = true;
            if (status is "completed-dry-run-only" or "failed" or "cancelled") break;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);

        Assert.True(sawWaitingForBaseline, "The job never parked at waiting-for-baseline.");
        Assert.Equal("completed-dry-run-only", status);

        // No permanently parked row remains, so the runner idles out and exits on its own — never killed.
        Assert.True(runner.WaitForExit(30000),
            "The runner did not exit on its own. Runner said: " + string.Join(" | ", _log));
    }

    private static string ExtractProposalId(string finalizeOutput)
    {
        const string marker = "-> Proposal ";
        var start = finalizeOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find '" + marker + "' in finalize output: " + finalizeOutput);
        start += marker.Length;
        var end = finalizeOutput.IndexOf(' ', start);
        return finalizeOutput.Substring(start, end - start);
    }

    private void RunRunnerToCompletion()
    {
        var runner = StartRunner(leaseSeconds: 30);
        if (!runner.WaitForExit(30000))
        {
            try { runner.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail("The runner did not exit. Runner said: " + string.Join(" | ", _log));
        }
    }

    /// Starts the runner with no project of its own; it sweeps whatever the CLI has already made Known.
    private Process StartRunner(int leaseSeconds)
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

    /// Runs the real CLI against this test's own isolated worker root, the one <see cref="StartRunner"/> uses too.
    private CliRun Cli(string arguments)
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
        start.Environment[RunnerOptions.RootVariable] = _root;
        // This suite starts and manages its own runners explicitly; RunnerKickRaceTests covers the kick itself.
        start.Environment[RunnerKick.SuppressVariable] = "1";
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
