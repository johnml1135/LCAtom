using System.Diagnostics;

namespace SIL.Motif.Host.Parser;

/// <summary>Raised when the parser could not be run at all — as distinct from a grammar it refused.</summary>
public sealed class ParserUnavailableException : Exception
{
    public ParserUnavailableException(string message) : base(message) { }
}

/// <summary>The outcome of asking the parser to analyse a batch: either results, or a refusal to build the FST.</summary>
/// <param name="Analysis">The results, when the run completed. <c>null</c> when <paramref name="Refusal"/> is set.</param>
/// <param name="Refusal">Why the FST path declined, when it did. <c>null</c> on success.</param>
public sealed record ParserRunResult(BatchAnalysis? Analysis, ParserRefusal? Refusal)
{
    public bool Succeeded => Analysis is not null;
}

/// <summary>
/// Runs PanGloss against a real FieldWorks project file and returns typed analyses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The project file is the input, and that is the whole point.</b> PanGloss reads <c>.fwdata</c> directly
/// (<c>pg_fwdata::import_file</c> → snapshot → <c>compile_project</c>), which takes ~253 ms on the 56 MB Sena 3
/// project and needs no FieldWorks assemblies, no HermitCrab dependency and no vendored grammar extractor. It
/// also — the reason Motif requires this route rather than preferring it — answers in **FieldWorks GUIDs**,
/// where the HermitCrab-XML route answers in synthetic keys that cannot be tied back to the entry or rule a
/// Proposal edited. See <c>docs/research/2026-08-07-parser-seam-goes-through-the-project-file.md</c>.
/// </para>
/// <para>
/// <b>Motif must save before calling this.</b> The parser reads the file, so anything uncommitted in an open
/// cache is invisible to it — the same precondition, for the same reason, as the Dry Run's scratch copy
/// (<c>docs/adr/0016-scratch-cache-copy-not-undo.md</c>). <see cref="FwDataProjectLoader.Save"/> waits for the
/// write to reach disk, which is what makes this safe to call immediately afterwards.
/// </para>
/// </remarks>
public class PanGlossParser
{
    private readonly string _executable;

    /// <param name="executablePath">The parser's path; discovered via <see cref="PanGlossExecutable"/> when null.</param>
    public PanGlossParser(string? executablePath = null)
    {
        _executable = executablePath ?? PanGlossExecutable.TryLocate()
            ?? throw new ParserUnavailableException(
                $"Could not find the pangloss executable. Build it with " +
                $"`cargo build --release -p pg-cli` in the PanGloss checkout, or set " +
                $"{PanGlossExecutable.PathVariable} to its path.");
    }

    /// <summary>
    /// Analyses <paramref name="words"/> against the grammar in <paramref name="projectFilePath"/>.
    /// </summary>
    /// <param name="perWordTimeoutMs">
    /// A per-word deadline. Words that hit it come back as <see cref="WordOutcome.TimedOut"/> and are never
    /// counted as analysis failures — see <see cref="BatchAnalysis.IsLowerBound"/>. Pass null for no deadline,
    /// which risks a single word taking minutes on a hard grammar (measured: 24 s on one Amharic word).
    /// </param>
    /// <param name="processTimeout">
    /// A ceiling on the whole run, since a grammar can be slow enough that no word ever completes — observed
    /// on a real project, where zero of fifteen words finished in ten minutes on the fallback engine.
    /// </param>
    public virtual ParserRunResult AnalyseBatch(
        string projectFilePath,
        IReadOnlyList<string> words,
        ParserEngine engine = ParserEngine.FstPrunedByHermitCrab,
        int? perWordTimeoutMs = 5000,
        TimeSpan? processTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) throw new ArgumentException("Required.", nameof(projectFilePath));
        if (words is null) throw new ArgumentNullException(nameof(words));
        if (!File.Exists(projectFilePath))
            throw new FileNotFoundException("The project file the parser must read does not exist.", projectFilePath);

