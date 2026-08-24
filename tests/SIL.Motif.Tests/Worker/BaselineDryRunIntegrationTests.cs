using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SIL.LCModel;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Baselines;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Store;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using Xunit;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Proves the plan's Task 4 promise at the job-handler layer: twenty Dry Runs against one published
/// Baseline are honest, independent, and never touch the canonical saved project a second time.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class BaselineDryRunIntegrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.BaselineDryRunIntegrationTests", Guid.NewGuid().ToString("N"));
    private readonly string _publishedRoot;
    private readonly SeededProject _seed;
    private readonly CountingFwDataProjectLoader _loader = new();
    private readonly ProjectLocator _project;
    private readonly MotifDatabase _database;
    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly BaselineToken _token;
    private readonly string _proposalJson;

    public BaselineDryRunIntegrationTests()
    {
        Directory.CreateDirectory(_root);

        var masterRoot = Path.Combine(_root, "master");
        Directory.CreateDirectory(masterRoot);
        var master = NewLangProjFixture.CreateCache(masterRoot);
        try
        {
            _seed = SeededProject.Seed(master);
            _loader.Save(master);

            _publishedRoot = Path.Combine(_root, "published");
            Directory.CreateDirectory(_publishedRoot);
            using (var bundle = new MemoryStream())
            {
                new BaselineBundleWriter().WriteAsync(master, bundle, CancellationToken.None)
                    .GetAwaiter().GetResult();
                using var archive = new ZipArchive(new MemoryStream(bundle.ToArray()), ZipArchiveMode.Read);
                archive.ExtractToDirectory(_publishedRoot);
            }
            // Mirrors what BaselineBundleReceiver pre-creates, so this fixture is a correctly published Baseline.
            Directory.CreateDirectory(Path.Combine(_publishedRoot, "WritingSystemStore"));
            Directory.CreateDirectory(Path.Combine(_publishedRoot, "SharedSettings"));
        }
        finally
        {
            master.Dispose();
        }
        Directory.Delete(masterRoot, recursive: true);

        var fwDataPath = Path.Combine(_publishedRoot, NewLangProjFixture.ProjectName + ".fwdata");
        _project = new ProjectLocator(Path.Combine(_root, "live", NewLangProjFixture.ProjectName + ".fwdata"),
            "live-project-identity");

        _database = MotifDatabase.OpenOwned(Path.Combine(_root, "motif.db"), _project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        _jobs = new JobRepository(_database);
        _baselines = new BaselineRepository(_database);

        _token = new BaselineToken("live-project-identity", "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64));
        _baselines.Record(ProjectWorkspaceKey.Compute(_project),
            new BaselinePublication(_publishedRoot, fwDataPath, _token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));

        _proposalJson = DryRunTestSupport.BuildSetGlossProposalJson(_seed.FirstSenseId, "same proposal every time");
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort: a locked native handle should not fail the test */ }
    }

    [Fact]
    public void TwentyDryRunsFromOneCaptureAreIdenticalAndLeaveTheBaselineByteForByteUnchanged()
    {
        var lanes = new ProjectLaneRegistry(_ => _token);
        var factory = new BaselineScratchFactory(_loader);
        var handler = new DryRunJobHandler(_jobs, _baselines, lanes, _ => null,
            (fwDataPath, proposal, _) =>
            {
                using var scratch = factory.OpenSingleUse(fwDataPath);
                return Task.FromResult(ProposalDryRunner.Run(scratch, proposal));
            });

        var directoriesBefore = DirectoriesUnder(_root);
        var manifestBefore = ManifestOf(_publishedRoot);
        string? firstEffectDigest = null;
        string? firstFootprintDigest = null;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var job = _jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(_project),
                "dry-run", _proposalJson, "2026-08-24T00:00:00Z");

            handler.RunAsync(job.JobId, _project, CancellationToken.None).GetAwaiter().GetResult();

            var completed = _jobs.Get(job.JobId)!;
            Assert.Equal(JobStatus.CompletedDryRunOnly, completed.Status);
            Assert.True(completed.DryRunPublished);
            Assert.NotNull(completed.DryRunJson);

            using var published = JsonDocument.Parse(completed.DryRunJson!);
            var effectDigest = published.RootElement.GetProperty("effectDigest").GetString();
            var footprintDigest = published.RootElement.GetProperty("anchor").GetProperty("FootprintDigest").GetString();

            firstEffectDigest ??= effectDigest;
            firstFootprintDigest ??= footprintDigest;
            Assert.Equal(firstEffectDigest, effectDigest);
            Assert.Equal(firstFootprintDigest, footprintDigest);
        }

        Assert.Equal(manifestBefore, ManifestOf(_publishedRoot));
        Assert.Equal(directoriesBefore, DirectoriesUnder(_root));
        Assert.Equal(1, _loader.SaveCount);
    }

    private static string[] DirectoriesUnder(string root) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string ManifestOf(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{Sha256Of(path)}"));

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // Counts real saves so the test proves the twenty Dry Runs below never triggered a second one.
    private sealed class CountingFwDataProjectLoader : FwDataProjectLoader
    {
        public int SaveCount { get; private set; }

        public override void Save(LcmCache cache)
        {
            SaveCount++;
            base.Save(cache);
        }
    }
}

