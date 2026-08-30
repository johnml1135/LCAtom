using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>
/// <see cref="SqliteAssessmentStore"/> is the read side of the Assessments table: the worker writes through
/// <see cref="AssessmentRepository"/> and <c>motif analyses</c> reads back through this type, from the one
/// project database. Every case here therefore seeds the way production does, because the store once had a
/// writer of its own and the two drifted apart without a test noticing.
/// <para>
/// <see cref="SqliteAssessmentStore.CountWords"/> and <see cref="SqliteAssessmentStore.CountAnalyses"/> are a
/// SQL <c>COUNT(*)</c> rather than "load the whole Assessment and count the list" — the two would agree on
/// every well-formed Assessment, so the proof needs a case where they would visibly disagree.
/// </para>
/// </summary>
public sealed class SqliteAssessmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "motif-sqlite-assessment-tests", Guid.NewGuid().ToString("N"));
    private readonly MotifDatabase _database;

    public SqliteAssessmentStoreTests()
    {
        Directory.CreateDirectory(_root);
        _database = MotifDatabase.OpenOwned(
            DatabasePath,
            new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project"),
            MotifSchema.CurrentSchema,
            new Version(1, 0));
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string DatabasePath => Path.Combine(_root, "motif.db");

    private SqliteAssessmentStore Store() => new(DatabasePath);

    private static ParsedAnalysis Analysis(string digest, params string[] morphemes) =>
        new(CategoryGuid: "cat-" + digest, MorphemeGuids: morphemes, RootIndex: 0, IdentityDigest: digest);

    private static AssessedWord Word(string word, string outcome, params ParsedAnalysis[] analyses) =>
        new(word, outcome, analyses);

    private static Selection SelectionOf(params AssessedWord[] words) => Selection.Create(
        "reach-test",
        words.Select(w => w.Word),
        new CorpusProvenance(
            new CorpusOrigin(
                "Testlang selection", null, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), "internal"),
            new TokenisationRecord("whitespace-and-punctuation", "1", "")));

    private string Seed(string assessmentId, params AssessedWord[] words)
    {
        new AssessmentRepository(_database).Record(new NewAssessmentRecord(
            AssessmentId: assessmentId,
            ProposalId: null,
            ProposalIntentDigest: null,
            Assessor: "pangloss",
            Kind: "ParseTime",
            ScopeJson: "{}",
            ScopeDigest: "sha256:scope",
            TokeniserName: "whitespace-and-punctuation",
            TokeniserVersion: "1",
            BaselineToken: "{}",
            Selection: SelectionOf(words),
            OutcomeDigest: "outcome-" + words.Length,
            SemanticDigest: "semantic-digest",
            GrammarSourceSha256: "sha256:" + new string('a', 64),
            ModelFingerprint: "model-fingerprint",
            Pipeline: "foma-confirm",
            DiagnosticCount: 3,
            Words: words));
        return assessmentId;
    }

    [Fact]
    public void LoadReadsBackWhatTheWorkerWroteWithEveryFieldAndOrderIntact()
    {
        var words = new[]
        {
            Word("mbali", "Analysed", Analysis("d1", "m1", "m2"), Analysis("d2", "m3")),
            Word("nyumba", "NoAnalysis"),
        };
        var id = Seed("assessment/round-trip", words);
        var selection = SelectionOf(words);

        var loaded = Store().Load(id);

        Assert.NotNull(loaded);
        Assert.Equal("outcome-2", loaded!.Report.OutcomeDigest);
        Assert.Equal("sha256:" + new string('a', 64), loaded.Report.GrammarSourceSha256);
        Assert.Equal(3, loaded.Report.DiagnosticCount);
        Assert.Equal(selection.Name, loaded.Selection.Name);
        Assert.Equal(selection.Sha256, loaded.Selection.Sha256);
        Assert.Equal(selection.Words, loaded.Selection.Words);
        Assert.Equal("Testlang selection", loaded.Selection.Provenance!.Origin.Description);

        Assert.Equal(2, loaded.Report.Words.Count);
        Assert.Equal("mbali", loaded.Report.Words[0].Word);
        Assert.Equal(2, loaded.Report.Words[0].Analyses.Count);
        // Order within a word's analyses, and morpheme order within an analysis, must both survive.
        Assert.Equal(new[] { "m1", "m2" }, loaded.Report.Words[0].Analyses[0].MorphemeGuids);
        Assert.Equal("d2", loaded.Report.Words[0].Analyses[1].IdentityDigest);
        Assert.Equal("nyumba", loaded.Report.Words[1].Word);
        Assert.Empty(loaded.Report.Words[1].Analyses);
    }

    [Fact]
    public void LoadingAMissingAssessmentReturnsNull()
    {
        Assert.Null(Store().Load("sha256:" + new string('0', 64)));
    }

    [Fact]
    public void CountWordsAndCountAnalyses_AreAggregatesThatSurviveAPoisonedAnalysisRow()
    {
        var id = Seed(
            "assessment/counts",
            Word("mbali", "Analysed", Analysis("d1", "m1")),
            Word("nyumba", "Analysed", Analysis("d2", "m2"), Analysis("d3", "m3")));
        var store = Store();

        // Corrupt one analysis's JSON directly, bypassing the writer: COUNT(*) must not notice.
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE ParsedAnalyses SET MorphemeGuidsJson = 'not-json{{{' WHERE IdentityDigest = 'd3';";
            command.ExecuteNonQuery();
        }

        // The proof: loading the whole Assessment to count would hit this row and throw; SQL COUNT does not.
        Assert.Equal(2, store.CountWords(id));
        Assert.Equal(3, store.CountAnalyses(id));

        Assert.Throws<System.Text.Json.JsonException>(() => store.Load(id));
    }

    [Fact]
    public void UnpinnedAssessmentsAreFoundByQuery_NothingDeletesThem()
    {
        var store = Store();
        var pinned = Seed("assessment/pinned", Word("mbali", "Analysed", Analysis("d1", "m1")));
        var unpinned = Seed("assessment/unpinned", Word("nyumba", "Analysed", Analysis("d2", "m1")));

        store.Pin(pinned, "proposal-42");

        Assert.Equal(new[] { unpinned }, store.ListUnpinnedAssessmentIds());

        // Unpinning removes the record of dependency; it does not touch the Assessment row itself.
        store.Unpin(pinned, "proposal-42");
        Assert.Equal(new[] { pinned, unpinned }.OrderBy(x => x, StringComparer.Ordinal),
            store.ListUnpinnedAssessmentIds().OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(store.Exists(pinned));
    }

    [Fact]
    public void PinningTheSamePairTwiceIsANoOp()
    {
        var store = Store();
        var id = Seed("assessment/pin-twice", Word("mbali", "Analysed", Analysis("d1", "m1")));

        store.Pin(id, "proposal-1");
        store.Pin(id, "proposal-1");   // must not throw a primary-key conflict

        Assert.Empty(store.ListUnpinnedAssessmentIds());
    }
}
