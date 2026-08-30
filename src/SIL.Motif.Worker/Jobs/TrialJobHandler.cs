using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.LCModel;
using SIL.Motif.Contract;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Config;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// The closed wire shape a Trial job's <c>InputJson</c> carries. Written by the CLI when it enqueues a
/// Trial and read back by <see cref="TrialJobHandler"/> — the two never share the proposal JSON any other
/// way, since they run in different processes.
/// </summary>
/// <param name="ProposalJson">
/// The Proposal's own JSON text, committed or an uncommitted Draft's working content — whatever
/// <see cref="ProposalRepository.Get"/> returned, verbatim.
/// </param>
/// <param name="Scope">The declared Assessment scope to use, or <c>null</c> for the project's default.</param>
public sealed record TrialJobInput(
    [property: JsonPropertyName("proposalJson")] string ProposalJson,
    [property: JsonPropertyName("scope")] string? Scope)
{
    /// <exception cref="InvalidOperationException">The input is not a well-formed Trial job input.</exception>
    public static TrialJobInput Parse(string inputJson) =>
        JsonSerializer.Deserialize<TrialJobInput>(inputJson, MotifJson.CreateOptions())
        ?? throw new InvalidOperationException("A Trial job's input could not be read.");
}

/// <summary>
/// Runs a Trial: one throwaway, writable copy of the published Baseline, opened once, that a Proposal is
/// applied to and read back as a Dry Run, then saved and handed to an Assessor for the candidate's
/// Assessments — all from that same copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot reuse <see cref="DryRunJobHandler"/>'s in-place open.</b> A plain Dry Run opens the
/// published Baseline's <c>.fwdata</c> directly (<c>BaselineScratchFactory.OpenSingleUse</c>) and never
/// saves it, because saving an in-place candidate would overwrite the immutable published directory. A
/// Trial's candidate must be saved — an Assessor reads bytes on disk, not an open cache — so it is opened
/// through a private, disposable file copy instead (<c>ScratchCacheFactory.CreateFromFileCopy</c>), which
/// still costs exactly one Baseline load. Saving that copy in place and pointing the Assessor at its own
/// folder needs no second copy.
/// </para>
/// <para>
/// A failure is <see cref="JobStatus.Failed"/> only while nothing has been committed yet. Once the Dry Run
/// is published, every later failure — building the candidate for Assessment, or the Assessor itself —
/// lands on <see cref="JobStatus.CompletedWithAssessmentFailure"/>, because the Dry Run already succeeded
/// and is retained.
/// </para>
/// </remarks>
internal sealed class TrialJobHandler
{
    /// <summary>The durable job kind this handler drives.</summary>
    internal const string TrialKind = "trial";

    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly ProposalRepository _proposals;
    private readonly ProjectLaneRegistry _lanes;
    private readonly IProjectConfigurationReader _configuration;
    private readonly IAssessorCatalog _assessors;
    private readonly IAssessmentRepository _assessments;
    private readonly Func<string, string, CancellationToken,
        Task<(IReadOnlyCollection<Guid> AppliedProposalIds, DryRunScratch? Scratch)>> _openScratch;
    private readonly Func<DryRunScratch?, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> _runDryRun;
    private readonly Func<LcmCache?, CancellationToken, Task<string>> _prepareForAssessment;

