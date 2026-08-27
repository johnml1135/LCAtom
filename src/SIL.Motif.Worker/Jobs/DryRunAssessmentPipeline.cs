using System.Globalization;
using SIL.Motif.Contract;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;

using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Worker.Jobs;

/// <summary>An already-open, mutated candidate the lane holds open just long enough to export.</summary>
/// <remarks>
/// <see cref="ExportAsync"/> must run, and this must be disposed, before the lane releases the project —
/// the Assessment itself runs later, against the exported directory alone, with no candidate open.
/// </remarks>
public interface IDryRunCandidateForExport : IDisposable
{
    /// <summary>The Dry Run computed from applying the Proposal to this candidate.</summary>
    DryRunModel DryRun { get; }

    /// <summary>Saves and copies this candidate into the empty <paramref name="emptyDestination"/>.</summary>
    Task ExportAsync(string emptyDestination, CancellationToken cancellationToken);
}

/// <summary>A closed summary of a completed Assessment run — never the per-word detail, never the engine.</summary>
public sealed record AssessmentSummary(
    [property: JsonPropertyName("pipeline")] string Pipeline,
    [property: JsonPropertyName("wordCount")] int WordCount,
    [property: JsonPropertyName("analysisCount")] int AnalysisCount,
    [property: JsonPropertyName("diagnosticCount")] int DiagnosticCount,
    [property: JsonPropertyName("outcomeDigest")] string OutcomeDigest,
    [property: JsonPropertyName("semanticDigest")] string SemanticDigest,
    [property: JsonPropertyName("grammarSourceSha256")] string GrammarSourceSha256,
    [property: JsonPropertyName("modelFingerprint")] string ModelFingerprint,
    [property: JsonPropertyName("boundedLog")] string BoundedLog);

/// <summary>What one Dry Run plus optional Assessment attempt is asked to do.</summary>
/// <param name="Baseline">
/// The Baseline the caller observed published; the pipeline refuses to proceed if this no longer
/// matches the durable publication, rather than silently binding to a different one.
/// Pinned by `RefusesWhenTheRequestedBaselineIsNoLongerThePublishedBaseline`.
/// </param>
/// <param name="IncludeAssessment">
/// Whether PanGloss should assess the candidate after the Dry Run. Callers default this to
/// <c>true</c>; <c>false</c> is the explicit opt-out.
/// </param>
public sealed record DryRunAssessmentRequest(
    CanonicalId ProposalId, string IntentDigest, BaselineToken Baseline, bool IncludeAssessment);

