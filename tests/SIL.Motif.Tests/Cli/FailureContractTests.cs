using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>Covers what a machine consumer reads from the real <c>motif.exe</c> on a failure.</summary>
public sealed class FailureContractTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-failure-" + Guid.NewGuid().ToString("N"));

    public FailureContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AJsonFailureIsOneEnvelopeOnStderrAndNothingOnStdout()
    {
        var result = Run("store-cutover --project \"" + Path.Combine(_root, "absent.fwdata") +
            "\" --store \"" + _root + "\" --json");

        Assert.Equal(string.Empty, result.Output);
        var envelope = JsonSerializer.Deserialize<FailureEnvelope>(result.Error,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(envelope);
        Assert.False(envelope!.Ok);
        Assert.Contains("Project file not found", envelope.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHumanRenderingIsUnchangedWithoutJson()
    {
        var result = Run("store-cutover --project \"" + Path.Combine(_root, "absent.fwdata") +
            "\" --store \"" + _root + "\"");

        Assert.StartsWith("error: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("{", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedInvocationIsCodeOneWhetherOrNotJsonWasAsked()
    {
        var text = Run("store-cutover");
        var json = Run("store-cutover --json");

        Assert.Equal(1, text.ExitCode);
        Assert.Equal(1, json.ExitCode);
        Assert.Contains("Usage: motif store-cutover", text.Error, StringComparison.Ordinal);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(json.Error).Reason);
    }

    [Fact]
    public void AnUnknownVerbIsCodeOneAndStillNamesTheVerbSet()
    {
        var result = Run("store-rollback");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command 'store-rollback'", result.Error, StringComparison.Ordinal);
        // The banner is what makes an unknown verb actionable rather than merely refused.
        Assert.Contains("store-cutover --project", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldProjectIsRetryableWhereAMalformedFlagIsNot()
    {
        // The whole point of the split: an agent must not retry a refusal and must be free to retry a lock.
        Assert.Equal(3, FailureEnvelope.ExitCodeFor(FailureReason.Busy));
        Assert.Equal(1, FailureEnvelope.ExitCodeFor(FailureReason.InvalidArgument));
        Assert.Equal(2, FailureEnvelope.ExitCodeFor(FailureReason.Refused));
        Assert.Equal(2, FailureEnvelope.ExitCodeFor(FailureReason.NotFound));
        Assert.Equal(4, FailureEnvelope.ExitCodeFor(FailureReason.StoreInconsistent));
    }

    private static FailureEnvelope Envelope(string stderr) =>
        JsonSerializer.Deserialize<FailureEnvelope>(stderr,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

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
