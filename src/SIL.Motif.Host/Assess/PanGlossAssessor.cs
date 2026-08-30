using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Assess;

/// <summary>Resolves the file path an Assessor's own cache should live at.</summary>
/// <remarks>
/// Declared here, in the Host layer that only knows Assessors, and implemented by
/// <c>SIL.Motif.Worker.Assess.StatsCacheStore</c>, which is the layer that knows the worker root and its
/// ownership rules. <see cref="PanGlossAssessor"/> must not know either of those things itself.
/// </remarks>
public interface IAssessorCachePathResolver
{
    /// <summary>
    /// The stable path for one Assessor's cache over one grammar and engine. Stable for the same three
    /// inputs; different when any of them differs, so two engines can never collide on one file — the rule
    /// PanGloss's own cache already enforces, respected here in the key rather than discovered at run time.
    /// </summary>
    string PathFor(string grammarSourceSha256, string assessor, string engine);
}

/// <summary>Runs PanGloss's stats-collecting batch pass, which populates a cache file PanGloss owns the format of.</summary>
/// <remarks>
/// Kept separate from <see cref="PanGlossParser.AnalyseBatch"/>: that call answers "how long, and did it
/// parse" from its own TSV output, cheaply. Per-object counters need <c>--stats --cache</c>, and reading
/// them back is <c>pangloss stats --format jsonl</c> — Motif does not parse the cache's SQLite itself
/// (ADR 0042 decision 8), so this seam only ever writes the cache; interpreting it is a Report's job.
/// </remarks>
public interface IPanGlossStatsRunner
{
    /// <summary>Runs the stats-collecting batch pass, writing PanGloss's cache to <paramref name="cachePath"/>.</summary>
    Task RunBatchAsync(string projectFilePath, IReadOnlyList<string> words, ParserEngine engine,
        TimeSpan perWordLimit, string cachePath, CancellationToken cancellationToken);
}

/// <summary>The real <see cref="IPanGlossStatsRunner"/>: shells out to <c>pangloss batch --stats --cache</c>.</summary>
public sealed class PanGlossStatsProcess : IPanGlossStatsRunner
{
    private readonly string _executable;

    /// <param name="executablePath">The parser's path; discovered via <see cref="PanGlossExecutable"/> when null.</param>
    public PanGlossStatsProcess(string? executablePath = null)
    {
        _executable = executablePath ?? PanGlossExecutable.TryLocate()
            ?? throw new ParserUnavailableException(
                $"Could not find the pangloss executable. Build it with " +
                $"`cargo build --release -p pg-cli` in the PanGloss checkout, or set " +
                $"{PanGlossExecutable.PathVariable} to its path.");
    }

    /// <inheritdoc />
    public async Task RunBatchAsync(string projectFilePath, IReadOnlyList<string> words, ParserEngine engine,
        TimeSpan perWordLimit, string cachePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(projectFilePath))
            throw new FileNotFoundException("The project file the parser must read does not exist.", projectFilePath);

