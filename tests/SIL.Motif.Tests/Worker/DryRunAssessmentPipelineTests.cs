using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SIL.LCModel;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.LiveHost.Baselines;
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
/// Proves the plan's Task 3 promise: one durable job carries a Dry Run, and — only when asked — the
/// Assessment that follows it, with the ordering and cancellation guarantees the plan fixes: publish
/// before export, disposal immediately after export, and never a partial Assessment record.
/// </summary>
public sealed class DryRunAssessmentPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-dryrun-assess-" + Guid.NewGuid().ToString("N"));

    public DryRunAssessmentPipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task NoAssessment_StopsAfterTheDryRunAndPinsCompletedDryRunOnly()
    {
        using var context = Context("no-assessment");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => Task.FromResult(FakeDryRun("digest-1")),
            openCandidateForExport: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Export must not open a candidate."),
            runAssessment: (_, _) => throw new Xunit.Sdk.XunitException("Assessment must not run."));

        var final = await pipeline.ExecuteAsync(context.Request("digest-1", includeAssessment: false), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.CompletedDryRunOnly, final.Status);
        Assert.True(final.DryRunPublished);
        Assert.NotNull(final.DryRunJson);
        Assert.Contains("\"assessmentDisposition\":\"skipped\"", final.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunIsPublishedDurablyBeforeExportBegins()
    {
        using var context = Context("publish-before-export");
        var observedPublishedDuringExport = false;
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"), export: (_, _) =>
                {
                    observedPublishedDuringExport = context.Jobs.Get(context.LastJobId!)!.DryRunPublished;
                    return Task.CompletedTask;
                })),
            runAssessment: (_, _) => Task.FromResult(FakeSummary()));

        await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.True(observedPublishedDuringExport, "the Dry Run must already be published while export runs.");
    }

    [Fact]
    public async Task ScratchDisposalHappensImmediatelyAfterExport_NotAfterTheAssessment()
    {
        using var context = Context("dispose-order");
        var order = new List<string>();
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"),
                    export: (_, _) => { order.Add("exported"); return Task.CompletedTask; },
                    dispose: () => order.Add("disposed"))),
            runAssessment: async (_, _) =>
            {
                order.Add("assessment-started");
                await Task.Delay(10);
                return FakeSummary();
            });

        await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.Equal(new[] { "exported", "disposed", "assessment-started" }, order);
    }

    [Fact]
    public async Task TheNextDryRunCanBeginWhileAPriorPanGlossAssessmentIsStillRunning()
    {
        using var context = Context("non-blocking");
        var assessmentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAssessmentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCandidateOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, intentDigest, _) =>
            {
                if (intentDigest == "digest-2") secondCandidateOpened.TrySetResult();
                return Task.FromResult<IDryRunCandidateForExport>(new FakeCandidate(FakeDryRun(intentDigest)));
            },
            // One shared gate: releasing it frees whichever job reached the Assessment stage.
            runAssessment: async (_, _) =>
            {
                firstAssessmentEntered.TrySetResult();
                await assessmentGate.Task;
                return FakeSummary();
            });

        var first = pipeline.ExecuteAsync(context.Request("digest-1", includeAssessment: true), CancellationToken.None);
        await firstAssessmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(secondCandidateOpened.Task.IsCompleted, "the second Dry Run must not have started yet.");

        var second = pipeline.ExecuteAsync(context.Request("digest-2", includeAssessment: true), CancellationToken.None);
        await secondCandidateOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(first.IsCompleted, "the first job's Assessment must still be in flight.");

        assessmentGate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ALateAssessmentResultBindsTheExactIntentDigestAndBaselineItWasComputedFor()
    {
        using var context = Context("late-binding");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, intentDigest, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun(intentDigest))),
            runAssessment: async (_, _) => { await Task.Delay(30); return FakeSummary(); });

        var final = await pipeline.ExecuteAsync(context.Request("digest-late", includeAssessment: true), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Completed, final.Status);
        Assert.Contains("\"intentDigest\":\"digest-late\"", final.ResultJson, StringComparison.Ordinal);
        Assert.Contains(context.Token.BundleDigest, final.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinguisticallyPoorResultsCompleteNormally()
    {
        using var context = Context("poor-result");
        var poorSummary = new AssessmentSummary("foma-confirm", WordCount: 40, AnalysisCount: 0,
            DiagnosticCount: 137, OutcomeDigest: "sha256:" + new string('9', 64),
            SemanticDigest: "sha256:" + new string('8', 64), GrammarSourceSha256: "sha256:" + new string('7', 64),
            ModelFingerprint: "fp-poor", BoundedLog: "many unparsed forms");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"))),
            runAssessment: (_, _) => Task.FromResult(poorSummary));

        var final = await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.Equal(JobStatus.Completed, final.Status);
        Assert.Contains("\"analysisCount\":0", final.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolFailureYieldsCompletedWithAssessmentFailure_WithTheDryRunRetained()
    {
        using var context = Context("tool-failure");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"))),
            runAssessment: (_, _) => throw new IOException("pangloss assess exited 1 for 'C:\\secret\\engine\\pangloss.exe'"));

        var final = await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.Equal(JobStatus.CompletedWithAssessmentFailure, final.Status);
        Assert.True(final.DryRunPublished);
        Assert.NotNull(final.DryRunJson);
        Assert.Contains("\"assessmentDisposition\":\"tool-failure\"", final.ResultJson, StringComparison.Ordinal);
        Assert.Contains("\"failure\":\"IOException\"", final.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", final.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pangloss.exe", final.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportFailureAfterPublicationAlsoYieldsCompletedWithAssessmentFailure()
    {
        using var context = Context("export-failure");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"), export: (_, _) => throw new IOException("disk full"))),
            runAssessment: (_, _) => throw new Xunit.Sdk.XunitException("Assessment must not run after export failed."));

        var final = await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.Equal(JobStatus.CompletedWithAssessmentFailure, final.Status);
        Assert.True(final.DryRunPublished);
    }

    [Fact]
    public async Task AmendmentNeverRelabelsHistory()
    {
        using var context = Context("amendment");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, intentDigest, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun(intentDigest))),
            runAssessment: (_, _) => Task.FromResult(FakeSummary()));

        var original = await pipeline.ExecuteAsync(context.Request("digest-original", includeAssessment: true),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var amended = await pipeline.ExecuteAsync(context.Request("digest-amended", includeAssessment: true),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var rereadOriginal = context.Jobs.Get(original.JobId)!;
        Assert.Equal(original.Status, rereadOriginal.Status);
        Assert.Equal(original.ResultJson, rereadOriginal.ResultJson);
        Assert.Equal(original.Version, rereadOriginal.Version);
        Assert.Contains("digest-original", rereadOriginal.ResultJson, StringComparison.Ordinal);
        Assert.NotEqual(original.JobId, amended.JobId);
        Assert.Contains("digest-amended", amended.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringExport_RetainsThePublishedDryRun_CancelsTheJob_AndDeletesTheCandidateWorkspace()
    {
        using var context = Context("cancel-export");
        using var cts = new CancellationTokenSource();
        string? exportDirectory = null;
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"), export: (destination, _) =>
                {
                    exportDirectory = destination;
                    File.WriteAllText(Path.Combine(destination, "partial.txt"), "partial export");
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                })),
            runAssessment: (_, _) => throw new Xunit.Sdk.XunitException("Assessment must not run after cancellation."));

        var final = await pipeline.ExecuteAsync(context.Request("digest-1", includeAssessment: true), cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Cancelled, final.Status);
        Assert.True(final.DryRunPublished);
        Assert.NotNull(final.DryRunJson);
        Assert.Equal("{\"assessmentDisposition\":\"cancelled\"}", final.ResultJson);
        Assert.NotNull(exportDirectory);
        Assert.False(Directory.Exists(exportDirectory), "the candidate workspace must be deleted after cancellation.");
    }

    [Fact]
    public async Task CancellationDuringAssessment_RetainsThePublishedDryRun_CancelsTheJob_AndDeletesTheCandidateWorkspace()
    {
        using var context = Context("cancel-assessment");
        using var cts = new CancellationTokenSource();
        string? exportDirectory = null;
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"), export: (destination, _) =>
                {
                    exportDirectory = destination;
                    File.WriteAllText(Path.Combine(destination, "candidate.fwdata"), "exported candidate");
                    return Task.CompletedTask;
                })),
            runAssessment: (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var final = await pipeline.ExecuteAsync(context.Request("digest-1", includeAssessment: true), cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Cancelled, final.Status);
        Assert.True(final.DryRunPublished);
        Assert.Equal("{\"assessmentDisposition\":\"cancelled\"}", final.ResultJson);
        Assert.NotNull(exportDirectory);
        Assert.False(Directory.Exists(exportDirectory), "the candidate workspace must be deleted after cancellation.");
    }

    [Fact]
    public async Task AssessmentPayloadAndItsBoundedLogBecomeDurableOnlyAfterACompleteParse()
    {
        using var context = Context("no-partial-record");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"))),
            runAssessment: async (_, _) =>
            {
                entered.TrySetResult();
                await gate.Task;
                return FakeSummary();
            });

        var runTask = pipeline.ExecuteAsync(context.Request("digest-1", includeAssessment: true), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var midFlight = context.Jobs.Get(context.LastJobId!)!;
        Assert.Equal(JobStatus.Running, midFlight.Status);
        Assert.Null(midFlight.ResultJson);

        gate.SetResult();
        var final = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Completed, final.Status);
        Assert.NotNull(final.ResultJson);
        Assert.Contains("\"assessmentDisposition\":\"completed\"", final.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolFailureIsNeverAutomaticallyRetried()
    {
        using var context = Context("no-auto-retry");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run."),
            openCandidateForExport: (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(
                new FakeCandidate(FakeDryRun("digest-1"))),
            runAssessment: (_, _) => throw new InvalidOperationException("pangloss refused this grammar"));

        var final = await RunAndCaptureJobId(context, pipeline, "digest-1");

        Assert.Equal(JobStatus.CompletedWithAssessmentFailure, final.Status);
        Assert.Empty(context.Jobs.ListActive(context.WorkspaceKey));
        Assert.Single(context.Jobs.ListAttempts(final.LogicalJobId));
    }

    [Fact]
    public async Task RefusesWhenTheRequestedBaselineIsNoLongerThePublishedBaseline()
    {
        using var context = Context("stale-baseline");
        var pipeline = context.BuildPipeline(
            runInPlace: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not run."),
            openCandidateForExport: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not run."),
            runAssessment: (_, _) => throw new Xunit.Sdk.XunitException("Must not run."));
        var staleToken = new BaselineToken(context.Token.ProjectIdentity, "sha256:" + new string('9', 64),
            "1", "2026-08-24T00:00:00Z", "sha256:" + new string('9', 64));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(
            new DryRunAssessmentRequest(CanonicalId.Mint(), "digest-1", staleToken, IncludeAssessment: false),
            CancellationToken.None));
    }

    [Fact]
    public async Task RefusesWhenNoBaselineHasBeenPublishedForTheProject()
    {
        var project = new ProjectLocator(Path.Combine(_root, "no-baseline.fwdata"), "no-baseline");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "no-baseline.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        using var lanes = new ProjectLaneRegistry(_ => throw new Xunit.Sdk.XunitException("No lane should be created."));
        var pipeline = new DryRunAssessmentPipeline(jobs, baselines, lanes, project,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not run."),
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not run."),
            (_, _) => throw new Xunit.Sdk.XunitException("Must not run."),
            () => throw new Xunit.Sdk.XunitException("Must not allocate."),
            _ => throw new Xunit.Sdk.XunitException("Must not delete."));

        var token = new BaselineToken("no-baseline", "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(
            new DryRunAssessmentRequest(CanonicalId.Mint(), "digest-1", token, IncludeAssessment: true),
            CancellationToken.None));
    }

    private static async Task<JobRecord> RunAndCaptureJobId(TestContext context, DryRunAssessmentPipeline pipeline,
        string intentDigest, bool includeAssessment = true)
    {
        var request = context.Request(intentDigest, includeAssessment);
        var task = pipeline.ExecuteAsync(request, CancellationToken.None);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static DryRunModel FakeDryRun(string intentDigest)
    {
        var effectDigest = "sha256:" + new string('c', 64);
        var footprintDigest = "sha256:" + new string('e', 64);
        var fullDigest = intentDigest.Length >= 64 ? intentDigest : intentDigest.PadRight(64, '0');
        return new DryRunModel(fullDigest, "fake baseline note", Array.Empty<SIL.Motif.Model.Effects.ExpectedEffect>(),
            effectDigest, new SIL.Motif.Model.DryRun.BoundDryRunAnchor(fullDigest, footprintDigest, effectDigest,
                "1.0.0.0", "1.0.0.0", "1", "20260824T000000Z"));
    }

    private static AssessmentSummary FakeSummary() => new("foma-confirm", WordCount: 3, AnalysisCount: 3,
        DiagnosticCount: 0, OutcomeDigest: "sha256:" + new string('1', 64), SemanticDigest: "sha256:" + new string('2', 64),
        GrammarSourceSha256: "sha256:" + new string('3', 64), ModelFingerprint: "fp-1", BoundedLog: "ok");

    private TestContext Context(string name)
    {
        var project = new ProjectLocator(Path.Combine(_root, name + ".fwdata"), name);
        var database = MotifDatabase.OpenOwned(Path.Combine(_root, name + ".motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        var token = new BaselineToken(name, "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64));
        var publishedRoot = Path.Combine(_root, name + "-published");
        Directory.CreateDirectory(publishedRoot);
        baselines.Record(workspaceKey,
            new BaselinePublication(publishedRoot, Path.Combine(publishedRoot, "project.fwdata"), token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        return new TestContext(project, database, jobs, baselines, token, workspaceKey, _root);
    }

    private sealed class TestContext(ProjectLocator project, MotifDatabase database, JobRepository jobs,
        BaselineRepository baselines, BaselineToken token, string workspaceKey, string root) : IDisposable
    {
        private readonly List<ProjectLaneRegistry> _lanes = [];

        public JobRepository Jobs { get; } = jobs;
        public BaselineToken Token { get; } = token;
        public string WorkspaceKey { get; } = workspaceKey;
        public string? LastJobId { get; private set; }

        public DryRunAssessmentRequest Request(string intentDigest, bool includeAssessment) =>
            new(CanonicalId.Mint(), intentDigest, Token, includeAssessment);

        public DryRunAssessmentPipeline BuildPipeline(
            Func<string, CanonicalId, string, CancellationToken, Task<DryRunModel>> runInPlace,
            Func<string, CanonicalId, string, CancellationToken, Task<IDryRunCandidateForExport>> openCandidateForExport,
            Func<string, CancellationToken, Task<AssessmentSummary>> runAssessment)
        {
            var lanes = new ProjectLaneRegistry(_ => Token);
            _lanes.Add(lanes);
            var exportRoot = Path.Combine(root, "exports-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exportRoot);
            return new DryRunAssessmentPipeline(Jobs, baselines, lanes, project,
                runInPlace: async (fwDataPath, proposalId, intentDigest, ct) =>
                {
                    CaptureJobId();
                    return await runInPlace(fwDataPath, proposalId, intentDigest, ct);
                },
                openCandidateForExport: async (fwDataPath, proposalId, intentDigest, ct) =>
                {
                    CaptureJobId();
                    return await openCandidateForExport(fwDataPath, proposalId, intentDigest, ct);
                },
                runAssessment: runAssessment,
                allocateExportDirectory: () =>
                {
                    var destination = Path.Combine(exportRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(destination);
                    return destination;
                },
                deleteExportDirectory: destination =>
                {
                    try { Directory.Delete(destination, recursive: true); } catch (IOException) { }
                });
        }

        // Job ids are minted inside ExecuteAsync, so capture the newest when a lane callback first runs.
        private void CaptureJobId() => LastJobId = Jobs.ListActive(WorkspaceKey)
            .OrderByDescending(record => record.CreatedUtc, StringComparer.Ordinal)
            .Select(record => record.JobId)
            .FirstOrDefault() ?? LastJobId;

        public void Dispose()
        {
            foreach (var lane in _lanes) lane.Dispose();
            database.Dispose();
        }
    }

    private sealed class FakeCandidate : IDryRunCandidateForExport
    {
        private readonly Func<string, CancellationToken, Task>? _export;
        private readonly Action? _dispose;

        public FakeCandidate(DryRunModel dryRun, Func<string, CancellationToken, Task>? export = null, Action? dispose = null)
        {
            DryRun = dryRun;
            _export = export;
            _dispose = dispose;
        }

        public DryRunModel DryRun { get; }

        public Task ExportAsync(string emptyDestination, CancellationToken cancellationToken) =>
            _export?.Invoke(emptyDestination, cancellationToken) ?? Task.CompletedTask;

        public void Dispose() => _dispose?.Invoke();
    }
}

/// <summary>Proves the closed command DTO defaults Assessment on and translates cleanly into a request.</summary>
public sealed class DryRunAssessmentCommandHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-dryrun-assess-cmd-" + Guid.NewGuid().ToString("N"));

    public DryRunAssessmentCommandHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OmittingIncludeAssessmentDefaultsItOn()
    {
        var project = new ProjectLocator(Path.Combine(_root, "cmd.fwdata"), "cmd");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "cmd.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        var token = new BaselineToken("cmd", "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64));
        var publishedRoot = Path.Combine(_root, "published");
        Directory.CreateDirectory(publishedRoot);
        baselines.Record(workspaceKey, new BaselinePublication(publishedRoot, Path.Combine(publishedRoot, "p.fwdata"), token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        using var lanes = new ProjectLaneRegistry(_ => token);
        var assessmentRan = false;
        var effectDigest = "sha256:" + new string('c', 64);
        var intentDigest = "sha256:" + new string('d', 64);
        var dryRun = new DryRunModel(intentDigest, "note", Array.Empty<SIL.Motif.Model.Effects.ExpectedEffect>(),
            effectDigest, new SIL.Motif.Model.DryRun.BoundDryRunAnchor(intentDigest, "sha256:" + new string('e', 64),
                effectDigest, "1.0.0.0", "1.0.0.0", "1", "20260824T000000Z"));
        var exportRoot = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportRoot);

        var pipeline = new DryRunAssessmentPipeline(jobs, baselines, lanes, project,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Must not run in place."),
            (_, _, _, _) => Task.FromResult<IDryRunCandidateForExport>(new NoOpCandidate(dryRun)),
            (_, _) =>
            {
                assessmentRan = true;
                return Task.FromResult(new AssessmentSummary("foma-confirm", 1, 1, 0,
                    "sha256:" + new string('1', 64), "sha256:" + new string('2', 64),
                    "sha256:" + new string('3', 64), "fp", "ok"));
            },
            () => exportRoot,
            _ => { });
        var handler = new DryRunAssessmentCommandHandler(pipeline);

        var final = await handler.HandleAsync(
            new DryRunAssessmentCommand(CanonicalId.Mint().Value, intentDigest, token, IncludeAssessment: null),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(assessmentRan, "omitting IncludeAssessment must default it to true.");
        Assert.Equal(JobStatus.Completed, final.Status);
    }

    private sealed class NoOpCandidate(DryRunModel dryRun) : IDryRunCandidateForExport
    {
        public DryRunModel DryRun { get; } = dryRun;
        public Task ExportAsync(string emptyDestination, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}

/// <summary>
/// Proves the joined pipeline seam carries no engine or cache-identity surface, matching the export
/// seam's own guarantee from Task 1.
/// </summary>
public sealed class DryRunAssessmentSeamSurfaceTests
{
    [Fact]
    public void SeamTypesExposeNoEngineOrCacheKeySurface()
    {
        var seamTypes = new[]
        {
            typeof(DryRunAssessmentPipeline),
            typeof(DryRunAssessmentRequest),
            typeof(DryRunAssessmentCommand),
            typeof(IDryRunCandidateForExport),
            typeof(AssessmentSummary),
        };
        var forbidden = new[] { "engine", "cachekey", "cache_key", "executable", "processpath" };
        var offenders = new List<string>();

        foreach (var type in seamTypes)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(flags))
            {
                if (ContainsForbidden(member.Name, forbidden))
                    offenders.Add($"{type.Name}.{member.Name}");

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                        if (ContainsForbidden(parameter.Name ?? string.Empty, forbidden))
                            offenders.Add($"{type.Name}.{member.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Found engine/cache-key surface on the joined pipeline seam: " + string.Join(", ", offenders));
    }

    private static bool ContainsForbidden(string name, IEnumerable<string> forbidden) =>
        forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One real-<c>LcmCache</c> pass through the whole assessed path: a copy-backed candidate is exported to
/// a fake PanGloss executable's input, the published Baseline never moves, and every ephemeral directory
/// this run created is gone by the time the job reaches its terminal state.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class DryRunAssessmentPipelineRealCandidateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.DryRunAssessmentRealCandidateTests", Guid.NewGuid().ToString("N"));

    public DryRunAssessmentPipelineRealCandidateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort: a locked native handle should not fail the test */ }
    }

    [Fact]
    public async Task RealCandidateIsCopiedExportedAndAssessed_LeavingThePublishedBaselineByteForByteUnchangedAndNoEphemeraBehind()
    {
        var publishedRoot = Path.Combine(_root, "published");
        Directory.CreateDirectory(publishedRoot);
        var masterRoot = Path.Combine(_root, "master");
        Directory.CreateDirectory(masterRoot);
        var seed = (SeededProject)null!;
        var master = NewLangProjFixture.CreateCache(masterRoot);
        try
        {
            seed = SeededProject.Seed(master);
            new FwDataProjectLoader().Save(master);
            using (var bundle = new MemoryStream())
            {
                await new BaselineBundleWriter().WriteAsync(master, bundle, CancellationToken.None);
                using var archive = new ZipArchive(new MemoryStream(bundle.ToArray()), ZipArchiveMode.Read);
                archive.ExtractToDirectory(publishedRoot);
            }
            Directory.CreateDirectory(Path.Combine(publishedRoot, "WritingSystemStore"));
            Directory.CreateDirectory(Path.Combine(publishedRoot, "SharedSettings"));
        }
        finally
        {
            master.Dispose();
        }
        Directory.Delete(masterRoot, recursive: true);

        var publishedFwData = Path.Combine(publishedRoot, NewLangProjFixture.ProjectName + ".fwdata");
        var directoriesBefore = DirectoriesUnder(publishedRoot);
        var manifestBefore = ManifestOf(publishedRoot);

        var project = new ProjectLocator(Path.Combine(_root, "live", NewLangProjFixture.ProjectName + ".fwdata"), "live-id");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);
        var baselines = new BaselineRepository(database);
        var workspaceKey = ProjectWorkspaceKey.Compute(project);
        var token = new BaselineToken("live-id", "sha256:" + new string('a', 64), "1",
            "2026-08-24T00:00:00Z", "sha256:" + new string('b', 64));
        baselines.Record(workspaceKey, new BaselinePublication(publishedRoot, publishedFwData, token),
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));
        using var lanes = new ProjectLaneRegistry(_ => token);

        var scratchRoot = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(scratchRoot);
        var scratchFactory = new ScratchCacheFactory();
        var proposal = ProposalJsonParser.Parse(
            BuildSetGlossProposalJson(seed.FirstSenseId, "assessed candidate gloss"));

        var scriptsDir = Path.Combine(_root, "fake-pangloss");
        Directory.CreateDirectory(scriptsDir);
        var fakeExecutable = WriteFakeSuccessExecutable(scriptsDir, BuildFakeReportJson());
        var exportRoots = new List<string>();

        var pipeline = new DryRunAssessmentPipeline(jobs, baselines, lanes, project,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("The in-place path must not run when assessing."),
            openCandidateForExport: (fwDataPath, _, _, ct) =>
            {
                var cache = scratchFactory.CreateFromFileCopy(fwDataPath, scratchRoot);
                var copiedProjectFolder = Path.GetDirectoryName(cache.ProjectId.Path)!;
                var scratch = DryRunScratch.Adopt(cache, "assessed candidate",
                    onDisposed: () => { try { Directory.Delete(copiedProjectFolder, recursive: true); } catch { } });
                var dryRun = ProposalDryRunner.Run(scratch, proposal);
                IDryRunCandidateForExport candidate = new RealCandidate(dryRun, cache, scratchRoot, scratch);
                return Task.FromResult(candidate);
            },
            runAssessment: async (exportedDirectory, ct) =>
            {
                exportRoots.Add(exportedDirectory);
                var process = new PanGlossAssessmentProcess(fakeExecutable);
                var report = await process.RunAsync(exportedDirectory, ct);
                return new AssessmentSummary(report.Pipeline, report.Words.Count,
                    report.Words.Sum(w => w.Analyses.Count), report.DiagnosticCount, report.OutcomeDigest,
                    report.SemanticDigest, report.GrammarSourceSha256, report.ModelFingerprint, "ok");
            },
            allocateExportDirectory: () =>
            {
                var destination = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(destination);
                return destination;
            },
            deleteExportDirectory: destination =>
            {
                try { Directory.Delete(destination, recursive: true); } catch { }
            });

        var request = new DryRunAssessmentRequest(proposal.ProposalId, "sha256:" + new string('9', 64), token, true);
        var final = await pipeline.ExecuteAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Completed, final.Status);
        Assert.Contains("\"pipeline\":\"foma-confirm\"", final.ResultJson, StringComparison.Ordinal);

        Assert.Equal(manifestBefore, ManifestOf(publishedRoot));
        Assert.Equal(directoriesBefore, DirectoriesUnder(publishedRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));
        foreach (var exportRoot in exportRoots) Assert.False(Directory.Exists(exportRoot));
    }

    // Holds the live cache for export, captured when the Dry Run ran against it.
    private sealed class RealCandidate(DryRunModel dryRun, LcmCache cache, string scratchRoot,
        DryRunScratch scratch) : IDryRunCandidateForExport
    {
        public DryRunModel DryRun { get; } = dryRun;

        public Task ExportAsync(string emptyDestination, CancellationToken cancellationToken) =>
            new PanGlossCandidateExporter(scratchRoot).ExportAsync(cache, emptyDestination, cancellationToken);

        public void Dispose() => scratch.Dispose();
    }

    private static string BuildSetGlossProposalJson(Guid targetId, string text)
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

    private static string BuildFakeReportJson() => """
        {
          "keyTable": ["11111111-1111-1111-1111-111111111111"],
          "cases": [
            {
              "input": "motifa",
              "outcome": "complete",
              "analyses": [
                { "identity": { "morphemes": [0], "rootIndex": 0 }, "identityDigest": "digest-1" }
              ]
            }
          ],
          "outcomeDigest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "semanticDigest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "provenance": {
            "sourceSha256": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            "modelFingerprint": "fp-1"
          },
          "execution": { "pipeline": "foma-confirm" },
          "diagnostics": []
        }
        """;

    private static string WriteFakeSuccessExecutable(string directory, string reportJson)
    {
        var envVarName = "PG_FAKE_REPORT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVarName, reportJson);
        var scriptPath = Path.Combine(directory, "fake-pangloss.cmd");
        File.WriteAllText(scriptPath,
            "@echo off\r\n" +
            $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"[System.IO.File]::WriteAllText('%~4', $env:{envVarName})\"\r\n");
        return scriptPath;
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
}
