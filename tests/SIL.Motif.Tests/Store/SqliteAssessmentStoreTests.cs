using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;
using SIL.Motif.Host.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>
/// <see cref="SqliteAssessmentStore"/> round-trips a <see cref="StoredAssessment"/> through the embedded
/// database ADR 0036 decision 6 assigns it to, and proves its aggregate reads
/// (<see cref="IAssessmentStore.CountWords"/>, <see cref="IAssessmentStore.CountAnalyses"/>) are a SQL
/// <c>COUNT(*)</c> rather than "load the whole Assessment and count the list" — the two would agree on every
/// well-formed Assessment, so the proof needs a case where they would visibly disagree.
/// </summary>
public sealed class SqliteAssessmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-sqlite-assessment-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string DatabasePath => Path.Combine(_root, "motif.db");
    private SqliteAssessmentStore Store() => new(DatabasePath);

    private static ParsedAnalysis Analysis(string digest, params string[] morphemes) =>
        new(CategoryGuid: "cat-" + digest, MorphemeGuids: morphemes, RootIndex: 0, IdentityDigest: digest);

    private static AssessedWord Word(string word, string outcome, params ParsedAnalysis[] analyses) =>
        new(word, outcome, analyses);

    private static StoredAssessment Assessment(params AssessedWord[] words)
    {
        var corpus = Selection.Create(
            "reach-test",
            words.Select(w => w.Word),
            new CorpusProvenance(
                new CorpusOrigin("Testlang corpus", null, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), "internal"),
                new TokenisationRecord("whitespace-and-punctuation", "1", "")));

        var report = new AssessReport(
            Words: words,
            OutcomeDigest: "outcome-" + words.Length,
            SemanticDigest: "semantic-digest",
            GrammarSourceSha256: "sha256:" + new string('a', 64),
            ModelFingerprint: "model-fingerprint",
            Pipeline: "foma-confirm",
            DiagnosticCount: 3);

        return new StoredAssessment(report, corpus);
    }

    [Fact]
    public void AnAssessmentRoundTripsThroughTheDatabase_EveryFieldAndOrderIntact()
    {
        var store = Store();
        var assessment = Assessment(
            Word("mbali", "Analysed", Analysis("d1", "m1", "m2"), Analysis("d2", "m3")),
            Word("nyumba", "NoAnalysis"));

        var id = store.Save(assessment);
        var loaded = store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(assessment.Report.OutcomeDigest, loaded!.Report.OutcomeDigest);
        Assert.Equal(assessment.Report.GrammarSourceSha256, loaded.Report.GrammarSourceSha256);
        Assert.Equal(assessment.Report.DiagnosticCount, loaded.Report.DiagnosticCount);
        Assert.Equal(assessment.Selection.Name, loaded.Selection.Name);
        Assert.Equal(assessment.Selection.Sha256, loaded.Selection.Sha256);
        Assert.Equal(assessment.Selection.Words, loaded.Selection.Words);
        Assert.Equal(assessment.Selection.Provenance!.Origin.Description, loaded.Selection.Provenance!.Origin.Description);

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
    public void IdentityIsContent_SoSavingTheSameAssessmentTwiceCollapsesOntoOneRow()
    {
        var store = Store();
        var assessment = Assessment(Word("mbali", "Analysed", Analysis("d1", "m1")));

        var first = store.Save(assessment);
        var second = store.Save(assessment);

        Assert.Equal(first, second);
        Assert.Equal(new[] { first }, store.List());
        Assert.Equal(1, store.CountWords(first));
    }

    [Fact]
    public void CountWordsAndCountAnalyses_AreAggregatesThatSurviveAPoisonedAnalysisRow()
    {
        var store = Store();
        var assessment = Assessment(
            Word("mbali", "Analysed", Analysis("d1", "m1")),
            Word("nyumba", "Analysed", Analysis("d2", "m2"), Analysis("d3", "m3")));
        var id = store.Save(assessment);

        // Corrupt one analysis's JSON directly, bypassing the store's writer: COUNT(*) must not notice.
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ParsedAnalyses SET MorphemeGuidsJson = 'not-json{{{' WHERE IdentityDigest = 'd3';";
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
        var pinned = store.Save(Assessment(Word("mbali", "Analysed", Analysis("d1", "m1"))));
        var unpinned = store.Save(Assessment(Word("nyumba", "Analysed", Analysis("d2", "m1"))));

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
        var id = store.Save(Assessment(Word("mbali", "Analysed", Analysis("d1", "m1"))));

        store.Pin(id, "proposal-1");
        store.Pin(id, "proposal-1");   // must not throw a primary-key conflict

        Assert.Empty(store.ListUnpinnedAssessmentIds());
    }
}
