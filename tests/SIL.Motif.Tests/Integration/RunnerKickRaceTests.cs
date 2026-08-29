using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Generator;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Integration;

/// <summary>
/// Covers the kick ADR 0041 decision 5 requires: the CLI spawns a runner unconditionally after
/// enqueueing, and the race that makes that not a one-liner — a runner the CLI kicks can lose the
/// ownership mutex to one that is alive but about to exit, and must retry rather than give up, or the job
/// it just queued is stranded until the next command happens to wake one.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class RunnerKickRaceTests : IDisposable
{
    private readonly PristineProjectFixture _projects;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-kick-" + Guid.NewGuid().ToString("N"));
    private readonly string _ownerNamespace = "motif-kick-" + Guid.NewGuid().ToString("N");

    public RunnerKickRaceTests(PristineProjectFixture projects)
    {
        _projects = projects;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Stands in for "a live runner inside its final idle tick" by holding the same ownership mutex
    /// directly, rather than racing a real process's own idle timer — which cannot be made to hit a
    /// precise instant without a sleep-and-hope test. The kicked runner is a real process throughout.
    /// </summary>
    [Fact]
    public void AnEnqueueThatLandsWhileTheOwnerIsAboutToExitStillEndsWithTheJobRun()
    {
        var project = _projects.CopyProjectFile();
        using var occupying = JobRunnerHost.ForNamespace(_ownerNamespace);
        Assert.True(occupying.TryAcquireOwnership());

        // Enqueues and kicks a real runner; its first acquisition attempt is guaranteed to fail here.
        var jobId = Cli($"baseline-refresh --project \"{project}\"").Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(jobId));

        // Comfortably inside the runner's own retry window (docs/adr/0041-the-database-is-the-only-store.md).
        Thread.Sleep(500);
        occupying.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string status;
        do
        {
            status = StatusOf(project, jobId);
            if (status is "completed" or "failed" or "cancelled") break;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);

        Assert.NotEqual("queued", status);
        Assert.NotEqual("running", status);
    }

    private string StatusOf(string project, string jobId)
    {
        var shown = Cli($"jobs show {jobId} --project \"{project}\" --json");
        Assert.Equal(0, shown.ExitCode);
        using var document = JsonDocument.Parse(shown.Output);
        return document.RootElement.GetProperty("status").GetString()!;
    }

    /// Runs the real CLI with the kick enabled, sharing this test's isolated root and runner namespace.
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
        start.Environment[RunnerOptions.NamespaceVariable] = _ownerNamespace;
        // The runner this kicks is nobody's to wait on, so bound how long it outlives the test.
        start.Environment[RunnerOptions.IdleVariable] = "2";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        // The kicked runner is still sweeping this root; give its bounded idle timeout time to expire.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(250); }
            catch (UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
