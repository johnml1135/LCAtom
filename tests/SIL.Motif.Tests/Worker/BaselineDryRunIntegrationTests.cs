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
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
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
        var proposals = new ProposalRepository(_database);
        var lanes = new ProjectLaneRegistry(_ => _token);
        var factory = new BaselineScratchFactory(_loader);
        var handler = new DryRunJobHandler(_jobs, _baselines, proposals, lanes, _ => null,
            (fwDataPath, _) =>
            {
                var scratch = factory.OpenSingleUse(fwDataPath);
                var appliedProposalIds = ProjectAppliedLog.ReadAll(scratch.PeekCache())
                    .Select(entry => entry.ProposalId).ToArray();
                return Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((appliedProposalIds, scratch));
            },
            (scratch, plan, _) => Task.FromResult(ProposalDryRunner.Run(scratch!, plan)));

        var directoriesBefore = DirectoriesUnder(_root);
        var manifestBefore = ManifestOf(_publishedRoot);
        string? firstEffectDigest = null;
        string? firstFootprintDigest = null;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var job = _jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(_project),
                "dry-run", _proposalJson, "2026-08-24T00:00:00Z");
            DryRunJobTestHarness.Claim(_jobs, job.JobId);

            var completed = DryRunJobTestHarness.RunAndFinishAsync(_jobs, handler, job.JobId, _project)
                .GetAwaiter().GetResult();
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
        // One scratch open per job, not two: proves the fix across twenty runs, not just one.
        Assert.Equal(20, _loader.LoadScratchCacheCount);
    }

    [Fact]
    public void ADryRunJobOpensThePublishedBaselineExactlyOnce()
    {
        var proposals = new ProposalRepository(_database);
        var lanes = new ProjectLaneRegistry(_ => _token);
        var factory = new BaselineScratchFactory(_loader);
        var handler = new DryRunJobHandler(_jobs, _baselines, proposals, lanes, _ => null,
            (fwDataPath, _) =>
            {
                var scratch = factory.OpenSingleUse(fwDataPath);
                var appliedProposalIds = ProjectAppliedLog.ReadAll(scratch.PeekCache())
                    .Select(entry => entry.ProposalId).ToArray();
                return Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((appliedProposalIds, scratch));
            },
            (scratch, plan, _) => Task.FromResult(ProposalDryRunner.Run(scratch!, plan)));

        var job = _jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(_project),
            "dry-run", _proposalJson, "2026-08-24T00:00:00Z");
        DryRunJobTestHarness.Claim(_jobs, job.JobId);

        var completed = DryRunJobTestHarness.RunAndFinishAsync(_jobs, handler, job.JobId, _project)
            .GetAwaiter().GetResult();

        Assert.Equal(JobStatus.CompletedDryRunOnly, completed.Status);
        Assert.Equal(1, _loader.LoadScratchCacheCount);
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

    // Counts real saves and scratch opens so a test can prove how many of each a run actually did.
    private sealed class CountingFwDataProjectLoader : FwDataProjectLoader
    {
        public int SaveCount { get; private set; }
        public int LoadScratchCacheCount { get; private set; }

        public override void Save(LcmCache cache)
        {
            SaveCount++;
            base.Save(cache);
        }

        public override LcmCache LoadScratchCache(string fwDataFilePath, string? templatesFolder = null)
        {
            LoadScratchCacheCount++;
            return base.LoadScratchCache(fwDataFilePath, templatesFolder);
        }
    }
}

/// <summary>
/// Stands in for <see cref="JobRunnerLoop"/> around a directly-constructed <see cref="DryRunJobHandler"/>:
/// claims a job the way the loop's own claim does, then applies any <see cref="JobOutcome"/> the handler
/// returns the way the loop's own <c>Finish</c> does. Every test in this file drives the handler directly
/// rather than through a real loop, so this is what keeps their claim/finish halves honest.
/// </summary>
internal static class DryRunJobTestHarness
{
    public static void Claim(JobRepository jobs, string jobId) =>
        jobs.Transition(jobs.Get(jobId)!, JobStatus.Running);

