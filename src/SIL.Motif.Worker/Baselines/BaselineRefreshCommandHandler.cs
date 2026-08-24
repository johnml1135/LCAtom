using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;

namespace SIL.Motif.Worker.Baselines;

/// <summary>Runs a durable refresh barrier through live-host presentation and verified capture.</summary>
internal sealed class BaselineRefreshCommandHandler
{
    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly ProjectLaneRegistry _lanes;
    private readonly ProjectHostReleaseCoordinator _hostReleases;
    private readonly Func<ProjectLocator, CancellationToken, Task<WorkerEventResultEnvelope>> _requestHost;
    private readonly Func<CancellationToken, Task<BaselineToken>> _capture;

    internal BaselineRefreshCommandHandler(JobRepository jobs, BaselineRepository baselines,
        ProjectLaneRegistry lanes,
        ProjectHostReleaseCoordinator hostReleases,
        Func<ProjectLocator, CancellationToken, Task<WorkerEventResultEnvelope>> requestHost,
        Func<CancellationToken, Task<BaselineToken>> capture)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        _hostReleases = hostReleases ?? throw new ArgumentNullException(nameof(hostReleases));
        _requestHost = requestHost ?? throw new ArgumentNullException(nameof(requestHost));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    internal async Task RunAsync(string jobId, ProjectLocator project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var job = _jobs.Get(jobId) ?? throw new InvalidOperationException("The refresh job does not exist.");
        if (job.Status != JobStatus.Queued)
            throw new InvalidOperationException("A refresh must begin from a queued job.");
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        if (!StringComparer.Ordinal.Equals(job.Kind, "baseline-refresh") ||
            !StringComparer.Ordinal.Equals(job.ProjectKey, workspaceKey))
            throw new InvalidOperationException("The job does not address this project's Baseline refresh.");
        var lane = _lanes.GetOrCreate(workspaceKey);
        var observedRelease = _hostReleases.Observe(workspaceKey);
        _jobs.Transition(job, JobStatus.WaitingForProjectHost);
        try
        {
            var result = await lane.EnqueueAsync(ProjectWorkItem.Refresh(
                token => CaptureAsync(jobId, project, workspaceKey, observedRelease, token)),
                cancellationToken).ConfigureAwait(false);
            var current = _jobs.Get(jobId)!;
            var json = JsonSerializer.Serialize(new BaselineRefreshCompletion(result.Baseline),
                WorkerJson.CreateOptions());
            _jobs.Transition(current, JobStatus.Completed, json);
        }
        catch (Exception exception)
        {
            var current = _jobs.Get(jobId)!;
            if (JobStateMachine.IsTerminal(current.Status)) return;
            if (current.Status == JobStatus.WaitingForProjectHost)
                current = _jobs.Transition(current, JobStatus.Running);
            var status = exception is OperationCanceledException ? JobStatus.Cancelled : JobStatus.Failed;
            _jobs.Transition(current, status, JsonSerializer.Serialize(
                new BaselineRefreshFailure(exception.GetType().Name), WorkerJson.CreateOptions()));
        }
    }

    private async Task<BaselineToken> CaptureAsync(string jobId, ProjectLocator project,
        string workspaceKey, long observedRelease, CancellationToken cancellationToken)
    {
        WorkerEventResultEnvelope? result = null;
        try
        {
            result = await _requestHost(project, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await _hostReleases.WaitForReleaseAsync(workspaceKey, observedRelease, cancellationToken)
                .ConfigureAwait(false);
        }
        if (result is not null)
        {
            var response = ParseResponse(result);
            RecordResponse(jobId, result.Outcome, response);
            if (result.Outcome == WorkerEventOutcome.Declined)
            {
                var current = _jobs.Get(jobId)!;
                _jobs.Transition(current, JobStatus.Cancelled,
                    JsonSerializer.Serialize(new BaselineRefreshDisposition("declined"),
                        WorkerJson.CreateOptions()));
                throw new RefreshDeclinedException();
            }
            if (result.Outcome == WorkerEventOutcome.Accepted && response.Failure is not null)
                throw new InvalidDataException(response.Failure.Message);
            if (result.Outcome == WorkerEventOutcome.Accepted && response.Publication is not null)
            {
                if (!StringComparer.Ordinal.Equals(response.Publication.ProjectKey, workspaceKey))
                    throw new InvalidDataException("The host published a Baseline for another project workspace.");
                var waiting = _jobs.Get(jobId)!;
                _jobs.Transition(waiting, JobStatus.Running);
                return VerifyPublished(workspaceKey, response.Publication.Token);
            }
            if (result.Outcome is not (WorkerEventOutcome.Accepted or WorkerEventOutcome.Deferred))
                throw new InvalidDataException("The refresh host returned an invalid disposition.");
            await _hostReleases.WaitForReleaseAsync(workspaceKey, observedRelease, cancellationToken)
                .ConfigureAwait(false);
        }
        var currentJob = _jobs.Get(jobId)!;
        _jobs.Transition(currentJob, JobStatus.Running);
        var captured = await _capture(cancellationToken).ConfigureAwait(false);
        return VerifyPublished(workspaceKey, captured);
    }

    private void RecordResponse(string jobId, WorkerEventOutcome outcome, BaselineRefreshHostResult response)
    {
        var current = _jobs.Get(jobId)!;
        var progress = new BaselineRefreshProgress(response.Actor, outcome.ToString().ToLowerInvariant(),
            response.Reason, response.RespondedUtc, response.Publication, response.Failure);
        _jobs.UpdateProgress(jobId, JsonSerializer.Serialize(progress, WorkerJson.CreateOptions()),
            current.Version);
    }

    private BaselineToken VerifyPublished(string workspaceKey, BaselineToken claimed)
    {
        var durable = _baselines.GetCurrent(workspaceKey);
        if (durable is null || !Equals(durable.Token, claimed))
            throw new InvalidDataException("The refresh result does not match the durable Baseline publication.");
        return durable.Token;
    }

    private static BaselineRefreshHostResult ParseResponse(WorkerEventResultEnvelope result)
    {
        if (result.Outcome is not (WorkerEventOutcome.Accepted or WorkerEventOutcome.Deferred or
                WorkerEventOutcome.Declined))
            throw new InvalidDataException("The refresh host returned an invalid disposition.");
        var response = result.Payload.Deserialize<BaselineRefreshHostResult>(WorkerJson.CreateOptions()) ??
            throw new InvalidDataException("The refresh host returned no result.");
        if (result.Outcome != WorkerEventOutcome.Accepted &&
            (response.Publication is not null || response.Failure is not null))
            throw new InvalidDataException("Only an accepted refresh may contain a capture result.");
        return response;
    }

    private sealed record BaselineRefreshProgress(
        [property: JsonPropertyName("actor")] string Actor,
        [property: JsonPropertyName("response")] string Response,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("respondedUtc")] string RespondedUtc,
        [property: JsonPropertyName("publication")] BaselinePublicationResult? Publication,
        [property: JsonPropertyName("failure")] BaselineCommandFailure? Failure);
    private sealed record BaselineRefreshCompletion(BaselineToken Token);
    private sealed record BaselineRefreshFailure(string Failure);
    private sealed record BaselineRefreshDisposition(string RefreshDisposition);
    private sealed class RefreshDeclinedException : OperationCanceledException;
}
