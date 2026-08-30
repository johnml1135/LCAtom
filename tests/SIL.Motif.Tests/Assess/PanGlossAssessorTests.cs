using System.Security.Cryptography;
using SIL.Motif.Host.Assess;
using SIL.Motif.Host.Parser;
using SIL.Motif.Worker.Assess;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Assess;

/// <summary>
/// PanGloss's own Assessor, composed from existing seams. These tests never touch a real or fake
/// executable — the process boundary belongs to <see cref="PanGlossParser"/>, <see cref="IPanGlossAssessor"/>
/// and <see cref="IPanGlossStatsRunner"/> individually; this seam's job is what it declares and what it
/// records once those run, which is what is under test here.
/// </summary>
public sealed class PanGlossAssessorTests : IDisposable
{
    private const string GrammarSha256 = "sha256:" + "cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc33cc";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-pangloss-assessor-" + Guid.NewGuid().ToString("N"));
    private readonly string _exportedCandidate;
    private readonly StatsCacheStore _cachePaths;

    public PanGlossAssessorTests()
    {
        Directory.CreateDirectory(_root);
        _exportedCandidate = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(_exportedCandidate);
        File.WriteAllText(Path.Combine(_exportedCandidate, "candidate.fwdata"), "the fake runners never read this.");
        _cachePaths = new StatsCacheStore(WorkspaceOwnership.Bootstrap(Path.Combine(_root, "worker")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    [Fact]
    public async Task TheRecordedAssessmentCarriesTheCachePathAndDigest()
    {
        var cacheBytes = "pretend sqlite bytes"u8.ToArray();
        var statsRunner = new FakeStatsRunner(cacheBytes);
        var assessor = new PanGlossAssessor(
            _cachePaths, new FakeBatchParser(null), new FakeReportRunner(Report()), statsRunner);
        var scope = Scope(AssessmentKind.ObjectTiming);

        var produced = Assert.Single(await assessor.ProduceAsync(scope, _exportedCandidate, CancellationToken.None));

        var expectedPath = _cachePaths.PathFor(GrammarSha256, PanGlossAssessor.AssessorName, "fast");
        Assert.Equal(AssessmentKind.ObjectTiming, produced.Kind);
        Assert.Equal(expectedPath, produced.CachePath);
        Assert.Equal(ExpectedDigest(cacheBytes), produced.CacheDigest);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task DifferentGrammarDigests_RecordDifferentCachePaths()
    {
        var statsRunner = new FakeStatsRunner("bytes"u8.ToArray());
        var assessor = new PanGlossAssessor(
            _cachePaths, new FakeBatchParser(null), new FakeReportRunner(Report()), statsRunner);

        var first = Assert.Single(await assessor.ProduceAsync(
            Scope(AssessmentKind.ObjectTiming), _exportedCandidate, CancellationToken.None));

        var otherReportRunner = new FakeReportRunner(Report(grammarSha256:
            "sha256:" + "dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd44dd"));
        var otherAssessor = new PanGlossAssessor(
            _cachePaths, new FakeBatchParser(null), otherReportRunner, new FakeStatsRunner("bytes"u8.ToArray()));
        var second = Assert.Single(await otherAssessor.ProduceAsync(
            Scope(AssessmentKind.ObjectTiming), _exportedCandidate, CancellationToken.None));

        Assert.NotEqual(first.CachePath, second.CachePath);
    }

    [Fact]
    public void KindsFor_DefaultsToParseTimeAndCorrectness_WhenTheScopeCollectsNothingSpecific()
    {
        var assessor = NeverRunningAssessor();

        var kinds = assessor.KindsFor(Scope());

        Assert.Equal(new[] { AssessmentKind.ParseTime, AssessmentKind.Correctness }, kinds);
    }

    [Fact]
    public void KindsFor_NeverDeclaresEngineSizeDifferenceOrCompletion()
    {
        var assessor = NeverRunningAssessor();

        var kinds = assessor.KindsFor(Scope(
            AssessmentKind.EngineSize, AssessmentKind.Difference, AssessmentKind.Completion));

        Assert.Empty(kinds);
    }

    [Fact]
    public async Task AskingForEngineSize_RefusesNamingTheKind()
    {
        var assessor = NeverRunningAssessor();

        var failure = await Assert.ThrowsAsync<AssessorRefusalException>(() => assessor.ProduceAsync(
            Scope(AssessmentKind.EngineSize), _exportedCandidate, CancellationToken.None));

        Assert.Equal(AssessmentKind.EngineSize, failure.Kind);
    }

    // A declaration-only assessor: every runner throws if actually invoked, since none of these tests should.
    private PanGlossAssessor NeverRunningAssessor() => new(
        _cachePaths,
        new FakeBatchParser(null),
        new FakeReportRunner(Report()),
        new FakeStatsRunner([]));

    [Fact]
    public async Task AnUnrecognizedEngineName_IsRefused()
    {
        var assessor = new PanGlossAssessor(
            _cachePaths, new FakeBatchParser(null), new FakeReportRunner(Report()), new FakeStatsRunner([]));
        var scope = new AssessmentScope(
            words: ["motifa"], engine: "turbo", collect: [AssessmentKind.Correctness], perWordLimit: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            assessor.ProduceAsync(scope, _exportedCandidate, CancellationToken.None));
    }

    private static AssessmentScope Scope(params AssessmentKind[] collect) => new(
        words: ["motifa"], engine: "fast", collect: collect, perWordLimit: TimeSpan.FromSeconds(1));

    private static AssessReport Report(string? grammarSha256 = null) => new(
        Words: [],
        OutcomeDigest: "sha256:" + new string('a', 64),
        SemanticDigest: "sha256:" + new string('b', 64),
        GrammarSourceSha256: grammarSha256 ?? GrammarSha256,
        ModelFingerprint: "fp-1",
        Pipeline: "foma-confirm",
        DiagnosticCount: 0);

    private static string ExpectedDigest(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private sealed class FakeReportRunner(AssessReport report) : IPanGlossAssessor
    {
        public Task<AssessReport> RunAsync(string exportedCandidate, CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }

    private sealed class FakeStatsRunner(byte[] bytesToWrite) : IPanGlossStatsRunner
    {
        public Task RunBatchAsync(string projectFilePath, IReadOnlyList<string> words, ParserEngine engine,
            TimeSpan perWordLimit, string cachePath, CancellationToken cancellationToken)
        {
            File.WriteAllBytes(cachePath, bytesToWrite);
            return Task.CompletedTask;
        }
    }

    // Never actually asked to run in these tests; ParseTime is not among the collected kinds they exercise.
    private sealed class FakeBatchParser(ParserRunResult? result) : PanGlossParser("unused")
    {
        public override ParserRunResult AnalyseBatch(
            string projectFilePath, IReadOnlyList<string> words,
            ParserEngine engine = ParserEngine.FstPrunedByHermitCrab,
            int? perWordTimeoutMs = 5000, TimeSpan? processTimeout = null) =>
            result ?? throw new InvalidOperationException("This test never expected AnalyseBatch to run.");
    }
}