/// <summary>
/// Drives one Dry Run through to a terminal job, and — when asked — an Assessment after it, without
/// ever letting the Assessment's slower, ephemeral tail block another project's Dry Run.
/// </summary>
/// <remarks>
/// <para>
/// The Dry Run is committed durably (<see cref="JobRepository.PublishDryRun"/>) before the candidate is
/// exported, and the candidate is exported — and disposed — before the project lane is released, so the
/// slow external Assessment process always runs against durable evidence and never holds the lane. A
/// Baseline-backed candidate is never opened here for export: <c>IncludeAssessment = false</c> always
/// takes the plain, no-copy, in-place path, and only <c>IncludeAssessment = true</c> ever asks
/// <see cref="IDryRunCandidateForExport"/> for a candidate at all.
/// </para>
/// <para>
/// Every failure after publication — export or Assessment — lands on
/// <see cref="JobStatus.CompletedWithAssessmentFailure"/> rather than <see cref="JobStatus.Failed"/>,
/// because the Dry Run itself already succeeded and is retained; only the evidence-gathering tail did
/// not. Cancellation after publication always resolves to the canonical cancelled Assessment
/// disposition <see cref="JobStateMachine"/> already enforces, and this type deletes the exported
/// candidate directory on every terminal outcome, not only cancellation.
/// </para>
/// </remarks>
public sealed class DryRunAssessmentPipeline
{
    /// <summary>The durable job kind this pipeline creates and drives.</summary>
    public const string JobKind = "dry-run-assessment";

    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly ProjectLaneRegistry _lanes;
    private readonly string _workspaceKey;
    private readonly Func<string, CanonicalId, string, CancellationToken, Task<DryRunModel>> _runInPlace;
    private readonly Func<string, CanonicalId, string, CancellationToken, Task<IDryRunCandidateForExport>> _openCandidateForExport;
    private readonly Func<string, CancellationToken, Task<AssessmentSummary>> _runAssessment;
    private readonly Func<string> _allocateExportDirectory;
    private readonly Action<string> _deleteExportDirectory;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <param name="jobs">The durable job store for this project's own database.</param>
    /// <param name="baselines">This project's Baseline publications.</param>
    /// <param name="lanes">The registry that serializes Baseline-dependent work per project.</param>
    /// <param name="project">The project this pipeline instance always drives.</param>
    /// <param name="runInPlace">
    /// Opens the published Baseline's <c>.fwdata</c> in place, applies the Proposal, and returns the Dry
    /// Run without ever saving — used only when <see cref="DryRunAssessmentRequest.IncludeAssessment"/>
    /// is <c>false</c>.
    /// </param>
    /// <param name="openCandidateForExport">
    /// Opens a copy-backed, writable candidate, applies the Proposal, and returns a handle that can
    /// export and must be disposed — used only when Assessment is requested.
    /// </param>
    /// <param name="runAssessment">Runs PanGloss against an already-exported candidate directory.</param>
    /// <param name="allocateExportDirectory">Creates and returns a fresh, empty export destination.</param>
    /// <param name="deleteExportDirectory">Best-effort removal of a previously allocated export destination.</param>
    /// <param name="utcNow">Clock used for the job's creation timestamp; defaults to the system clock.</param>
    internal DryRunAssessmentPipeline(
        JobRepository jobs,
        BaselineRepository baselines,
        ProjectLaneRegistry lanes,
        ProjectLocator project,
        Func<string, CanonicalId, string, CancellationToken, Task<DryRunModel>> runInPlace,
        Func<string, CanonicalId, string, CancellationToken, Task<IDryRunCandidateForExport>> openCandidateForExport,
        Func<string, CancellationToken, Task<AssessmentSummary>> runAssessment,
        Func<string> allocateExportDirectory,
        Action<string> deleteExportDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _workspaceKey = ProjectWorkspaceKey.Compute(project ?? throw new ArgumentNullException(nameof(project)));
        _runInPlace = runInPlace ?? throw new ArgumentNullException(nameof(runInPlace));
        _openCandidateForExport = openCandidateForExport ?? throw new ArgumentNullException(nameof(openCandidateForExport));
        _runAssessment = runAssessment ?? throw new ArgumentNullException(nameof(runAssessment));
        _allocateExportDirectory = allocateExportDirectory ?? throw new ArgumentNullException(nameof(allocateExportDirectory));
        _deleteExportDirectory = deleteExportDirectory ?? throw new ArgumentNullException(nameof(deleteExportDirectory));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Creates the durable job for <paramref name="request"/> and drives it to a terminal state.</summary>
    public async Task<JobRecord> ExecuteAsync(DryRunAssessmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IntentDigest))
            throw new ArgumentException("Required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Baseline);

        var createdUtc = _utcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var inputJson = JsonSerializer.Serialize(
            new PipelineInput(request.ProposalId.Value, request.IntentDigest, request.Baseline, request.IncludeAssessment),
            MotifJson.CreateOptions());
        var job = _jobs.Create(Guid.NewGuid().ToString("N"), _workspaceKey, JobKind, inputJson, createdUtc);
        var jobId = job.JobId;

        var baseline = _baselines.GetCurrent(_workspaceKey) ??
            throw new InvalidOperationException("No published Baseline exists for this project.");
        if (baseline.Token != request.Baseline)
        {
            throw new InvalidOperationException(
                "The requested Baseline is no longer this project's published Baseline.");
        }

        job = _jobs.Transition(job, JobStatus.WaitingForBaseline);
        var lane = _lanes.GetOrCreate(_workspaceKey);

        return request.IncludeAssessment
            ? await RunWithAssessmentAsync(jobId, request, baseline.FwDataPath, baseline.Token, lane, cancellationToken)
                .ConfigureAwait(false)
            : await RunDryRunOnlyAsync(jobId, request, baseline.FwDataPath, baseline.Token, lane, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<JobRecord> RunDryRunOnlyAsync(string jobId, DryRunAssessmentRequest request,
        string baselineFwDataPath, BaselineToken baselineToken, ProjectLane lane, CancellationToken cancellationToken)
    {
        DryRunModel? dryRun = null;
        try
        {
            await lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, laneToken) =>
            {
                _jobs.Transition(_jobs.Get(jobId)!, JobStatus.Running);
                dryRun = await _runInPlace(baselineFwDataPath, request.ProposalId, request.IntentDigest, laneToken)
                    .ConfigureAwait(false);
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return TerminateAfterLaneFailure(jobId, exception);
        }

        if (cancellationToken.IsCancellationRequested) return CancelIfNotTerminal(jobId);

        var beforePublish = _jobs.Get(jobId)!;
        var published = _jobs.PublishDryRun(jobId, DryRunJobHandler.BuildPublishedDryRunJson(dryRun!), beforePublish.Version);
        var completionJson = JsonSerializer.Serialize(
            PipelineCompletion.Skipped(request.IntentDigest, baselineToken), MotifJson.CreateOptions());
        return _jobs.Transition(published, JobStatus.CompletedDryRunOnly, completionJson);
    }

    private async Task<JobRecord> RunWithAssessmentAsync(string jobId, DryRunAssessmentRequest request,
        string baselineFwDataPath, BaselineToken baselineToken, ProjectLane lane, CancellationToken cancellationToken)
    {
        var exportDirectory = _allocateExportDirectory();
        try
        {
            await lane.EnqueueAsync(ProjectWorkItem.CandidateExport(async (_, laneToken) =>
            {
                _jobs.Transition(_jobs.Get(jobId)!, JobStatus.Running);
                using var candidate = await _openCandidateForExport(
                    baselineFwDataPath, request.ProposalId, request.IntentDigest, laneToken).ConfigureAwait(false);

                // Commit the Dry Run before export, so the slow Assessment runs against safe evidence.
                var beforePublish = _jobs.Get(jobId)!;
                _jobs.PublishDryRun(jobId, DryRunJobHandler.BuildPublishedDryRunJson(candidate.DryRun), beforePublish.Version);

                await candidate.ExportAsync(exportDirectory, laneToken).ConfigureAwait(false);
                // candidate.Dispose() below releases the lane immediately after export completes.
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var final = TerminateAfterLaneFailure(jobId, exception);
            _deleteExportDirectory(exportDirectory);
            return final;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CancelIfNotTerminal(jobId);
            _deleteExportDirectory(exportDirectory);
            return cancelled;
        }

        // The lane — and so the next project's own work — is free from here on; only this Assessment remains.
        try
        {
            var summary = await _runAssessment(exportDirectory, cancellationToken).ConfigureAwait(false);
            var current = _jobs.Get(jobId)!;
            var completionJson = JsonSerializer.Serialize(
                PipelineCompletion.Completed(request.IntentDigest, baselineToken, summary), MotifJson.CreateOptions());
            return _jobs.Transition(current, JobStatus.Completed, completionJson);
        }
        catch (OperationCanceledException)
        {
            return CancelIfNotTerminal(jobId);
        }
        catch (Exception exception)
        {
            var current = _jobs.Get(jobId)!;
            var failureJson = JsonSerializer.Serialize(
                PipelineCompletion.ToolFailure(request.IntentDigest, baselineToken, exception.GetType().Name),
                MotifJson.CreateOptions());
            return _jobs.Transition(current, JobStatus.CompletedWithAssessmentFailure, failureJson);
        }
        finally
        {
            _deleteExportDirectory(exportDirectory);
        }
    }

    // Before publication there is nothing to retain; after it, the failure is the Assessment's.
    private JobRecord TerminateAfterLaneFailure(string jobId, Exception exception)
    {
        var current = _jobs.Get(jobId)!;
        if (JobStateMachine.IsTerminal(current.Status)) return current;
        if (exception is OperationCanceledException) return _jobs.Transition(current, JobStatus.Cancelled);
        var failureJson = JsonSerializer.Serialize(
            new PipelineFailure(exception.GetType().Name), MotifJson.CreateOptions());
        var status = current.DryRunPublished ? JobStatus.CompletedWithAssessmentFailure : JobStatus.Failed;
        return _jobs.Transition(current, status, failureJson);
    }

    private JobRecord CancelIfNotTerminal(string jobId)
    {
        var current = _jobs.Get(jobId)!;
        return JobStateMachine.IsTerminal(current.Status) ? current : _jobs.Transition(current, JobStatus.Cancelled);
    }

    private sealed record PipelineInput(
        [property: JsonPropertyName("proposalId")] string ProposalId,
        [property: JsonPropertyName("intentDigest")] string IntentDigest,
        [property: JsonPropertyName("baseline")] BaselineToken Baseline,
        [property: JsonPropertyName("includeAssessment")] bool IncludeAssessment);

    private sealed record PipelineFailure([property: JsonPropertyName("failure")] string Failure);

    private sealed record PipelineCompletion(
        [property: JsonPropertyName("intentDigest")] string IntentDigest,
        [property: JsonPropertyName("baseline")] BaselineToken Baseline,
        [property: JsonPropertyName("assessmentDisposition")] string AssessmentDisposition,
        [property: JsonPropertyName("assessment")] AssessmentSummary? Assessment,
        [property: JsonPropertyName("failure")] string? Failure)
    {
        public static PipelineCompletion Skipped(string intentDigest, BaselineToken baseline) =>
            new(intentDigest, baseline, "skipped", null, null);

        public static PipelineCompletion Completed(string intentDigest, BaselineToken baseline, AssessmentSummary summary) =>
            new(intentDigest, baseline, "completed", summary, null);

        public static PipelineCompletion ToolFailure(string intentDigest, BaselineToken baseline, string failure) =>
            new(intentDigest, baseline, "tool-failure", null, failure);
    }
}
