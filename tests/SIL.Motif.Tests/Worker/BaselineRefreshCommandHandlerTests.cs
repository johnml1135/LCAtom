using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class BaselineRefreshCommandHandlerTests : IDisposable
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-refresh-" + Guid.NewGuid().ToString("N"));

    public BaselineRefreshCommandHandlerTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(WorkerEventOutcome.Declined, JobStatus.Cancelled)]
    [InlineData(WorkerEventOutcome.Deferred, JobStatus.WaitingForProjectHost)]
    public async Task HostDispositionIsRecordedDurablyWithoutReleasingTheBarrier(
        WorkerEventOutcome outcome, JobStatus expected)
    {
        using var context = Context("disposition-" + outcome);
        using var lanes = Lanes(context);
        var lane = lanes.GetOrCreate(ProjectWorkspaceKey.Compute(context.Project));
        using var releases = new ProjectHostReleaseCoordinator();
        var response = Result(outcome, "linguist", "not now");
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) => Task.FromResult(response), _ => Task.FromResult(Token('b')));

        var run = handler.RunAsync(context.Job.JobId, context.Project, CancellationToken.None);
        if (outcome == WorkerEventOutcome.Deferred)
        {
            await WaitForStatus(context.Jobs, context.Job.JobId, JobStatus.WaitingForProjectHost);
            await WaitForProgress(context.Jobs, context.Job.JobId);
            Assert.False(run.IsCompleted);
        }
        else
        {
            await run;
        }
        var durable = context.Jobs.Get(context.Job.JobId)!;
        Assert.Equal(expected, durable.Status);
        Assert.Contains("\"actor\":\"linguist\"", durable.ProgressJson, StringComparison.Ordinal);
        Assert.Contains("\"response\":\"" + outcome.ToString().ToLowerInvariant() + "\"",
            durable.ProgressJson, StringComparison.Ordinal);

        var laterStarted = false;
        var later = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            laterStarted = true;
            return Task.CompletedTask;
        }), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(laterStarted);
        Assert.False(later.IsCompleted);
    }

    [Fact]
    public async Task DeferredRefreshTransfersToCliCaptureAfterHostAuthorityIsReleased()
    {
        using var context = Context("deferred");
        using var lanes = Lanes(context);
        using var releases = new ProjectHostReleaseCoordinator();
        var captureStarted = false;
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) => Task.FromResult(Result(WorkerEventOutcome.Deferred, "linguist", "after close")),
            _ =>
            {
                captureStarted = true;
                return Task.FromResult(Publish(context, Token('b')));
            });
        var run = handler.RunAsync(context.Job.JobId, context.Project, CancellationToken.None);
        await WaitForStatus(context.Jobs, context.Job.JobId, JobStatus.WaitingForProjectHost);

        releases.NotifyReleased(ProjectWorkspaceKey.Compute(context.Project));
        await run;

        Assert.True(captureStarted);
        Assert.Equal(JobStatus.Completed, context.Jobs.Get(context.Job.JobId)!.Status);
    }

    [Fact]
    public async Task AcceptedRefreshRecordsTheReplacementAndReleasesLaterWork()
    {
        using var context = Context("accepted");
        using var lanes = Lanes(context);
        var lane = lanes.GetOrCreate(ProjectWorkspaceKey.Compute(context.Project));
        using var releases = new ProjectHostReleaseCoordinator();
        var captureStarted = false;
        Publish(context, Token('b'));
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) => Task.FromResult(Result(WorkerEventOutcome.Accepted, "linguist", null,
                new BaselinePublicationResult(ProjectWorkspaceKey.Compute(context.Project), Token('b')))),
            _ =>
            {
                captureStarted = true;
                return Task.FromResult(Token('c'));
            });

        await handler.RunAsync(context.Job.JobId, context.Project, CancellationToken.None);
        var durable = context.Jobs.Get(context.Job.JobId)!;
        Assert.Equal(JobStatus.Completed, durable.Status);
        Assert.False(captureStarted);
        Assert.Contains(Token('b').BundleDigest, durable.ResultJson, StringComparison.Ordinal);
        var later = await lane.EnqueueAsync(ProjectWorkItem.DryRun((token, _) =>
        {
            Assert.Equal(Token('b'), token);
            return Task.CompletedTask;
        }), CancellationToken.None);
        Assert.Equal(Token('b'), later.Baseline);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException), JobStatus.Cancelled)]
    [InlineData(typeof(IOException), JobStatus.Failed)]
    [InlineData(typeof(InvalidDataException), JobStatus.Failed)]
    public async Task CaptureCancellationSaveTransferOrVerificationFailureKeepsBarrierClosed(
        Type failureType, JobStatus expected)
    {
        using var context = Context("failure-" + failureType.Name);
        using var lanes = Lanes(context);
        var lane = lanes.GetOrCreate(ProjectWorkspaceKey.Compute(context.Project));
        using var releases = new ProjectHostReleaseCoordinator();
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) =>
            {
                releases.NotifyReleased(ProjectWorkspaceKey.Compute(context.Project));
                return Task.FromResult(Result(WorkerEventOutcome.Accepted, "host", null));
            },
            _ => Task.FromException<BaselineToken>((Exception)Activator.CreateInstance(failureType)!));

        await handler.RunAsync(context.Job.JobId, context.Project, CancellationToken.None);

        Assert.Equal(expected, context.Jobs.Get(context.Job.JobId)!.Status);
        var later = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) => Task.CompletedTask), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(later.IsCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandlerRejectsWrongJobKindOrProjectBeforeClosingALane(bool wrongKind)
    {
        using var context = Context("wrong-job", wrongKind ? "dry-run" : "baseline-refresh",
            wrongKind ? null : "another-project-key");
        using var lanes = Lanes(context);
        using var releases = new ProjectHostReleaseCoordinator();
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) => throw new Xunit.Sdk.XunitException("Host must not be requested."),
            _ => throw new Xunit.Sdk.XunitException("Capture must not start."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.RunAsync(
            context.Job.JobId, context.Project, CancellationToken.None));

        Assert.Equal(JobStatus.Queued, context.Jobs.Get(context.Job.JobId)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecordedOrMismatchedHostPublicationCannotAdvanceTheLane(bool recordMismatch)
    {
        using var context = Context("host-verification-" + recordMismatch);
        using var lanes = Lanes(context);
        var lane = lanes.GetOrCreate(ProjectWorkspaceKey.Compute(context.Project));
        using var releases = new ProjectHostReleaseCoordinator();
        if (recordMismatch) Publish(context, Token('c'));
        var handler = new BaselineRefreshCommandHandler(context.Jobs, context.Baselines, lanes, releases,
            (_, _) => Task.FromResult(Result(WorkerEventOutcome.Accepted, "host", null,
                new BaselinePublicationResult(ProjectWorkspaceKey.Compute(context.Project), Token('b')))),
            _ => throw new Xunit.Sdk.XunitException("CLI capture must not replace host verification."));

        await handler.RunAsync(context.Job.JobId, context.Project, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, context.Jobs.Get(context.Job.JobId)!.Status);
        var later = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) => Task.CompletedTask),
            CancellationToken.None);
        await Task.Delay(50);
        Assert.False(later.IsCompleted);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private TestContext Context(string name, string kind = "baseline-refresh", string? projectKey = null)
    {
        var project = new ProjectLocator(Path.Combine(_root, name + ".fwdata"), name);
        var database = MotifDatabase.OpenOwned(Path.Combine(_root, name + ".motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var job = jobs.Create(Guid.NewGuid().ToString("N"), projectKey ?? ProjectWorkspaceKey.Compute(project),
            kind, "{}",
            "2026-08-24T00:00:00Z");
        return new TestContext(project, database, jobs, baselines, job);
    }

    private static async Task WaitForStatus(JobRepository jobs, string jobId, JobStatus status)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Get(jobId)!.Status != status) await Task.Delay(10, cancellation.Token);
    }

    private static async Task WaitForProgress(JobRepository jobs, string jobId)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Get(jobId)!.ProgressJson is null) await Task.Delay(10, cancellation.Token);
    }

    private static WorkerEventResultEnvelope Result(WorkerEventOutcome outcome, string actor, string? reason,
        BaselinePublicationResult? publication = null, BaselineCommandFailure? failure = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new BaselineRefreshHostResult(actor, reason, "2026-08-24T00:00:00Z", publication, failure),
            WorkerJson.CreateOptions()));
        return new WorkerEventResultEnvelope("event", outcome, document.RootElement.Clone(), 1);
    }

    private static BaselineToken Token(char value) => new(
        "project", "sha256:" + new string(value, 64), "1", "2026-08-24T00:00:00Z",
        "sha256:" + new string(value, 64));

    private ProjectLaneRegistry Lanes(TestContext context) =>
        new(_ => Token('a'));

    private BaselineToken Publish(TestContext context, BaselineToken token)
    {
        var root = Path.Combine(_root, "published-" + token.BundleDigest[^1]);
        Directory.CreateDirectory(root);
        context.Baselines.Record(ProjectWorkspaceKey.Compute(context.Project),
            new BaselinePublication(root, Path.Combine(root, "project.fwdata"), token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        return token;
    }

    private sealed record TestContext(ProjectLocator Project, MotifDatabase Database,
        JobRepository Jobs, BaselineRepository Baselines, JobRecord Job) : IDisposable
    {
        public void Dispose() => Database.Dispose();
    }
}
