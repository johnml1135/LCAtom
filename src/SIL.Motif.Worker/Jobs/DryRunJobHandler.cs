using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// Runs a Proposal against the current published Baseline on a single-use scratch, opened and disposed
/// inside the project's lane, and publishes the resulting Dry Run without ever refreshing the Baseline.
/// </summary>
internal sealed class DryRunJobHandler
{
    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly ProjectLaneRegistry _lanes;
    private readonly Func<string, ProjectFreshnessTracker?> _freshnessTrackers;
    private readonly Func<string, Proposal, CancellationToken, Task<DryRunModel>> _runDryRun;

    internal DryRunJobHandler(JobRepository jobs, BaselineRepository baselines, ProjectLaneRegistry lanes,
        Func<string, ProjectFreshnessTracker?> freshnessTrackers,
        Func<string, Proposal, CancellationToken, Task<DryRunModel>> runDryRun)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _freshnessTrackers = freshnessTrackers ?? throw new ArgumentNullException(nameof(freshnessTrackers));
        _runDryRun = runDryRun ?? throw new ArgumentNullException(nameof(runDryRun));
    }

    internal async Task RunAsync(string jobId, ProjectLocator project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var job = _jobs.Get(jobId) ?? throw new InvalidOperationException("The Dry Run job does not exist.");
        if (job.Status != JobStatus.Queued)
            throw new InvalidOperationException("A Dry Run must begin from a queued job.");
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        if (!StringComparer.Ordinal.Equals(job.Kind, "dry-run") ||
            !StringComparer.Ordinal.Equals(job.ProjectKey, workspaceKey))
            throw new InvalidOperationException("The job does not address this project's Dry Run.");
        var baseline = _baselines.GetCurrent(workspaceKey) ??
            throw new InvalidOperationException("No published Baseline exists for this project.");
        var proposal = ProposalJsonParser.Parse(job.InputJson);
        var lane = _lanes.GetOrCreate(workspaceKey);

        job = _jobs.Transition(job, JobStatus.WaitingForBaseline);

        DryRunModel? dryRun = null;
        try
        {
            await lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, laneToken) =>
            {
                // A closed barrier can park this job, and a released wait resumes directly into Running.
                _jobs.Transition(_jobs.Get(jobId)!, JobStatus.Running);
                dryRun = await _runDryRun(baseline.FwDataPath, proposal, laneToken).ConfigureAwait(false);
            }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var current = _jobs.Get(jobId)!;
            if (JobStateMachine.IsTerminal(current.Status)) return;
            var status = exception is OperationCanceledException ? JobStatus.Cancelled : JobStatus.Failed;
            _jobs.Transition(current, status,
                JsonSerializer.Serialize(new DryRunFailure(exception.GetType().Name), WorkerJson.CreateOptions()));
            return;
        }

        // The run already happened; cancellation now must still stop it from becoming durable.
        if (cancellationToken.IsCancellationRequested)
        {
            var current = _jobs.Get(jobId)!;
            if (!JobStateMachine.IsTerminal(current.Status)) _jobs.Transition(current, JobStatus.Cancelled);
            return;
        }

        var freshness = _freshnessTrackers(workspaceKey)?.Check(baseline.Token) ?? BaselineFreshness.CurrentnessNotChecked;
        var publishedJson = BuildPublishedDryRunJson(dryRun!);
        var beforePublish = _jobs.Get(jobId)!;
        var published = _jobs.PublishDryRun(jobId, publishedJson, beforePublish.Version);
        var completionJson = JsonSerializer.Serialize(
            new DryRunCompletion(baseline.Token, baseline.Token.CapturedUtc, ToWire(freshness)),
            WorkerJson.CreateOptions());
        _jobs.Transition(published, JobStatus.CompletedDryRunOnly, completionJson);
    }

    /// <summary>The canonical published-Dry-Run wire shape, shared with <see cref="DryRunAssessmentPipeline"/>.</summary>
    internal static string BuildPublishedDryRunJson(DryRunModel dryRun) =>
        JsonSerializer.Serialize(BuildPublishedDryRun(dryRun), WorkerJson.CreateOptions());

    private static PublishedDryRun BuildPublishedDryRun(DryRunModel dryRun)
    {
        using var document = JsonDocument.Parse(ExpectedEffectSetJsonWriter.WriteJson(dryRun.ExpectedEffects));
        return new PublishedDryRun(dryRun.IntentDigest, dryRun.BaselineNote, document.RootElement.Clone(),
            dryRun.EffectDigest, dryRun.Anchor);
    }

    private static string ToWire(BaselineFreshness freshness) => freshness switch
    {
        BaselineFreshness.Current => "current",
        BaselineFreshness.KnownOld => "known-old",
        BaselineFreshness.CurrentnessNotChecked => "currentness-not-checked",
        _ => throw new ArgumentOutOfRangeException(nameof(freshness))
    };

    private sealed record PublishedDryRun(
        [property: JsonPropertyName("intentDigest")] string IntentDigest,
        [property: JsonPropertyName("baselineNote")] string BaselineNote,
        [property: JsonPropertyName("expectedEffects")] JsonElement ExpectedEffects,
        [property: JsonPropertyName("effectDigest")] string EffectDigest,
        [property: JsonPropertyName("anchor")] BoundDryRunAnchor Anchor);

    private sealed record DryRunCompletion(
        [property: JsonPropertyName("baselineToken")] BaselineToken BaselineToken,
        [property: JsonPropertyName("capturedUtc")] string CapturedUtc,
        [property: JsonPropertyName("freshness")] string Freshness);

    private sealed record DryRunFailure(string Failure);
}
