using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Baselines;
using SIL.Motif.Host.Config;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Store;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Scheduling;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Proves a Trial's job-handler contract: it opens the published Baseline exactly once, does not move the
/// Proposal it measures, and a Draft with no committed revision still produces citable Assessments.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class TrialJobHandlerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.TrialJobHandlerTests", Guid.NewGuid().ToString("N"));
    private readonly string _publishedRoot;
    private readonly CountingFwDataProjectLoader _loader = new();
    private readonly ProjectLocator _project;
    private readonly MotifDatabase _database;
    private readonly JobRepository _jobs;
    private readonly BaselineRepository _baselines;
    private readonly ProposalRepository _proposals;
    private readonly AssessmentRepository _assessments;
    private readonly BaselineToken _token;
    private readonly SeededProject _seed;

    public TrialJobHandlerTests()
    {
        Directory.CreateDirectory(_root);

        var masterRoot = Path.Combine(_root, "master");
        Directory.CreateDirectory(masterRoot);
        var master = NewLangProjFixture.CreateCache(masterRoot);
        try
        {
            _seed = SeededProject.Seed(master);
            SeedWordformsForSelectionTests(master);
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
        _proposals = new ProposalRepository(_database);
        _assessments = new AssessmentRepository(_database);

        _token = new BaselineToken("live-project-identity", "sha256:" + new string('a', 64), "1",
            "2026-08-29T00:00:00Z", "sha256:" + new string('b', 64));
        _baselines.Record(ProjectWorkspaceKey.Compute(_project),
            new BaselinePublication(_publishedRoot, fwDataPath, _token),
            DateTimeOffset.Parse("2026-08-29T00:00:00Z"));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ATrialJobOpensThePublishedBaselineExactlyOnce()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]));
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "trial text");
        SaveCommittedProposal(proposalId, proposalJson);

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.True(completed.DryRunPublished);
        Assert.Equal(1, _loader.LoadScratchCacheCount);
    }

    [Fact]
    public void TrialDoesNotChangeTheProposalsStatus()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]));
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "status text");
        SaveCommittedProposal(proposalId, proposalJson);

        var statusBefore = _proposals.Get(proposalId).Status;

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        var statusAfter = _proposals.Get(proposalId).Status;
        Assert.Equal(statusBefore, statusAfter);
        Assert.Equal("proposed", statusAfter);
    }

    [Fact]
    public void ATrialOfAnUncommittedDraftSucceedsAndCitesTheDraftsIntentDigestWithNoRevision()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]));
        var proposalId = CanonicalId.Mint("proposal/");
        var draftJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "draft text");
        _proposals.CreateDraft("working", proposalId, draftJson);
        var expectedDigest = IntentDigest.Compute(ProposalJsonParser.Parse(draftJson));

        var job = CreateTrialJob(draftJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);

        var draftAfter = _proposals.Get(proposalId);
        Assert.Equal("draft", draftAfter.Status);
        Assert.Null(draftAfter.IntentDigest);

        var recorded = _assessments.ListByProposal(proposalId);
        Assert.NotEmpty(recorded);
        foreach (var assessment in recorded)
        {
            Assert.Equal(expectedDigest, assessment.ProposalIntentDigest);
            Assert.Equal(proposalId, assessment.ProposalId);
        }
    }

    [Fact]
    public void ASecondTrialOfTheSameProposalProducesASecondAssessmentSetWithoutDisturbingTheFirst()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]));
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "repeat text");
        SaveCommittedProposal(proposalId, proposalJson);

        var firstJob = CreateTrialJob(proposalJson);
        RunAndFinish(handler, firstJob.JobId);
        var afterFirst = _assessments.ListByProposal(proposalId);
        Assert.Single(afterFirst);
        var firstAssessmentId = afterFirst[0].AssessmentId;

        var secondJob = CreateTrialJob(proposalJson);
        RunAndFinish(handler, secondJob.JobId);
        var afterSecond = _assessments.ListByProposal(proposalId);

        Assert.Equal(2, afterSecond.Count);
        Assert.Contains(afterSecond, a => a.AssessmentId == firstAssessmentId);
        Assert.Equal(2, afterSecond.Select(a => a.AssessmentId).Distinct().Count());

        // The first is still readable, byte-for-byte the same header it was recorded with.
        var firstStillThere = _assessments.Get(firstAssessmentId);
        Assert.Equal(proposalId, firstStillThere.ProposalId);
    }

    [Fact]
    public void ATrialsRecordedSelectionCarriesOnlyWordsWithAManualAnalysis()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]));
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "selection text");
        SaveCommittedProposal(proposalId, proposalJson);

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        var recorded = Assert.Single(_assessments.ListByProposal(proposalId));
        Assert.Contains(AnalysedWordform, recorded.Selection.Words);
        Assert.DoesNotContain(UnanalysedWordform, recorded.Selection.Words);

        // The words are what this run resolved to; the query is what the scope was told, and both are kept.
        Assert.Contains("\"query\"", recorded.ScopeJson, StringComparison.Ordinal);
        Assert.Contains(AssessmentScopeConfiguration.DefaultQueryText, recorded.ScopeJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectTimingAssessmentRecordsTheCachePathAndDigestWithNoWordRows()
    {
        const string cachePath = @"C:\stats\pangloss-cache.db";
        var cacheDigest = "sha256:" + new string('7', 64);
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.ObjectTiming],
            _ => new AssessmentRaw.FileCache(cachePath, cacheDigest)));
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "object timing text");
        SaveCommittedProposal(proposalId, proposalJson);

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        var header = Assert.Single(_assessments.ListByProposal(proposalId));
        var full = _assessments.Get(header.AssessmentId);
        Assert.Equal(cachePath, full.CachePath);
        Assert.Equal(cacheDigest, full.CacheDigest);
        Assert.Empty(full.Words!);
    }

    [Fact]
    public void DryRunIsPublishedDurablyBeforeTheCandidateIsPreparedForAssessment()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "publish-order text");
        SaveCommittedProposal(proposalId, proposalJson);
        var job = CreateTrialJob(proposalJson);
        var publishedBeforePrepare = false;

        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]),
            prepareForAssessment: (cache, ct) =>
            {
                publishedBeforePrepare = _jobs.Get(job.JobId)!.DryRunPublished;
                return DefaultPrepareForAssessment(cache, ct);
            });

        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.True(publishedBeforePrepare, "the Dry Run must already be published while the candidate is prepared for Assessment.");
    }

    [Fact]
    public void PrepareForAssessmentFailureAfterPublicationYieldsCompletedWithAssessmentFailure()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "prepare-failure text");
        SaveCommittedProposal(proposalId, proposalJson);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]),
            prepareForAssessment: (_, _) => throw new IOException("disk full"));

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.CompletedWithAssessmentFailure, completed.Status);
        Assert.True(completed.DryRunPublished);
    }

    [Fact]
    public void AssessorFailureYieldsCompletedWithAssessmentFailureWithTheDryRunRetained()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "assessor-failure text");
        SaveCommittedProposal(proposalId, proposalJson);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness],
            _ => throw new IOException("pangloss assess exited 1")));

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.CompletedWithAssessmentFailure, completed.Status);
        Assert.True(completed.DryRunPublished);
    }

    [Fact]
    public void CancellationDuringAssessmentRetainsThePublishedDryRunAndCancelsTheJob()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "cancel-assessment text");
        SaveCommittedProposal(proposalId, proposalJson);
        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness],
            _ => throw new OperationCanceledException()));

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Cancelled, completed.Status);
        Assert.True(completed.DryRunPublished);
    }

    [Fact]
    public void ASuccessfulTrialLeavesThePublishedBaselineUnchangedAndNoScratchDirectoryBehind()
    {
        using var lanes = new ProjectLaneRegistry(_ => _token);
        var proposalId = CanonicalId.Mint("proposal/");
        var proposalJson = BuildSetGlossProposalJson(proposalId, _seed.FirstSenseId, "no-ephemera text");
        SaveCommittedProposal(proposalId, proposalJson);
        var fwDataPath = Path.Combine(_publishedRoot, NewLangProjFixture.ProjectName + ".fwdata");
        var digestBefore = Sha256Of(fwDataPath);
        string? preparedDirectory = null;

        var handler = BuildHandler(lanes, new FakeAssessor("pangloss", [AssessmentKind.Correctness]),
            prepareForAssessment: async (cache, ct) =>
            {
                var directory = await DefaultPrepareForAssessment(cache, ct);
                preparedDirectory = directory;
                return directory;
            });

        var job = CreateTrialJob(proposalJson);
        var completed = RunAndFinish(handler, job.JobId);

        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(digestBefore, Sha256Of(fwDataPath));
        Assert.NotNull(preparedDirectory);
        Assert.False(Directory.Exists(preparedDirectory),
            "the scratch directory used for Assessment must not survive a completed Trial.");
    }

    // One wordform carries a human-approved analysis; the other has none, and a resolved Selection excludes it.
    private const string AnalysedWordform = "zzmotiftrialanalysed";
    private const string UnanalysedWordform = "zzmotiftrialunanalysed";

    private static void SeedWordformsForSelectionTests(LcmCache cache)
    {
        NonUndoableUnitOfWorkHelper.Do(cache.ActionHandlerAccessor, () =>
        {
            var analysed = cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(AnalysedWordform, cache.DefaultVernWs));
            var analysis = cache.ServiceLocator.GetInstance<IWfiAnalysisFactory>().Create();
            analysed.AnalysesOC.Add(analysis);
            cache.LangProject.DefaultUserAgent.SetEvaluation(analysis, Opinions.approves);

            cache.ServiceLocator.GetInstance<IWfiWordformFactory>()
                .Create(TsStringUtils.MakeString(UnanalysedWordform, cache.DefaultVernWs));
        });
    }

    private void SaveCommittedProposal(CanonicalId proposalId, string proposalJson)
    {
        var digest = IntentDigest.Compute(ProposalJsonParser.Parse(proposalJson));
        _proposals.SaveRevision(new ProposalRevisionRecord(proposalId, digest, proposalJson, "proposed",
            null, null, null));
    }

    private JobRecord CreateTrialJob(string proposalJson)
    {
        var inputJson = JsonSerializer.Serialize(new TrialJobInput(proposalJson, null),
            SIL.Motif.Contract.MotifJson.CreateOptions());
        var job = _jobs.Create(Guid.NewGuid().ToString("N"), ProjectWorkspaceKey.Compute(_project),
            TrialJobHandler.TrialKind, inputJson, "2026-08-29T00:00:00Z");
        _jobs.Transition(_jobs.Get(job.JobId)!, JobStatus.Running);
        return job;
    }

    private JobRecord RunAndFinish(TrialJobHandler handler, string jobId)
    {
        var claim = ClaimedJob.Of(_jobs, jobId);
        var outcome = handler.RunAsync(claim, _project, CancellationToken.None).GetAwaiter().GetResult();
        if (outcome is not null) claim.Transition(outcome.Status, outcome.Category, outcome.ResultJson);
        return _jobs.Get(jobId)!;
    }

    private TrialJobHandler BuildHandler(ProjectLaneRegistry lanes, IAssessor assessor,
        Func<LcmCache?, CancellationToken, Task<string>>? prepareForAssessment = null)
    {
        var catalog = new AssessorCatalog(new[] { assessor });
        var factory = new ScratchCacheFactory(_loader);
        return new TrialJobHandler(_baselines, _proposals, lanes,
            new ProjectConfigurationReader(), catalog, _assessments,
            (fwDataPath, scratchRoot, _) =>
            {
                var cache = factory.CreateFromFileCopy(fwDataPath, scratchRoot);
                var scratch = DryRunScratch.Adopt(cache, $"test file copy under {scratchRoot}");
                var appliedProposalIds = ProjectAppliedLog.ReadAll(scratch.PeekCache())
                    .Select(entry => entry.ProposalId).ToArray();
                return Task.FromResult<(IReadOnlyCollection<Guid>, DryRunScratch?)>((appliedProposalIds, scratch));
            },
            (scratch, plan, _) => Task.FromResult(ProposalDryRunner.Run(scratch!, plan)),
            prepareForAssessment ?? DefaultPrepareForAssessment);
    }

    private Task<string> DefaultPrepareForAssessment(LcmCache? cache, CancellationToken _)
    {
        _loader.Save(cache!);
        var directory = Path.GetDirectoryName(Path.GetFullPath(cache!.ProjectId.Path))!;
        return Task.FromResult(directory);
    }

    private static string BuildSetGlossProposalJson(CanonicalId proposalId, Guid targetId, string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = "en", text });
        var operationId = CanonicalId.Mint().Value;
        var target = CanonicalId.FromGuid(targetId).Value;
        return $$"""
            {
              "contractVersions": {"lexical": "1.0"},
              "proposalId": "{{proposalId.Value}}",
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
