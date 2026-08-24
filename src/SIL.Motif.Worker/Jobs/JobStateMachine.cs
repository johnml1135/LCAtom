using System.Globalization;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Determines the durable lifecycle and timestamp rules for one job attempt.</summary>
public sealed class JobStateMachine
{
    private const string CancelledAssessmentResult = "{\"assessmentDisposition\":\"cancelled\"}";
    private static readonly IReadOnlyDictionary<JobStatus, IReadOnlySet<JobStatus>> Transitions =
        new Dictionary<JobStatus, IReadOnlySet<JobStatus>>
        {
            [JobStatus.Queued] = new HashSet<JobStatus>
                { JobStatus.WaitingForBaseline, JobStatus.WaitingForProjectHost, JobStatus.Running, JobStatus.Cancelled },
            [JobStatus.WaitingForBaseline] = new HashSet<JobStatus>
                { JobStatus.Queued, JobStatus.Failed, JobStatus.Cancelled },
            [JobStatus.WaitingForProjectHost] = new HashSet<JobStatus>
                { JobStatus.Queued, JobStatus.Running, JobStatus.Cancelled },
            [JobStatus.Running] = new HashSet<JobStatus>
                { JobStatus.Completed, JobStatus.CompletedDryRunOnly,
                    JobStatus.CompletedWithAssessmentFailure, JobStatus.Failed, JobStatus.Cancelled, JobStatus.Interrupted },
            [JobStatus.Interrupted] = new HashSet<JobStatus>(),
            [JobStatus.Completed] = new HashSet<JobStatus>(),
            [JobStatus.CompletedDryRunOnly] = new HashSet<JobStatus>(),
            [JobStatus.CompletedWithAssessmentFailure] = new HashSet<JobStatus>(),
            [JobStatus.Failed] = new HashSet<JobStatus>(),
            [JobStatus.Cancelled] = new HashSet<JobStatus>()
        };

    private readonly IJobClock _clock;

    public JobStateMachine(IJobClock? clock = null) => _clock = clock ?? new SystemJobClock();

    public static bool IsTerminal(JobStatus status) => status is JobStatus.Completed or JobStatus.CompletedDryRunOnly or
        JobStatus.CompletedWithAssessmentFailure or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Interrupted;

    public static IReadOnlySet<JobStatus> LegalNextStatuses(JobStatus status) =>
        Transitions.TryGetValue(status, out var next) ? next : throw new ArgumentOutOfRangeException(nameof(status));

    public JobRecord Transition(JobRecord current, JobStatus next, string? resultJson = null)
    {
        ParseUtc(current.CreatedUtc);
        if (!Transitions.TryGetValue(current.Status, out var legal))
            throw new ArgumentOutOfRangeException(nameof(current), "The current status is not a known job status.");
        if (current.Status == next || !legal.Contains(next))
            throw new InvalidOperationException($"Job transition {JobStatusJson.ToWire(current.Status)} -> {JobStatusJson.ToWire(next)} is not legal.");
        if (!IsTerminal(next) && resultJson is not null)
            throw new InvalidOperationException("A nonterminal job cannot record a result.");
        if (current.CancellationRequested && next is not (JobStatus.Cancelled or JobStatus.Interrupted))
            throw new InvalidOperationException("A cancellation-requested job cannot advance without cancellation.");
        if ((next is JobStatus.CompletedDryRunOnly or JobStatus.CompletedWithAssessmentFailure) && !current.DryRunPublished)
            throw new InvalidOperationException("An Assessment outcome requires a published Dry Run.");
        if (next == JobStatus.Cancelled && current.DryRunPublished && resultJson is not null && resultJson != CancelledAssessmentResult)
            throw new InvalidOperationException("Cancellation after Dry Run requires the canonical cancelled Assessment disposition.");
        if (resultJson is not null) JobJson.ValidateStructured(resultJson, nameof(resultJson));
        return current with
        {
            Status = next,
            ResultJson = next == JobStatus.Cancelled && current.DryRunPublished
                ? CancelledAssessmentResult : resultJson ?? current.ResultJson,
            UpdatedUtc = LaterUtc(current.UpdatedUtc),
            Version = checked(current.Version + 1)
        };
    }

    public JobRecord RequestCancellation(JobRecord current)
    {
        if (IsTerminal(current.Status))
            throw new InvalidOperationException("A terminal job cannot accept a cancellation request.");
        if (current.CancellationRequested) return current;
        return current with { CancellationRequested = true, UpdatedUtc = LaterUtc(current.UpdatedUtc), Version = checked(current.Version + 1) };
    }

    public JobRecord UpdateProgress(JobRecord current, string progressJson)
    {
        JobJson.ValidateStructured(progressJson, nameof(progressJson));
        if (current.Status == JobStatus.Queued) throw new InvalidOperationException("A queued job cannot update progress.");
        if (IsTerminal(current.Status)) throw new InvalidOperationException("A terminal job cannot update progress.");
        return current with { ProgressJson = progressJson, UpdatedUtc = LaterUtc(current.UpdatedUtc), Version = checked(current.Version + 1) };
    }

    public JobRecord PublishDryRun(JobRecord current, string resultJson)
    {
        JobJson.ValidateStructured(resultJson, nameof(resultJson));
        if (current.Status != JobStatus.Running)
            throw new InvalidOperationException("Only a running job can publish a Dry Run.");
        if (current.CancellationRequested || current.DryRunPublished)
            throw new InvalidOperationException("A cancelled or already published job cannot publish a Dry Run.");
        return current with
        {
            DryRunPublished = true,
            DryRunJson = resultJson,
            UpdatedUtc = LaterUtc(current.UpdatedUtc),
            Version = checked(current.Version + 1)
        };
    }

    public JobRecord Retry(JobRecord terminal, string newJobId)
    {
        if (!IsTerminal(terminal.Status))
            throw new InvalidOperationException("Only a terminal job may be explicitly retried.");
        if (string.IsNullOrWhiteSpace(newJobId)) throw new ArgumentException("A new job id is required.", nameof(newJobId));
        var now = LaterUtc(terminal.UpdatedUtc);
        return new JobRecord(newJobId, terminal.ProjectKey, terminal.Kind, JobStatus.Queued, checked(terminal.Attempt + 1),
            terminal.InputJson, null, now, now, null, terminal.LogicalJobId, false, 0, false, null);
    }

    private string LaterUtc(string current)
    {
        var previous = ParseUtc(current);
        var now = _clock.UtcNow.ToUniversalTime();
        return (now < previous ? previous : now).ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero ||
            !(value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new ArgumentException("Job timestamps must be valid UTC timestamps.", nameof(value));
        return parsed;
    }
}
