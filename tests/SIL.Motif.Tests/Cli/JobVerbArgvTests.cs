using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Covers the two verbs that let anything outside this process put work in the queue and read it back.
/// </summary>
/// <remarks>
/// Driven against the real executable rather than the command layer. A verb that only works in-process is
/// exactly what this suite exists to stop believing: the deleted wire protocol kept green seam tests for
/// months while its executable was never once driven.
/// </remarks>
public sealed class JobVerbArgvTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-job-verbs-" + Guid.NewGuid().ToString("N"));

    public JobVerbArgvTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Project, string.Empty);
    }

    private string Project => Path.Combine(_root, "project.fwdata");

    [Fact]
    public void EnqueueingARefreshPrintsAJobIdAndSucceeds()
    {
        var result = Run($"baseline-refresh --project \"{Project}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public void AQueuedJobIsReadableByIdAsJson()
    {
        var jobId = Enqueue();

        var shown = Run($"jobs show {jobId} --project \"{Project}\" --json");

        Assert.Equal(0, shown.ExitCode);
        using var document = JsonDocument.Parse(shown.Output);
        Assert.Equal(jobId, document.RootElement.GetProperty("jobId").GetString());
        // Queued rather than running: nothing has claimed it, because no runner is going.
        Assert.Equal("queued", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void AJobIdNobodyQueuedIsNotFoundRatherThanACrash()
    {
        Enqueue();

        var shown = Run($"jobs show job/absent --project \"{Project}\" --json");

        Assert.Equal(2, shown.ExitCode);
        Assert.Equal(FailureReason.NotFound, Envelope(shown.Error).Reason);
        Assert.DoesNotContain("   at ", shown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedJobIdIsAnInvocationErrorRatherThanARefusal()
    {
        Enqueue();

        var shown = Run($"jobs show \" \" --project \"{Project}\" --json");

        // Exit 1, not 2: the caller cannot fix this by retrying, and must not be told it can.
        Assert.Equal(1, shown.ExitCode);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(shown.Error).Reason);
    }

    [Fact]
    public void OmittingTheProjectIsAUsageFailureThatNamesTheFlag()
    {
        var result = Run("jobs show job/anything");

        Assert.Equal(1, result.ExitCode);
        // Naming the verb is what separates this from the unknown-verb banner, which also lists --project.
        Assert.Contains("Usage: motif jobs show <jobId> --project", result.Error, StringComparison.Ordinal);
    }

    private string Enqueue()
    {
        var result = Run($"baseline-refresh --project \"{Project}\"");
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private static FailureEnvelope Envelope(string stderr) =>
        ProjectionJson.Deserialize<FailureEnvelope>(stderr)!;

    private static CliRun Run(string arguments)
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
        Assert.True(process.WaitForExit(60000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
