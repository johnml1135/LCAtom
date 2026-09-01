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
    private readonly BaselineRepository _baselines;
    private readonly ProposalRepository _proposals;
    private readonly ProjectLaneRegistry _lanes;
    private readonly Func<string, ProjectFreshnessTracker?> _freshnessTrackers;
    private readonly Func<string, CancellationToken, Task<(IReadOnlyCollection<Guid> AppliedProposalIds, DryRunScratch? Scratch)>> _openScratch;
    private readonly Func<DryRunScratch?, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> _runDryRun;

    /// <param name="baselines">This project's published-Baseline store.</param>
    /// <param name="proposals">
    /// This project's Proposal store, used only to resolve the requested Proposal's prerequisite
    /// closure (<see cref="ProposalRepository.PlanPrerequisites"/>) — never to read or write the
    /// requested Proposal itself, which the job's own <c>InputJson</c> already carries.
    /// </param>
    /// <param name="lanes">Serializes Baseline-dependent work per project.</param>
    /// <param name="freshnessTrackers">Looks up a live observation to compare the Baseline against, if any.</param>
    /// <param name="openScratch">
    /// Opens the one scratch this Dry Run measures against and reads which Proposals are already
    /// applied in it — the Baseline-derived cache, not the live project — via
    /// <see cref="DryRunScratch.PeekCache"/>, so <see cref="ProposalRepository.PlanPrerequisites"/> knows
    /// which of the requested Proposal's prerequisites still need pre-applying. The scratch is returned
    /// so <paramref name="runDryRun"/> reuses this same open rather than opening a second one; it is
    /// disposed once <paramref name="runDryRun"/> returns. Nullable only so a test double that never
    /// touches a real cache can return none.
    /// </param>
    /// <param name="runDryRun">Runs the resolved plan against the scratch <paramref name="openScratch"/> opened.</param>
    internal DryRunJobHandler(BaselineRepository baselines, ProposalRepository proposals,
        ProjectLaneRegistry lanes, Func<string, ProjectFreshnessTracker?> freshnessTrackers,
        Func<string, CancellationToken, Task<(IReadOnlyCollection<Guid> AppliedProposalIds, DryRunScratch? Scratch)>> openScratch,
        Func<DryRunScratch?, PrerequisiteExecutionPlan, CancellationToken, Task<DryRunModel>> runDryRun)
    {
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _freshnessTrackers = freshnessTrackers ?? throw new ArgumentNullException(nameof(freshnessTrackers));
        _openScratch = openScratch ?? throw new ArgumentNullException(nameof(openScratch));
        _runDryRun = runDryRun ?? throw new ArgumentNullException(nameof(runDryRun));
    }

    /// <summary>
    /// Runs one job the loop has already claimed. A terminal outcome is returned for the loop to finish
    /// with; a non-terminal parked row (no published Baseline, or a Baseline-dependent lane still busy)
    /// is left exactly there and this returns <c>null</c>, since <see cref="JobClaims.Claim"/> only ever
    /// reclaims a <c>queued</c> or lease-expired <c>running</c> row — nothing moves a parked row again.
    /// </summary>
    internal async Task<JobOutcome?> RunAsync(ClaimedJob claim, ProjectLocator project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Job.Status != JobStatus.Running)
            throw new InvalidOperationException("A Dry Run must already be claimed by a runner.");
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        if (!StringComparer.Ordinal.Equals(claim.Job.Kind, "dry-run") ||
            !StringComparer.Ordinal.Equals(claim.Job.ProjectKey, workspaceKey))
            throw new InvalidOperationException("The job does not address this project's Dry Run.");

        var baseline = _baselines.GetCurrent(workspaceKey);
        if (baseline is null)
        {
            claim.Transition(JobStatus.WaitingForBaseline);
            return null;
        }

        var proposal = ProposalJsonParser.Parse(claim.Job.InputJson);
        var lane = _lanes.GetOrCreate(workspaceKey);

        // Reported while queued behind the lane; the lane's own dispatch resumes this directly into Running.
        claim.Transition(JobStatus.WaitingForBaseline);

        DryRunModel? dryRun = null;
        await lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, laneToken) =>
        {
            claim.Transition(JobStatus.Running);
            var (appliedProposalIds, scratch) = await _openScratch(baseline.FwDataPath, laneToken)
                .ConfigureAwait(false);
            try
            {
                var plan = _proposals.PlanPrerequisites(proposal, appliedProposalIds);
                dryRun = await _runDryRun(scratch, plan, laneToken).ConfigureAwait(false);
            }
            finally
            {
                scratch?.Dispose();
            }
        }),
            cancellationToken).ConfigureAwait(false);

        // The run already happened; cancellation now must still stop it from becoming durable.
        if (cancellationToken.IsCancellationRequested)
            return new JobOutcome(JobStatus.Cancelled, JobFailureCategory.Cancellation);

        var freshness = _freshnessTrackers(workspaceKey)?.Check(baseline.Token) ?? BaselineFreshness.CurrentnessNotChecked;
        var publishedJson = BuildPublishedDryRunJson(dryRun!);
        claim.PublishDryRun(publishedJson);
        var completionJson = JsonSerializer.Serialize(
            new DryRunCompletion(baseline.Token, baseline.Token.CapturedUtc, ToWire(freshness)),
            MotifJson.CreateOptions());
        return new JobOutcome(JobStatus.CompletedDryRunOnly, JobFailureCategory.None, completionJson);
    }

    /// <summary>The canonical published-Dry-Run wire shape, shared with <see cref="TrialJobHandler"/>.</summary>
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