/// <summary>
/// Deterministic, no-real-cache tests for scheduling, freshness reporting, and cancellation: everything
/// <see cref="DryRunJobHandler"/> owns that does not require a live <c>LcmCache</c> to prove.
/// </summary>
public sealed class BaselineDryRunSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-dryrun-job-" + Guid.NewGuid().ToString("N"));

    public BaselineDryRunSchedulingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ConcurrentDryRunJobsForTheSameProjectOpenOnlyOneScratchAtATime()
    {
        using var context = Context("serial");
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var firstStarted = Signal();
        var releaseFirst = Signal();
        var secondStarted = false;
        var callCount = 0;

        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes, _ => null,
            async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondStarted = true;
                }
                return DryRunTestSupport.FakeDryRun();
            });

        var firstJob = context.CreateJob("first");
        var secondJob = context.CreateJob("second");

        var firstRun = handler.RunAsync(firstJob.JobId, context.Project, CancellationToken.None);
        await firstStarted.Task;

        var secondRun = handler.RunAsync(secondJob.JobId, context.Project, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(secondStarted);

        releaseFirst.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        Assert.True(secondStarted);
        Assert.Equal(JobStatus.CompletedDryRunOnly, context.Jobs.Get(firstJob.JobId)!.Status);
        Assert.Equal(JobStatus.CompletedDryRunOnly, context.Jobs.Get(secondJob.JobId)!.Status);
    }

    [Fact]
    public async Task CurrentWhenTheLiveObservationMatchesTheTokensSessionAndGeneration()
    {
        using var context = Context("current");
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(new LiveProjectObservation("session-a", 5, false, context.Token.SemanticSnapshotDigest));
        var completed = await RunOnce(context, tracker);
        AssertPublishedAndReported(completed, context.Token, "current");
    }

    [Fact]
    public async Task KnownOldAfterAHigherLiveEditGenerationInTheSameHostSession()
    {
        using var context = Context("known-old-generation");
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(new LiveProjectObservation("session-a", 6, false, context.Token.SemanticSnapshotDigest));
        var completed = await RunOnce(context, tracker);
        AssertPublishedAndReported(completed, context.Token, "known-old");
    }

    [Fact]
    public async Task KnownOldWhenANewHostEpochReportsADifferentSavedSemanticDigest()
    {
        using var context = Context("known-old-epoch");
        var tracker = new ProjectFreshnessTracker();
        tracker.Register(new LiveProjectObservation("session-b", 1, false, "sha256:" + new string('f', 64)));
        var completed = await RunOnce(context, tracker);
        AssertPublishedAndReported(completed, context.Token, "known-old");
    }

    [Fact]
    public async Task CurrentnessNotCheckedWhenNoLiveObservationIsRegistered()
    {
        using var context = Context("not-checked");
        var completed = await RunOnce(context, tracker: null);
        AssertPublishedAndReported(completed, context.Token, "currentness-not-checked");
    }

    [Fact]
    public async Task CancellationAfterTheRunButBeforePublicationLeavesNoPartialDryRunRecord()
    {
        using var context = Context("cancel");
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        using var cts = new CancellationTokenSource();
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes, _ => null,
            (_, _, _) =>
            {
                var result = DryRunTestSupport.FakeDryRun();
                cts.Cancel();
                return Task.FromResult(result);
            });
        var job = context.CreateJob("cancel");

        await handler.RunAsync(job.JobId, context.Project, cts.Token);

        var completed = context.Jobs.Get(job.JobId)!;
        Assert.Equal(JobStatus.Cancelled, completed.Status);
        Assert.False(completed.DryRunPublished);
        Assert.Null(completed.DryRunJson);
    }

    [Fact]
    public async Task DryRunParkedBehindAClosedBarrierReportsWaitingForBaselineAndDoesNotRun()
    {
        using var context = Context("closed-barrier");
        var lanes = new ProjectLaneRegistry(_ => context.Token);
        var lane = lanes.GetOrCreate(context.WorkspaceKey);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes, _ => null,
            (_, _, _) => throw new Xunit.Sdk.XunitException("A parked Dry Run must never run."));

        // Close the barrier exactly as ProjectLaneTests does: a refresh whose capture fails.
        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));

        var job = context.CreateJob("parked");
        var runTask = handler.RunAsync(job.JobId, context.Project, CancellationToken.None);

        Assert.Equal(JobStatus.WaitingForBaseline, context.Jobs.Get(job.JobId)!.Status);
        Assert.False(runTask.IsCompleted);

        // Drain deterministically instead of leaving the parked task to a bare using-dispose race.
        lanes.Dispose();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DryRunReleasedByASuccessfulRefreshProceedsToCompletedDryRunOnly()
    {
        using var context = Context("released-barrier");
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var lane = lanes.GetOrCreate(context.WorkspaceKey);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes, _ => null,
            (_, _, _) => Task.FromResult(DryRunTestSupport.FakeDryRun()));

        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));

        var job = context.CreateJob("parked");
        var runTask = handler.RunAsync(job.JobId, context.Project, CancellationToken.None);
        Assert.Equal(JobStatus.WaitingForBaseline, context.Jobs.Get(job.JobId)!.Status);

        // Only a successful replacement opens the barrier; this releases the parked Dry Run above.
        await lane.EnqueueAsync(ProjectWorkItem.Refresh(_ => Task.FromResult(context.Token)),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        var completed = context.Jobs.Get(job.JobId)!;
        Assert.Equal(JobStatus.CompletedDryRunOnly, completed.Status);
        Assert.True(completed.DryRunPublished);
        Assert.NotNull(completed.DryRunJson);
    }

    [Fact]
    public async Task RefusesAJobThatIsNotQueuedForThisProjectsDryRunKind()
    {
        using var context = Context("wrong-identity");
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes, _ => null,
            (_, _, _) => throw new Xunit.Sdk.XunitException("The scratch must never open."));
        var wrongKind = context.Jobs.Create(Guid.NewGuid().ToString("N"), context.WorkspaceKey,
            "baseline-refresh", "{}", "2026-08-24T00:00:00Z");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.RunAsync(wrongKind.JobId, context.Project, CancellationToken.None));
        Assert.Equal(JobStatus.Queued, context.Jobs.Get(wrongKind.JobId)!.Status);
    }

    [Fact]
    public async Task RefusesWhenNoBaselineHasBeenPublishedForTheProject()
    {
        var project = new ProjectLocator(Path.Combine(_root, "no-baseline.fwdata"), "no-baseline");
        var database = MotifDatabase.OpenOwned(Path.Combine(_root, "no-baseline.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        using var _ = database;
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        using var lanes = new ProjectLaneRegistry(_ => throw new Xunit.Sdk.XunitException("No lane should be created."));
        var handler = new DryRunJobHandler(jobs, baselines, lanes, _ => null,
            (_, _, _) => throw new Xunit.Sdk.XunitException("The scratch must never open."));
        var job = jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(project),
            "dry-run", DryRunTestSupport.BuildSetGlossProposalJson(Guid.NewGuid(), "text"), "2026-08-24T00:00:00Z");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.RunAsync(job.JobId, project, CancellationToken.None));
    }

    private static async Task<JobRecord> RunOnce(TestContext context, ProjectFreshnessTracker? tracker)
    {
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, lanes,
            key => key == context.WorkspaceKey ? tracker : null,
            (_, _, _) => Task.FromResult(DryRunTestSupport.FakeDryRun()));
        var job = context.CreateJob("run");

        await handler.RunAsync(job.JobId, context.Project, CancellationToken.None);

        return context.Jobs.Get(job.JobId)!;
    }

    private static void AssertPublishedAndReported(JobRecord completed, BaselineToken token, string expectedFreshness)
    {
        Assert.Equal(JobStatus.CompletedDryRunOnly, completed.Status);
        Assert.True(completed.DryRunPublished);
        Assert.NotNull(completed.DryRunJson);
        Assert.NotNull(completed.ResultJson);
        Assert.Contains($"\"freshness\":\"{expectedFreshness}\"", completed.ResultJson!, StringComparison.Ordinal);
        Assert.Contains($"\"capturedUtc\":\"{token.CapturedUtc}\"", completed.ResultJson!, StringComparison.Ordinal);
        Assert.Contains(token.BundleDigest, completed.ResultJson!, StringComparison.Ordinal);
    }

    private TestContext Context(string name)
    {
        var project = new ProjectLocator(Path.Combine(_root, name + ".fwdata"), name);
        var database = MotifDatabase.OpenOwned(Path.Combine(_root, name + ".motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        var token = new BaselineToken(name, "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64), "session-a", 5);
        var publishedRoot = Path.Combine(_root, name + "-published");
        Directory.CreateDirectory(publishedRoot);
        baselines.Record(workspaceKey,
            new BaselinePublication(publishedRoot, Path.Combine(publishedRoot, "project.fwdata"), token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        return new TestContext(project, database, jobs, baselines, token, workspaceKey);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record TestContext(ProjectLocator Project, MotifDatabase Database, JobRepository Jobs,
        BaselineRepository Baselines, BaselineToken Token, string WorkspaceKey) : IDisposable
    {
        public JobRecord CreateJob(string label) => Jobs.Create(Guid.NewGuid().ToString("N"), WorkspaceKey,
            "dry-run", DryRunTestSupport.BuildSetGlossProposalJson(Guid.NewGuid(), label), "2026-08-24T00:00:00Z");

        public void Dispose() => Database.Dispose();
    }
}

internal static class DryRunTestSupport
{
    public static string BuildSetGlossProposalJson(Guid targetId, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text });
        var proposalId = CanonicalId.Mint().Value;
        var operationId = CanonicalId.Mint().Value;
        var target = CanonicalId.FromGuid(targetId).Value;
        return $$"""
            {
              "contractVersions": {"lexical": "1.0"},
              "proposalId": "{{proposalId}}",
              "operations": [
                {
                  "operationId": "{{operationId}}",
                  "kind": "lexical/lexSense/setGloss",
                  "target": "{{target}}",
                  "after": {{afterJson}}
                }
              ]
            }
            """;
    }

    public static DryRunModel FakeDryRun()
    {
        var effectDigest = "sha256:" + new string('c', 64);
        var intentDigest = "sha256:" + new string('d', 64);
        var footprintDigest = "sha256:" + new string('e', 64);
        return new DryRunModel(intentDigest, "fake baseline note", Array.Empty<ExpectedEffect>(), effectDigest,
            new SIL.Motif.Model.DryRun.BoundDryRunAnchor(intentDigest, footprintDigest, effectDigest,
                "1.0.0.0", "1.0.0.0", "1", "20260824T000000Z"));
    }
}