    public static async Task<JobRecord> RunAndFinishAsync(JobRepository jobs, DryRunJobHandler handler,
        string jobId, ProjectLocator project, CancellationToken cancellationToken = default)
    {
        try
        {
            var outcome = await handler.RunAsync(jobId, project, cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
            {
                var current = jobs.Get(jobId)!;
                jobs.Transition(jobId, outcome.Status, current.Version, outcome.Category, outcome.ResultJson);
            }
        }
        catch (OperationCanceledException)
        {
            var current = jobs.Get(jobId)!;
            if (!JobStateMachine.IsTerminal(current.Status))
                jobs.Transition(jobId, JobStatus.Cancelled, current.Version, JobFailureCategory.Cancellation);
        }
        catch (Exception exception)
        {
            var current = jobs.Get(jobId)!;
            if (!JobStateMachine.IsTerminal(current.Status))
            {
                // A plain message is not structured JSON; wrap it the way JobRunnerLoop's own detail does.
                jobs.Transition(jobId, JobStatus.Failed, current.Version, JobFailureCategory.Unknown,
                    JsonSerializer.Serialize(new { detail = exception.Message }));
            }
        }
        return jobs.Get(jobId)!;
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

        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes, _ => null,
            (_, _) => Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((Array.Empty<Guid>(), null)),
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
        DryRunJobTestHarness.Claim(context.Jobs, firstJob.JobId);
        DryRunJobTestHarness.Claim(context.Jobs, secondJob.JobId);

        var firstRun = DryRunJobTestHarness.RunAndFinishAsync(context.Jobs, handler, firstJob.JobId, context.Project);
        await firstStarted.Task;

        var secondRun = DryRunJobTestHarness.RunAndFinishAsync(context.Jobs, handler, secondJob.JobId, context.Project);
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
        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes, _ => null,
            (_, _) => Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((Array.Empty<Guid>(), null)),
            (_, _, _) =>
            {
                var result = DryRunTestSupport.FakeDryRun();
                cts.Cancel();
                return Task.FromResult(result);
            });
        var job = context.CreateJob("cancel");
        DryRunJobTestHarness.Claim(context.Jobs, job.JobId);

        var completed = await DryRunJobTestHarness.RunAndFinishAsync(
            context.Jobs, handler, job.JobId, context.Project, cts.Token);

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
        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes, _ => null,
            (_, _) => throw new Xunit.Sdk.XunitException("A parked Dry Run must never read applied ids."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("A parked Dry Run must never run."));

        // Close the barrier exactly as ProjectLaneTests does: a refresh whose capture fails.
        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));

        var job = context.CreateJob("parked");
        DryRunJobTestHarness.Claim(context.Jobs, job.JobId);
        var runTask = DryRunJobTestHarness.RunAndFinishAsync(context.Jobs, handler, job.JobId, context.Project);

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
        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes, _ => null,
            (_, _) => Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((Array.Empty<Guid>(), null)),
            (_, _, _) => Task.FromResult(DryRunTestSupport.FakeDryRun()));

        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));

        var job = context.CreateJob("parked");
        DryRunJobTestHarness.Claim(context.Jobs, job.JobId);
        var runTask = DryRunJobTestHarness.RunAndFinishAsync(context.Jobs, handler, job.JobId, context.Project);
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
    public async Task RefusesAJobThatIsNotClaimedOrIsTheWrongKindForThisProjectsDryRun()
    {
        using var context = Context("wrong-identity");
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes, _ => null,
            (_, _) => throw new Xunit.Sdk.XunitException("Applied ids must never be read."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("The scratch must never open."));
        var wrongKind = context.Jobs.Create(Guid.NewGuid().ToString("N"), context.WorkspaceKey,
            "baseline-refresh", "{}", "2026-08-24T00:00:00Z");

        // Unclaimed: the loop's own claim is what moves a job to Running before it ever reaches a handler.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.RunAsync(wrongKind.JobId, context.Project, CancellationToken.None));
        Assert.Equal(JobStatus.Queued, context.Jobs.Get(wrongKind.JobId)!.Status);

        // Claimed, but the wrong kind: refused for the reason the name says, not a status mismatch.
        DryRunJobTestHarness.Claim(context.Jobs, wrongKind.JobId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.RunAsync(wrongKind.JobId, context.Project, CancellationToken.None));
        Assert.Equal(JobStatus.Running, context.Jobs.Get(wrongKind.JobId)!.Status);
    }

    [Fact]
    public async Task ParksAtWaitingForBaselineRatherThanFailingWhenNoBaselineHasBeenPublishedForTheProject()
    {
        var project = new ProjectLocator(Path.Combine(_root, "no-baseline.fwdata"), "no-baseline");
        var database = MotifDatabase.OpenOwned(Path.Combine(_root, "no-baseline.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        using var _ = database;
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        using var lanes = new ProjectLaneRegistry(_ => throw new Xunit.Sdk.XunitException("No lane should be created."));
        var proposals = new ProposalRepository(database);
        var handler = new DryRunJobHandler(jobs, baselines, proposals, lanes, _ => null,
            (_, _) => throw new Xunit.Sdk.XunitException("Applied ids must never be read."),
            (_, _, _) => throw new Xunit.Sdk.XunitException("The scratch must never open."));
        var job = jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(project),
            "dry-run", DryRunTestSupport.BuildSetGlossProposalJson(Guid.NewGuid(), "text"), "2026-08-24T00:00:00Z");
        DryRunJobTestHarness.Claim(jobs, job.JobId);

        var outcome = await handler.RunAsync(job.JobId, project, CancellationToken.None);

        // Nothing here ever requeues a parked job, so this is where it actually stays: not failed.
        Assert.Null(outcome);
        Assert.Equal(JobStatus.WaitingForBaseline, jobs.Get(job.JobId)!.Status);
    }

    private static async Task<JobRecord> RunOnce(TestContext context, ProjectFreshnessTracker? tracker)
    {
        using var lanes = new ProjectLaneRegistry(_ => context.Token);
        var proposals = new ProposalRepository(context.Database);
        var handler = new DryRunJobHandler(context.Jobs, context.Baselines, proposals, lanes,
            key => key == context.WorkspaceKey ? tracker : null,
            (_, _) => Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((Array.Empty<Guid>(), null)),
            (_, _, _) => Task.FromResult(DryRunTestSupport.FakeDryRun()));
        var job = context.CreateJob("run");
        DryRunJobTestHarness.Claim(context.Jobs, job.JobId);

        return await DryRunJobTestHarness.RunAndFinishAsync(context.Jobs, handler, job.JobId, context.Project);
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