        var scratch = Path.Combine(Path.GetTempPath(), "SIL.Motif.StatsCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var wordsPath = Path.Combine(scratch, "words.txt");
        var outPath = Path.Combine(scratch, "out.tsv");

        try
        {
            await File.WriteAllLinesAsync(wordsPath, words, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo(_executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("batch");
            startInfo.ArgumentList.Add(projectFilePath);
            startInfo.ArgumentList.Add(wordsPath);
            startInfo.ArgumentList.Add(outPath);
            startInfo.ArgumentList.Add("--engine=" + engine.BatchEngine());
            startInfo.ArgumentList.Add("--word-timeout-ms");
            startInfo.ArgumentList.Add(((int)perWordLimit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--stats");
            startInfo.ArgumentList.Add("--cache");
            startInfo.ArgumentList.Add(cachePath);

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
                    $"pangloss batch --stats exited {process.ExitCode} for '{projectFilePath}':" +
                    Environment.NewLine + stdErr.Trim());
            }

            if (!File.Exists(cachePath))
            {
                throw new ParserUnavailableException(
                    $"pangloss batch --stats reported success but wrote no cache to '{cachePath}'.");
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch { /* best effort: a leaked temp directory must not fail a run that succeeded */ }
        }
    }
}

/// <summary>
/// PanGloss as an <see cref="IAssessor"/>: the first Assessor, and proof the seam needs no PanGloss-specific
/// caller.
/// </summary>
/// <remarks>
/// <para>
/// Composes three existing or narrowly-new seams rather than re-implementing any of them:
/// <see cref="IPanGlossAssessor"/> for <see cref="AssessmentKind.Correctness"/> (GUID-keyed analyses against
/// manual analysis), <see cref="PanGlossParser.AnalyseBatch"/> for <see cref="AssessmentKind.ParseTime"/>
/// (already carries per-word elapsed time), and <see cref="IPanGlossStatsRunner"/> for
/// <see cref="AssessmentKind.ObjectTiming"/> (the new stats-cache route).
/// </para>
/// <para>
/// <see cref="AssessmentKind.EngineSize"/> is never declared: PanGloss emits build time and engine size on
/// stderr, and scraping stderr for them was rejected rather than adopted — that is a PanGloss-side ask, not
/// a Motif workaround. <see cref="AssessmentKind.Difference"/> and <see cref="AssessmentKind.Completion"/>
/// are never declared either: both compare two Assessments, which is the comparison mechanism's job, not
/// one Assessor call's.
/// </para>
/// </remarks>
public sealed class PanGlossAssessor : IAssessor
{
    /// <summary>The name this Assessor is registered and cited under.</summary>
    public const string AssessorName = "pangloss";

    private static readonly IReadOnlyDictionary<string, ParserEngine> EnginesByName =
        new Dictionary<string, ParserEngine>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] = ParserEngine.FstPrunedByHermitCrab,
            ["accurate"] = ParserEngine.HermitCrabOnly,
        };

    private static readonly IReadOnlyList<AssessmentKind> Supported =
        [AssessmentKind.ParseTime, AssessmentKind.Correctness, AssessmentKind.ObjectTiming];

    private static readonly IReadOnlyList<AssessmentKind> DefaultCollected =
        [AssessmentKind.ParseTime, AssessmentKind.Correctness];

    private readonly IAssessorCachePathResolver _cachePaths;
    private readonly PanGlossParser _parser;
    private readonly IPanGlossAssessor _reportRunner;
    private readonly IPanGlossStatsRunner _statsRunner;

    /// <param name="cachePaths">Resolves where this Assessor's stats cache lives for a grammar and engine.</param>
    /// <param name="parser">Runs the plain batch pass; defaults to a real one.</param>
    /// <param name="reportRunner">Runs the GUID-keyed assess pass; defaults to a real one.</param>
    /// <param name="statsRunner">Runs the stats-collecting batch pass; defaults to a real one.</param>
    public PanGlossAssessor(
        IAssessorCachePathResolver cachePaths,
        PanGlossParser? parser = null,
        IPanGlossAssessor? reportRunner = null,
        IPanGlossStatsRunner? statsRunner = null)
    {
        _cachePaths = cachePaths ?? throw new ArgumentNullException(nameof(cachePaths));
        _parser = parser ?? new PanGlossParser();
        _reportRunner = reportRunner ?? new PanGlossAssessmentProcess();
        _statsRunner = statsRunner ?? new PanGlossStatsProcess();
    }

    /// <inheritdoc />
    public string Name => AssessorName;

    /// <inheritdoc />
    public IReadOnlyList<AssessmentKind> KindsFor(AssessmentScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var wanted = scope.Collect.Count == 0 ? DefaultCollected : scope.Collect;
        return wanted.Where(Supported.Contains).Distinct().ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProducedAssessment>> ProduceAsync(
        AssessmentScope scope, string exportedCandidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(exportedCandidate))
            throw new ArgumentException("Required.", nameof(exportedCandidate));

        var wanted = scope.Collect.Count == 0 ? DefaultCollected : scope.Collect;
        foreach (var kind in wanted)
        {
            if (!Supported.Contains(kind))
                throw new AssessorRefusalException(AssessorName, kind, ReasonNotProduced(kind));
        }

        if (!EnginesByName.TryGetValue(scope.Engine, out var engine))
        {
            throw new ArgumentException(
                $"'{scope.Engine}' does not name an engine {AssessorName} recognizes.", nameof(scope));
        }

        var grammarSourcePath = LocateGrammarSource(exportedCandidate);
        var results = new List<ProducedAssessment>();

        AssessReport? report = null;
        if (wanted.Contains(AssessmentKind.Correctness) || wanted.Contains(AssessmentKind.ObjectTiming))
            report = await _reportRunner.RunAsync(exportedCandidate, cancellationToken).ConfigureAwait(false);

        if (wanted.Contains(AssessmentKind.Correctness))
            results.Add(new ProducedAssessment(AssessmentKind.Correctness, null, null));

        if (wanted.Contains(AssessmentKind.ParseTime))
        {
            var runResult = _parser.AnalyseBatch(
                grammarSourcePath, scope.Words, engine, (int)scope.PerWordLimit.TotalMilliseconds);
            if (!runResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"{AssessorName} could not measure parse time: {runResult.Refusal!.Detail}");
            }
            results.Add(new ProducedAssessment(AssessmentKind.ParseTime, null, null));
        }

        if (wanted.Contains(AssessmentKind.ObjectTiming))
        {
            var cachePath = _cachePaths.PathFor(report!.GrammarSourceSha256, AssessorName, scope.Engine);
            await _statsRunner.RunBatchAsync(
                grammarSourcePath, scope.Words, engine, scope.PerWordLimit, cachePath, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new ProducedAssessment(AssessmentKind.ObjectTiming, cachePath, DigestOfFile(cachePath)));
        }

        return results;
    }

    private static string ReasonNotProduced(AssessmentKind kind) => kind switch
    {
        AssessmentKind.EngineSize =>
            "the compiled engine's size is emitted on stderr at build time, not through this route.",
        AssessmentKind.Difference =>
            "a difference compares two Assessments; it is not one Assessor call's job to produce alone.",
        AssessmentKind.Completion =>
            "which words newly complete compares two Assessments; it is not one Assessor call's job to produce alone.",
        _ => $"'{AssessorName}' does not produce {kind} from this scope's collection.",
    };

    // Mirrors PanGlossAssessmentProcess's own dispatch: exactly one .fwdata by extension, no assumed layout.
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
                $"The exported candidate contains {matches.Length} .fwdata files; exactly one is required.");
        }
        return matches[0];
    }

    // Hashes what actually landed on disk rather than trusting the stats runner's exit code alone.
    private static string DigestOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
