using System.Diagnostics;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// Runs PanGloss's <c>assess</c> command against an already-exported candidate directory and returns the
/// parsed report.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the export/execute seam that never touches an <see cref="SIL.LCModel.LcmCache"/>,
/// a scratch, or a Baseline. Everything it needs is already bytes on disk — the directory an
/// <see cref="IPanGlossCandidateExporter"/> produced — so it can run after that scratch has been released.
/// </para>
/// <para>
/// PanGloss owns what it writes inside the candidate directory; this type looks for exactly one
/// <c>.fwdata</c> file by extension (the same dispatch <c>pg-cli</c> itself uses) rather than assuming a
/// name or layout.
/// </para>
/// </remarks>
public sealed class PanGlossAssessmentProcess : IPanGlossAssessor
{
    private readonly string _executable;

    /// <param name="executablePath">The parser's path; discovered via <see cref="PanGlossExecutable"/> when null.</param>
    public PanGlossAssessmentProcess(string? executablePath = null)
    {
        _executable = executablePath ?? PanGlossExecutable.TryLocate()
            ?? throw new ParserUnavailableException(
                $"Could not find the pangloss executable. Build it with " +
                $"`cargo build --release -p pg-cli` in the PanGloss checkout, or set " +
                $"{PanGlossExecutable.PathVariable} to its path.");
    }

    /// <summary>
    /// Assesses the grammar source found in <paramref name="exportedCandidate"/> and returns the report.
    /// </summary>
    /// <inheritdoc />
    public async Task<AssessReport> RunAsync(string exportedCandidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exportedCandidate))
            throw new ArgumentException("Required.", nameof(exportedCandidate));
        if (!Directory.Exists(exportedCandidate))
        {
            throw new DirectoryNotFoundException(
                $"The exported candidate directory does not exist: '{exportedCandidate}'.");
        }

        var grammarSource = LocateGrammarSource(exportedCandidate);

        var scratch = Path.Combine(Path.GetTempPath(), "SIL.Motif.Assessment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var reportPath = Path.Combine(scratch, "report.json");

        try
        {
            await RunProcessAsync(grammarSource, reportPath, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(reportPath))
            {
                throw new ParserUnavailableException(
                    $"pangloss assess reported success but wrote no report to '{reportPath}'.");
            }

            return AssessReportParser.Parse(File.ReadAllText(reportPath));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best effort: a leaked temp directory must not fail an assessment that succeeded */ }
        }
    }

    // PanGloss owns the exported directory's shape, so assume nothing beyond the extension.
    private static string LocateGrammarSource(string exportedCandidate)
    {
        var matches = Directory.GetFiles(exportedCandidate, "*.fwdata", SearchOption.AllDirectories);
        if (matches.Length == 0)
        {
            throw new FileNotFoundException(
                "The exported candidate contains no .fwdata grammar source.", exportedCandidate);
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"The exported candidate contains {matches.Length} .fwdata files; exactly one grammar " +
                "source is required to know which one PanGloss should read.");
        }

        return matches[0];
    }

    private async Task RunProcessAsync(string grammarSource, string reportPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("assess");
        startInfo.ArgumentList.Add(grammarSource);
        startInfo.ArgumentList.Add("--report");
        startInfo.ArgumentList.Add(reportPath);

        using var process = Process.Start(startInfo)
            ?? throw new ParserUnavailableException($"Could not start '{_executable}'.");

        // Read both streams before waiting: a full pipe buffer deadlocks a process that is still writing.
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var stdErr = await stdErrTask.ConfigureAwait(false);
        _ = await stdOutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new ParserUnavailableException(
                $"pangloss assess exited {process.ExitCode} for '{grammarSource}':" +
                Environment.NewLine + stdErr.Trim());
        }
    }
}
