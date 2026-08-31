using System.Diagnostics;
using System.Text.Json;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Generator;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Covers <c>config show</c> against the real <c>motif.exe</c> rather than the command layer, so the
/// documented defaults and the failure contract are proven for the surface an outside caller actually runs.
/// </summary>
public sealed class ConfigCommandArgvTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-config-argv-" + Guid.NewGuid().ToString("N"));

    public ConfigCommandArgvTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Project, string.Empty);
    }

    private string Project => Path.Combine(_root, "project.fwdata");

    [Fact]
    public void ShowReportsTheDocumentedDefaultsAsJsonWhenTheFileIsAbsent()
    {
        var result = Run($"config show --project \"{Project}\" --json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.True(document.RootElement.GetProperty("gateOnRegression").GetBoolean());
        Assert.True(document.RootElement.GetProperty("purgeOnApply").GetBoolean());
        var scopes = document.RootElement.GetProperty("scopes");
        Assert.Equal(1, scopes.GetArrayLength());
        var scope = scopes[0];
        Assert.Equal("default", scope.GetProperty("name").GetString());
        Assert.Equal("pangloss", scope.GetProperty("assessor").GetString());
        Assert.Equal("fast", scope.GetProperty("engine").GetString());
        Assert.Equal(1000, scope.GetProperty("perWordLimitMs").GetInt64());
    }

    [Fact]
    public void ShowReportsTheDocumentedDefaultsAsTextWhenTheFileIsAbsent()
    {
        var result = Run($"config show --project \"{Project}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("Regression gate: on", result.Output, StringComparison.Ordinal);
        Assert.Contains("Purge on apply:  on", result.Output, StringComparison.Ordinal);
        Assert.Contains("default", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowRefusesAMalformedFileNamingTheLine()
    {
        File.WriteAllText(
            Path.Combine(_root, "project.motif.toml"),
            "[regression]\ngate = perhaps\n");

        var result = Run($"config show --project \"{Project}\" --json");

        Assert.Equal(2, result.ExitCode);
        var envelope = Envelope(result.Error);
        Assert.Equal(FailureReason.Refused, envelope.Reason);
        Assert.Contains("line 2", envelope.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void ShowRefusesAnUnknownKeyRatherThanIgnoringIt()
    {
        File.WriteAllText(
            Path.Combine(_root, "project.motif.toml"),
            "[apply]\npurge-on-apply = true\nsweep-drafts = true\n");

        var result = Run($"config show --project \"{Project}\" --json");

        Assert.Equal(2, result.ExitCode);
        var envelope = Envelope(result.Error);
        Assert.Equal(FailureReason.Refused, envelope.Reason);
        Assert.Contains("unknown key", envelope.Message, StringComparison.Ordinal);
        Assert.Contains("sweep-drafts", envelope.Message, StringComparison.Ordinal);
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
