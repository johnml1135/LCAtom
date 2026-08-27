using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>Covers what the runner's work loop does with a queued row, in-process.</summary>
/// <remarks>
/// The loop's own decisions are covered here where they are cheap to provoke; that a real runner
/// executable performs them is <c>RunnerSpineTests</c>' subject.
/// </remarks>
public sealed class JobRunnerLoopTests : IDisposable
{
    private const string Project = "project-key";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-loop-" + Guid.NewGuid().ToString("N"));
    private readonly MotifDatabase _database;

    public JobRunnerLoopTests()
    {
        Directory.CreateDirectory(_root);
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        _database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public async Task AQueuedJobIsClaimedDispatchedAndCompleted()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "demo", "{}", Stamp());
        var seen = new List<string>();

        await Loop((job, _) => { seen.Add(job.JobId); return Task.CompletedTask; }).RunUntilIdleAsync(
            CancellationToken.None);

        Assert.Equal(new[] { "job-1" }, seen);
        Assert.Equal(JobStatus.Completed, jobs.Get("job-1")!.Status);
    }

    [Fact]
    public async Task AKindWithNoHandlerFailsTheJobRatherThanSpinningOnIt()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "nobody-handles-this", "{}", Stamp());

        var loop = new JobRunnerLoop(new JobClaims(_database), Project, "runner-a",
            TimeSpan.FromMinutes(5),
            TimeSpan.Zero, new Dictionary<string, JobRunnerLoop.Handler>(StringComparer.Ordinal));
        await loop.RunUntilIdleAsync(CancellationToken.None);

        var job = jobs.Get("job-1")!;
        // Failed rather than left queued: a row nothing can run is a row every later poll would re-claim.
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("nobody-handles-this", job.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandlerThatThrowsFailsTheJobCarryingTheReason()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "demo", "{}", Stamp());

        await Loop((_, _) => throw new InvalidOperationException("the capture exploded"))
            .RunUntilIdleAsync(CancellationToken.None);

        var job = jobs.Get("job-1")!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("the capture exploded", job.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLoopHeartbeatsWhileAHandlerRuns()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "demo", "{}", Stamp());
        string? leaseDuringRun = null;

        await Loop(async (job, _) =>
        {
            await Task.Delay(150);
            leaseDuringRun = jobs.Get(job.JobId)!.LeaseUntilUtc;
        }, lease: TimeSpan.FromSeconds(2)).RunUntilIdleAsync(CancellationToken.None);

        Assert.NotNull(leaseDuringRun);
        // Pushed past the claim's own deadline, which is the only thing keeping the row from being taken.
        Assert.True(string.CompareOrdinal(leaseDuringRun, Stamp()) > 0);
    }

    [Fact]
    public async Task TheLoopDoesNotAbandonALeasedJobWhenItStops()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "demo", "{}", Stamp());
        using var stopping = new CancellationTokenSource();

        await Loop(async (_, _) => { stopping.Cancel(); await Task.Delay(50); })
            .RunUntilIdleAsync(stopping.Token);

        // Cancelled mid-handler, the row must still reach a terminal state rather than stay leased.
        Assert.True(jobs.Get("job-1")!.Status is JobStatus.Cancelled or JobStatus.Failed
            or JobStatus.Completed);
    }

    [Fact]
    public async Task AnEmptyQueueIsNotAnError()
    {
        var jobs = new JobRepository(_database);

        await Loop((_, _) => Task.CompletedTask).RunUntilIdleAsync(CancellationToken.None);

        Assert.Empty(jobs.ListActive(Project));
    }

    private JobRunnerLoop Loop(JobRunnerLoop.Handler handler, TimeSpan? lease = null) =>
        new(new JobClaims(_database), Project, "runner-a", lease ?? TimeSpan.FromMinutes(5), TimeSpan.Zero,
            new Dictionary<string, JobRunnerLoop.Handler>(StringComparer.Ordinal) { ["demo"] = handler });

    private static string Stamp() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
