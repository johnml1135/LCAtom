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
    private readonly string _fwDataPath;

    public FailureContractTests()
    {
        Directory.CreateDirectory(_root);
        _fwDataPath = Path.Combine(_root, "Project.fwdata");
    }

    [Fact]
    public void AJsonFailureIsOneEnvelopeOnStderrAndNothingOnStdout()
    {
        var result = Run("baseline-refresh --project \"" + Path.Combine(_root, "absent.fwdata") + "\" --json");

        Assert.Equal(string.Empty, result.Output);
        var envelope = ProjectionJson.Deserialize<FailureEnvelope>(result.Error);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Ok);
        Assert.Contains("Project file not found", envelope.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHumanRenderingIsUnchangedWithoutJson()
    {
        var result = Run("baseline-refresh --project \"" + Path.Combine(_root, "absent.fwdata") + "\"");

        Assert.StartsWith("error: ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("{", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedInvocationIsCodeOneWhetherOrNotJsonWasAsked()
    {
        var text = Run("baseline-refresh");
        var json = Run("baseline-refresh --json");

        Assert.Equal(1, text.ExitCode);
        Assert.Equal(1, json.ExitCode);
        Assert.Contains("Usage: motif baseline-refresh", text.Error, StringComparison.Ordinal);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(json.Error).Reason);
    }

    [Fact]
    public void AnUnknownVerbIsCodeOneAndStillNamesTheVerbSet()
    {
        var result = Run("store-rollback");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command 'store-rollback'", result.Error, StringComparison.Ordinal);
        // The banner is what makes an unknown verb actionable rather than merely refused.
        Assert.Contains("dry-run <proposalId> --project", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentProposalIsNotFoundAndEnveloped()
    {
        // A well-formed id that nothing queued: the caller may act on the reason, and must not retry.
        var absent = "proposal/" + new string('A', 22);

        var result = Run($"show {absent} --project \"{_fwDataPath}\" --json");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(FailureReason.NotFound, Envelope(result.Error).Reason);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void AMalformedProposalIdIsAnInvocationErrorAndEnveloped()
    {
        var result = Run($"show proposal/short --project \"{_fwDataPath}\" --json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(result.Error).Reason);
    }

    [Fact]
    public void AMissingProjectFileIsAnInvocationErrorAndEnveloped()
    {
        var absent = Path.Combine(_root, "not-here.fwdata");

        var result = Run($"open \"{absent}\" --json");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(FailureReason.InvalidArgument, Envelope(result.Error).Reason);
    }

    [Fact]
    public void EveryFailureUnderJsonCarriesAnEnvelopeRatherThanProse()
    {
        // One reader must handle every verb; a verb opting out silently is worse than one that fails.
        foreach (var invocation in new[]
                 {
                     $"show proposal/short --project \"{_fwDataPath}\" --json",
                     $"show proposal/{new string('A', 22)} --project \"{_fwDataPath}\" --json",
                     $"open \"{Path.Combine(_root, "absent.fwdata")}\" --json",
                 })
        {
            var result = Run(invocation);
            Assert.NotEqual(0, result.ExitCode);
            Assert.StartsWith("{", result.Error.TrimStart(), StringComparison.Ordinal);
        }
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