        var scratch = Path.Combine(Path.GetTempPath(), "SIL.Motif.Parser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var wordsPath = Path.Combine(scratch, "words.txt");
        var outPath = Path.Combine(scratch, "out.tsv");

        try
        {
            File.WriteAllLines(wordsPath, words);

            var args = new List<string>
            {
                "batch", projectFilePath, wordsPath, outPath, "--engine=" + engine.BatchEngine(),
            };
            if (perWordTimeoutMs is int cap)
                args.Add($"--word-timeout-ms {cap}");

            var (exitCode, stdErr) = Run(args, processTimeout ?? TimeSpan.FromMinutes(15));

            if (exitCode != 0)
            {
                // A refused FST build is a grammar fact and must trigger the fallback. Anything else is an
                // environment or invocation fault and must not be dressed up as one.
                var refusal = ParserRefusalRecognizer.Recognize(stdErr);
                if (refusal is not null) return new ParserRunResult(null, refusal);

                throw new ParserUnavailableException(
                    $"pangloss batch exited {exitCode} for '{projectFilePath}' and the failure is not a " +
                    $"recognised FST refusal, so it is not something the fallback engine would fix:" +
                    Environment.NewLine + stdErr.Trim());
            }

            var tsv = File.Exists(outPath) ? File.ReadAllText(outPath) : string.Empty;
            var analysis = new BatchAnalysis(
                Words: BatchTsvParser.Parse(tsv),
                Engine: engine,
                PerWordTimeoutMs: perWordTimeoutMs,
                ProjectPath: projectFilePath,
                Warnings: ExtractWarnings(stdErr));

            return new ParserRunResult(analysis, null);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best effort: a leaked temp directory must not fail an analysis that succeeded */ }
        }
    }

    /// <summary>
    /// Assesses <paramref name="words"/> and returns analyses with every key resolved to a FieldWorks GUID —
    /// the form Motif can correlate with a Proposal's effects.
    /// </summary>
    /// <remarks>
    /// <see cref="AnalyseBatch"/> answers "did it parse, and how fast"; this answers "what did it parse it
    /// <i>as</i>". Coverage needs the first; deciding whether a grammar change did what was intended needs the
    /// second, because that comparison is per morpheme against the entry and category record a Proposal
    /// touched (docs/adr/0027-what-counts-as-the-same-word-analysis.md).
    /// </remarks>
    public virtual (AssessReport? Report, ParserRefusal? Refusal) Assess(
        string projectFilePath,
        IReadOnlyList<string> words,
        ParserEngine engine = ParserEngine.FstPrunedByHermitCrab,
        TimeSpan? processTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)) throw new ArgumentException("Required.", nameof(projectFilePath));
        if (words is null) throw new ArgumentNullException(nameof(words));
        if (!File.Exists(projectFilePath))
            throw new FileNotFoundException("The project file the parser must read does not exist.", projectFilePath);

        var scratch = Path.Combine(Path.GetTempPath(), "SIL.Motif.Parser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var wordsPath = Path.Combine(scratch, "words.txt");
        var reportPath = Path.Combine(scratch, "report.json");

        try
        {
            File.WriteAllLines(wordsPath, words);

            var (exitCode, stdErr) = Run(new[]
            {
                "assess", projectFilePath, "--words", wordsPath,
                "--pipeline", engine.AssessPipeline(), "--report", reportPath,
            }, processTimeout ?? TimeSpan.FromMinutes(15));

            if (exitCode != 0)
            {
                var refusal = ParserRefusalRecognizer.Recognize(stdErr);
                if (refusal is not null) return (null, refusal);

                throw new ParserUnavailableException(
                    $"pangloss assess exited {exitCode} for '{projectFilePath}' and the failure is not a " +
                    $"recognised FST refusal:" + Environment.NewLine + stdErr.Trim());
            }

            if (!File.Exists(reportPath))
                throw new ParserUnavailableException(
                    $"pangloss assess reported success but wrote no report to '{reportPath}'.");

            return (AssessReportParser.Parse(File.ReadAllText(reportPath)), null);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Keeps the parser's warnings rather than discarding them. Sena 3 produces 137 through this route, and a
    /// grammar coverage figure computed while those were ignored is not a figure about the grammar.
    /// </summary>
    private static IReadOnlyList<string> ExtractWarnings(string stdErr) =>
        stdErr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
                        || l.StartsWith("capability:", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private (int ExitCode, string StdErr) Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Argument-per-item, so a project path containing spaces needs no quoting of ours.
        foreach (var arg in args)
        {
            if (arg.StartsWith("--word-timeout-ms ", StringComparison.Ordinal))
            {
                startInfo.ArgumentList.Add("--word-timeout-ms");
                startInfo.ArgumentList.Add(arg["--word-timeout-ms ".Length..]);
            }
            else
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new ParserUnavailableException($"Could not start '{_executable}'.");

        // Read both streams before waiting: a full pipe buffer deadlocks a process that is still writing.
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new ParserUnavailableException(
                $"pangloss did not finish within {timeout.TotalMinutes:0.#} minutes. On a hard grammar a " +
                "single word can take tens of seconds and the fallback engine may never finish one at all; " +
                "raise the timeout deliberately or reduce the batch.");
        }

        _ = stdOutTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdErrTask.GetAwaiter().GetResult());
    }
}