    /// <param name="jobs">This project's job store.</param>
    /// <param name="baselines">This project's published-Baseline store.</param>
    /// <param name="proposals">
    /// This project's Proposal store, used only to resolve prerequisites (<see cref="ProposalRepository.PlanPrerequisites"/>)
    /// — never to read the requested Proposal itself, which the job's own <c>InputJson</c> already carries.
    /// </param>
    /// <param name="lanes">Serializes Baseline-dependent work per project.</param>
    /// <param name="configuration">Reads the project's declared Assessment scopes and policy.</param>
    /// <param name="assessors">Resolves the scope's named Assessor.</param>
    /// <param name="assessments">Where produced Assessments are recorded.</param>
    /// <param name="openScratch">
    /// Opens the one private, writable copy of the published Baseline this Trial measures against — a
    /// single Baseline load — and reads which Proposals are already applied in it.
    /// </param>
    /// <param name="runDryRun">Runs the resolved plan against the scratch <paramref name="openScratch"/> opened.</param>
    /// <param name="prepareForAssessment">
    /// Saves the mutated scratch in place and returns the directory an Assessor can read it from. Never
    /// opens the Baseline again.
    /// </param>
    internal TrialJobHandler(JobRepository jobs, BaselineRepository baselines, ProposalRepository proposals,
        ProjectLaneRegistry lanes, IProjectConfigurationReader configuration, IAssessorCatalog assessors,
        IAssessmentRepository assessments,
        Func<string, string, CancellationToken,
            Task<(IReadOnlyCollection<Guid> AppliedProposalIds, DryRunScratch? Scratch)>> openScratch,
        Func<DryRunScratch?, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> runDryRun,
        Func<LcmCache?, CancellationToken, Task<string>> prepareForAssessment)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _assessors = assessors ?? throw new ArgumentNullException(nameof(assessors));
        _assessments = assessments ?? throw new ArgumentNullException(nameof(assessments));
        _openScratch = openScratch ?? throw new ArgumentNullException(nameof(openScratch));
        _runDryRun = runDryRun ?? throw new ArgumentNullException(nameof(runDryRun));
        _prepareForAssessment = prepareForAssessment ?? throw new ArgumentNullException(nameof(prepareForAssessment));
    }

    /// <summary>Runs one claimed Trial job through to a terminal outcome, or parks it waiting for a Baseline.</summary>
    internal async Task<JobOutcome?> RunAsync(string jobId, ProjectLocator project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var job = _jobs.Get(jobId) ?? throw new InvalidOperationException("The Trial job does not exist.");
        if (job.Status != JobStatus.Running)
            throw new InvalidOperationException("A Trial must already be claimed by a runner.");
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        if (!StringComparer.Ordinal.Equals(job.Kind, TrialKind) ||
            !StringComparer.Ordinal.Equals(job.ProjectKey, workspaceKey))
            throw new InvalidOperationException("The job does not address this project's Trial.");

        var baseline = _baselines.GetCurrent(workspaceKey);
        if (baseline is null)
        {
            _jobs.Transition(job, JobStatus.WaitingForBaseline);
            return null;
        }

        var input = TrialJobInput.Parse(job.InputJson);
        var proposal = ProposalJsonParser.Parse(input.ProposalJson);
        var configuration = _configuration.Read(project);
        var scopeConfiguration = ResolveScope(configuration, input.Scope);
        var assessor = _assessors.Resolve(scopeConfiguration.Assessor);
        var lane = _lanes.GetOrCreate(workspaceKey);

        // Reported while queued behind the lane; the lane's own dispatch resumes this directly into Running.
        _jobs.Transition(job, JobStatus.WaitingForBaseline);

        DryRunModel? dryRun = null;
        AssessmentScope? scope = null;
        CorpusDescriptor? corpus = null;
        string? exportedDirectory = null;
        var scratchRoot = AllocateScratchRoot();
        Exception? laneFailure = null;
        try
        {
            await lane.EnqueueAsync(ProjectWorkItem.CandidateExport(async (_, laneToken) =>
            {
                _jobs.Transition(_jobs.Get(jobId)!, JobStatus.Running);
                var (appliedProposalIds, scratch) = await _openScratch(baseline.FwDataPath, scratchRoot, laneToken)
                    .ConfigureAwait(false);
                try
                {
                    var plan = _proposals.PlanPrerequisites(proposal, appliedProposalIds);
                    dryRun = await _runDryRun(scratch, plan, laneToken).ConfigureAwait(false);

                    var publishedJson = DryRunJobHandler.BuildPublishedDryRunJson(dryRun);
                    var beforePublish = _jobs.Get(jobId)!;
                    _jobs.PublishDryRun(jobId, publishedJson, beforePublish.Version);

                    var cache = scratch?.PeekCache();
                    var words = cache is null
                        ? Array.Empty<string>()
                        : LcmWordformCorpus.ExtractForms(cache).ToArray();
                    corpus = CorpusDescriptor.Create(scopeConfiguration.Name, words);
                    scope = new AssessmentScope(words, scopeConfiguration.Engine,
                        ParseCollect(scopeConfiguration.Collect), scopeConfiguration.PerWordLimit);
                    exportedDirectory = await _prepareForAssessment(cache, laneToken).ConfigureAwait(false);
                }
                finally
                {
                    scratch?.Dispose();
                }
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            laneFailure = exception;
        }

        if (laneFailure is not null)
        {
            TryDeleteDirectory(scratchRoot);
            var current = _jobs.Get(jobId)!;
            var detail = JsonSerializer.Serialize(new { detail = laneFailure.Message });
            // A successful terminal status may not carry a failure category, so only Failed gets one here.
            return current.DryRunPublished
                ? new JobOutcome(JobStatus.CompletedWithAssessmentFailure, JobFailureCategory.None, detail)
                : new JobOutcome(JobStatus.Failed, JobFailureCategory.Unknown, detail);
        }

        // The run already happened; cancellation now must still stop it from becoming durable further.
        if (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(scratchRoot);
            return new JobOutcome(JobStatus.Cancelled, JobFailureCategory.Cancellation);
        }

        try
        {
            var produced = await assessor.ProduceAsync(scope!, exportedDirectory!, cancellationToken)
                .ConfigureAwait(false);
            var assessmentIds = RecordAll(produced, proposal, dryRun!, corpus!, scope!, scopeConfiguration,
                assessor.Name, baseline.Token);
            var completionJson = JsonSerializer.Serialize(
                new TrialCompletion(baseline.Token, assessmentIds), MotifJson.CreateOptions());
            return new JobOutcome(JobStatus.Completed, JobFailureCategory.None, completionJson);
        }
        catch (OperationCanceledException)
        {
            return new JobOutcome(JobStatus.Cancelled, JobFailureCategory.Cancellation);
        }
        catch (Exception exception)
        {
            var detail = JsonSerializer.Serialize(new { detail = exception.Message });
            return new JobOutcome(JobStatus.CompletedWithAssessmentFailure, JobFailureCategory.None, detail);
        }
        finally
        {
            TryDeleteDirectory(scratchRoot);
        }
    }

    private IReadOnlyList<string> RecordAll(IReadOnlyList<ProducedAssessment> produced, Contract.Model.Proposal proposal,
        DryRunModel dryRun, CorpusDescriptor corpus, AssessmentScope scope,
        AssessmentScopeConfiguration scopeConfiguration, string assessorName, BaselineToken baselineToken)
    {
        var scopeJson = JsonSerializer.Serialize(new ScopeWire(scope.Words, scope.Engine,
            scope.Collect.Select(kind => kind.ToString()).ToArray(), (long)scope.PerWordLimit.TotalMilliseconds),
            MotifJson.CreateOptions());
        var scopeDigest = Digest(scopeJson);
        var baselineTokenJson = JsonSerializer.Serialize(baselineToken, MotifJson.CreateOptions());

        var ids = new List<string>();
        foreach (var item in produced)
        {
            var assessmentId = CanonicalId.Mint("assessment/").Value;
            _assessments.Record(new NewAssessmentRecord(
                AssessmentId: assessmentId,
                ProposalId: proposal.ProposalId,
                ProposalIntentDigest: dryRun.IntentDigest,
                Assessor: assessorName,
                Kind: item.Kind.ToString(),
                ScopeJson: scopeJson,
                ScopeDigest: scopeDigest,
                TokeniserName: "none",
                TokeniserVersion: "1",
                BaselineToken: baselineTokenJson,
                Corpus: corpus,
                OutcomeDigest: item.OutcomeDigest,
                SemanticDigest: item.SemanticDigest,
                GrammarSourceSha256: item.GrammarSourceSha256,
                ModelFingerprint: item.ModelFingerprint,
                Pipeline: item.Pipeline,
                DiagnosticCount: item.DiagnosticCount,
                Words: WordsFor(item.Raw)));
            ids.Add(assessmentId);
        }
        return ids;
    }

    private static IReadOnlyList<AssessedWord> WordsFor(AssessmentRaw raw) => raw switch
    {
        AssessmentRaw.WordMeasurements measurements => measurements.Words,
        AssessmentRaw.Batch batch => batch.Analysis.Words
            .Select(word => new AssessedWord(word.Word, WordOutcomeWire(word.Outcome), Array.Empty<ParsedAnalysis>()))
            .ToArray(),
        AssessmentRaw.FileCache => throw new NotSupportedException(
            "An ObjectTiming Assessment carries a stats-cache file, not per-word rows, and the Assessments " +
            "schema has no column for a cache path or digest yet; a Trial does not request this kind."),
        _ => throw new ArgumentOutOfRangeException(nameof(raw)),
    };

    private static string WordOutcomeWire(WordOutcome outcome) => outcome switch
    {
        WordOutcome.Analysed => "analysed",
        WordOutcome.NoAnalysis => "no-analysis",
        WordOutcome.TimedOut => "timed-out",
        WordOutcome.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static AssessmentScopeConfiguration ResolveScope(ProjectConfiguration configuration, string? requestedName)
    {
        if (requestedName is not null)
        {
            foreach (var candidate in configuration.Scopes)
            {
                if (string.Equals(candidate.Name, requestedName, StringComparison.Ordinal)) return candidate;
            }
            throw new InvalidOperationException($"No Assessment scope named '{requestedName}' is declared.");
        }

        foreach (var candidate in configuration.Scopes)
        {
            if (string.Equals(candidate.Name, AssessmentScopeConfiguration.DefaultName, StringComparison.Ordinal))
                return candidate;
        }
        return configuration.Scopes[0];
    }

    private static IReadOnlyList<AssessmentKind> ParseCollect(IReadOnlyList<string> collect)
    {
        if (collect.Count == 0) return Array.Empty<AssessmentKind>();
        var kinds = new List<AssessmentKind>(collect.Count);
        foreach (var name in collect)
        {
            if (!Enum.TryParse<AssessmentKind>(name, ignoreCase: true, out var kind))
                throw new InvalidOperationException($"'{name}' does not name a known Assessment kind.");
            kinds.Add(kind);
        }
        return kinds;
    }

    private static string AllocateScratchRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "SIL.Motif.Trial", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // Best effort: a leaked temp directory must not fail a Trial that already succeeded or already failed.
    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Digest(string json)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ScopeWire(
        [property: JsonPropertyName("words")] IReadOnlyList<string> Words,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("collect")] IReadOnlyList<string> Collect,
        [property: JsonPropertyName("perWordLimitMs")] long PerWordLimitMs);

    private sealed record TrialCompletion(
        [property: JsonPropertyName("baselineToken")] BaselineToken BaselineToken,
        [property: JsonPropertyName("assessmentIds")] IReadOnlyList<string> AssessmentIds);
}
