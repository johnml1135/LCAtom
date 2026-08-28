using SIL.Motif.Contract;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;

using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
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
    private readonly ProposalRepository _proposals;
    private readonly ProjectLaneRegistry _lanes;
    private readonly Func<string, ProjectFreshnessTracker?> _freshnessTrackers;
    private readonly Func<string, CancellationToken, Task<IReadOnlyCollection<Guid>>> _readAppliedProposalIds;
    private readonly Func<string, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> _runDryRun;

    /// <param name="jobs">This project's job store.</param>
    /// <param name="baselines">This project's published-Baseline store.</param>
    /// <param name="proposals">
    /// This project's Proposal store, used only to resolve the requested Proposal's prerequisite
    /// closure (<see cref="ProposalRepository.PlanPrerequisites"/>) — never to read or write the
    /// requested Proposal itself, which the job's own <c>InputJson</c> already carries.
    /// </param>
    /// <param name="lanes">Serializes Baseline-dependent work per project.</param>
    /// <param name="freshnessTrackers">Looks up a live observation to compare the Baseline against, if any.</param>
    /// <param name="readAppliedProposalIds">
    /// Reads which Proposals are already applied in the state the Dry Run measures against — the
    /// Baseline-derived cache, not the live project — so <see cref="ProposalRepository.PlanPrerequisites"/>
    /// knows which of the requested Proposal's prerequisites still need pre-applying to the scratch.
    /// </param>
    /// <param name="runDryRun">Opens a single-use scratch from the published Baseline and runs the resolved plan.</param>
    internal DryRunJobHandler(JobRepository jobs, BaselineRepository baselines, ProposalRepository proposals,
        ProjectLaneRegistry lanes, Func<string, ProjectFreshnessTracker?> freshnessTrackers,
        Func<string, CancellationToken, Task<IReadOnlyCollection<Guid>>> readAppliedProposalIds,
        Func<string, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> runDryRun)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _freshnessTrackers = freshnessTrackers ?? throw new ArgumentNullException(nameof(freshnessTrackers));
        _readAppliedProposalIds = readAppliedProposalIds ?? throw new ArgumentNullException(nameof(readAppliedProposalIds));
        _runDryRun = runDryRun ?? throw new ArgumentNullException(nameof(runDryRun));
    }

    /// <summary>
    /// Runs one job the loop has already claimed. A terminal outcome is returned for the loop to finish
    /// with; a non-terminal parked row (no published Baseline, or a Baseline-dependent lane still busy)
    /// is left exactly there and this returns <c>null</c>, since <see cref="JobClaims.Claim"/> only ever
    /// reclaims a <c>queued</c> or lease-expired <c>running</c> row — nothing moves a parked row again.
    /// </summary>
    internal async Task<JobOutcome?> RunAsync(string jobId, ProjectLocator project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var job = _jobs.Get(jobId) ?? throw new InvalidOperationException("The Dry Run job does not exist.");
        if (job.Status != JobStatus.Running)
            throw new InvalidOperationException("A Dry Run must already be claimed by a runner.");
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        if (!StringComparer.Ordinal.Equals(job.Kind, "dry-run") ||
            !StringComparer.Ordinal.Equals(job.ProjectKey, workspaceKey))
            throw new InvalidOperationException("The job does not address this project's Dry Run.");

        var baseline = _baselines.GetCurrent(workspaceKey);
        if (baseline is null)
        {
            _jobs.Transition(job, JobStatus.WaitingForBaseline);
            return null;
        }

        var proposal = ProposalJsonParser.Parse(job.InputJson);
        var lane = _lanes.GetOrCreate(workspaceKey);

        // Reported while queued behind the lane; the lane's own dispatch resumes this directly into Running.
        _jobs.Transition(job, JobStatus.WaitingForBaseline);

        DryRunModel? dryRun = null;
        await lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, laneToken) =>
        {
            _jobs.Transition(_jobs.Get(jobId)!, JobStatus.Running);
            var appliedProposalIds = await _readAppliedProposalIds(baseline.FwDataPath, laneToken)
                .ConfigureAwait(false);
            var plan = _proposals.PlanPrerequisites(proposal, appliedProposalIds);
            dryRun = await _runDryRun(baseline.FwDataPath, plan, laneToken).ConfigureAwait(false);
        }),
            cancellationToken).ConfigureAwait(false);

        // The run already happened; cancellation now must still stop it from becoming durable.
        if (cancellationToken.IsCancellationRequested)
            return new JobOutcome(JobStatus.Cancelled, JobFailureCategory.Cancellation);

        var freshness = _freshnessTrackers(workspaceKey)?.Check(baseline.Token) ?? BaselineFreshness.CurrentnessNotChecked;
        var publishedJson = BuildPublishedDryRunJson(dryRun!);
        var beforePublish = _jobs.Get(jobId)!;
        _jobs.PublishDryRun(jobId, publishedJson, beforePublish.Version);
        var completionJson = JsonSerializer.Serialize(
            new DryRunCompletion(baseline.Token, baseline.Token.CapturedUtc, ToWire(freshness)),
            MotifJson.CreateOptions());
        return new JobOutcome(JobStatus.CompletedDryRunOnly, JobFailureCategory.None, completionJson);
    }

    /// <summary>The canonical published-Dry-Run wire shape, shared with <see cref="DryRunAssessmentPipeline"/>.</summary>
    internal static string BuildPublishedDryRunJson(DryRunModel dryRun) =>
        JsonSerializer.Serialize(BuildPublishedDryRun(dryRun), MotifJson.CreateOptions());

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
}
