using System.Diagnostics;
using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Argv-level proof for the first CLI verb that is a worker command rather than local file work.
/// </summary>
/// <remarks>
/// These drive the real <c>motif.exe</c>, so they cover what only the executable decides: verb routing, flag
/// validation, exit codes, and that a worker failure is reported as an actionable message rather than a
/// stack trace. They deliberately do not cover the successful round trip, because reaching a worker from a
/// separate process would mean installing one into the machine-wide catalog the launcher reads. That path is
/// proven instead by <c>WorkerCommandDispatchTests</c>, which drives the same client against a real server
/// over a real named pipe.
/// </remarks>
public sealed class StoreCutoverArgvTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-cutover-argv-" + Guid.NewGuid().ToString("N"));

    public StoreCutoverArgvTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TheVerbIsRoutedAndListedRatherThanReportedUnknown()
    {
        var unknown = Run("store-rollback");

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("Unknown command 'store-rollback'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("store-cutover --project", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OmittingTheProjectIsAUsageFailureThatNamesTheFlag()
    {
        var result = Run("store-cutover");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: motif store-cutover --project <fwdata>", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void AProjectThatDoesNotExistIsRefusedBeforeAnyWorkerIsStarted()
    {
        var missing = Path.Combine(_root, "absent.fwdata");

        var result = Run("store-cutover --project \"" + missing + "\" --store \"" + _root + "\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error: Project file not found", result.Error, StringComparison.Ordinal);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        // A stack trace here would mean the CLI let an exception escape instead of reporting the refusal.
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

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
