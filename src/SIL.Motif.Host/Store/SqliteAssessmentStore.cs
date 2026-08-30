using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Host.Analysis;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Store;

/// <summary>
/// Stores Assessments in the embedded database ADR 0036 decision 6 assigns them to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normalised, not blobbed.</b> Each <see cref="AssessedWord"/> and each of its
/// <see cref="ParsedAnalysis"/>es is its own row (<c>AssessedWords</c>, <c>ParsedAnalyses</c>), so
/// <see cref="CountWords"/> and <see cref="CountAnalyses"/> are a SQL <c>COUNT(*)</c> that never constructs a
/// <see cref="StoredAssessment"/>, never deserialises a morpheme list, and never touches the corpus's word
/// set — an Assessment at the plan's ~64 MB/100,000-word-form scale answers "how many word forms" in the same
/// cost as one at ten. <see cref="Load"/> is the one operation that reads all of it, because that is what a
/// caller asking for the whole Assessment means.
/// </para>
/// <para>
/// Word order and analysis order are preserved with an explicit ordinal column apiece rather than relying on
/// SQLite's row order, so a round trip returns the exact list <see cref="AssessReport.Words"/> and
/// <see cref="AssessedWord.Analyses"/> started with.
/// </para>
/// </remarks>
public sealed class SqliteAssessmentStore : IAssessmentStore
{
    private readonly string _databasePath;

    public SqliteAssessmentStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    public bool Exists(string assessmentId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Assessments WHERE AssessmentId = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", assessmentId);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<string> List()
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AssessmentId FROM Assessments ORDER BY AssessmentId;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>A SQL <c>COUNT(*)</c> on <c>AssessedWords</c> — no row is turned into an <see cref="AssessedWord"/>.</summary>
    public int CountWords(string assessmentId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AssessedWords WHERE AssessmentId = $id;";
        command.Parameters.AddWithValue("$id", assessmentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>A SQL <c>COUNT(*)</c> on <c>ParsedAnalyses</c> — no morpheme list is deserialised.</summary>
    public int CountAnalyses(string assessmentId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ParsedAnalyses pa
            JOIN AssessedWords aw ON aw.AssessedWordId = pa.AssessedWordId
            WHERE aw.AssessmentId = $id;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Pin(string assessmentId, string pinnedBy)
    {
        if (string.IsNullOrWhiteSpace(pinnedBy)) throw new ArgumentException("Required.", nameof(pinnedBy));

        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AssessmentPins (AssessmentId, PinnedBy, PinnedUtc) VALUES ($id, $pinnedBy, $utc)
            ON CONFLICT(AssessmentId, PinnedBy) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        command.Parameters.AddWithValue("$pinnedBy", pinnedBy);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void Unpin(string assessmentId, string pinnedBy)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AssessmentPins WHERE AssessmentId = $id AND PinnedBy = $pinnedBy;";
        command.Parameters.AddWithValue("$id", assessmentId);
        command.Parameters.AddWithValue("$pinnedBy", pinnedBy);
        command.ExecuteNonQuery();
    }

    /// <summary>The pruning candidate query (ADR 0036's open pruning question) — finds; never deletes.</summary>
    public IReadOnlyList<string> ListUnpinnedAssessmentIds()
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AssessmentId FROM Assessments a
            WHERE NOT EXISTS (SELECT 1 FROM AssessmentPins p WHERE p.AssessmentId = a.AssessmentId)
            ORDER BY AssessmentId;
            """;

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public string Save(StoredAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var assessmentId = AssessmentIdentity.ComputeId(assessment);

        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);
        using var transaction = connection.BeginTransaction();

        DeleteExisting(connection, transaction, assessmentId);
        InsertAssessmentRow(connection, transaction, assessmentId, assessment);
        InsertWordsAndAnalyses(connection, transaction, assessmentId, assessment.Report.Words);

        transaction.Commit();
        return assessmentId;
    }

    private static void DeleteExisting(SqliteConnection connection, SqliteTransaction transaction, string assessmentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM ParsedAnalyses
                WHERE AssessedWordId IN (SELECT AssessedWordId FROM AssessedWords WHERE AssessmentId = $id);
            DELETE FROM AssessedWords WHERE AssessmentId = $id;
            DELETE FROM AssessmentPins WHERE AssessmentId = $id;
            DELETE FROM Assessments WHERE AssessmentId = $id;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        command.ExecuteNonQuery();
    }

    private static void InsertAssessmentRow(
        SqliteConnection connection, SqliteTransaction transaction, string assessmentId, StoredAssessment assessment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Assessments
                (AssessmentId, CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson,
                 OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline,
                 DiagnosticCount, SavedUtc)
            VALUES
                ($id, $corpusId, $corpusWords, $corpusSha, $corpusProvenance,
                 $outcomeDigest, $semanticDigest, $grammarSha, $modelFingerprint, $pipeline,
                 $diagnosticCount, $savedUtc);
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        command.Parameters.AddWithValue("$corpusId", assessment.Selection.Name);
        command.Parameters.AddWithValue("$corpusWords", JsonSerializer.Serialize(assessment.Selection.Words));
        command.Parameters.AddWithValue("$corpusSha", assessment.Selection.Sha256);
        command.Parameters.AddWithValue(
            "$corpusProvenance",
            assessment.Selection.Provenance is null ? DBNull.Value : JsonSerializer.Serialize(assessment.Selection.Provenance));
        command.Parameters.AddWithValue("$outcomeDigest", assessment.Report.OutcomeDigest);
        command.Parameters.AddWithValue("$semanticDigest", assessment.Report.SemanticDigest);
        command.Parameters.AddWithValue("$grammarSha", assessment.Report.GrammarSourceSha256);
        command.Parameters.AddWithValue("$modelFingerprint", assessment.Report.ModelFingerprint);
        command.Parameters.AddWithValue("$pipeline", assessment.Report.Pipeline);
        command.Parameters.AddWithValue("$diagnosticCount", assessment.Report.DiagnosticCount);
        command.Parameters.AddWithValue("$savedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void InsertWordsAndAnalyses(
        SqliteConnection connection, SqliteTransaction transaction, string assessmentId, IReadOnlyList<AssessedWord> words)
    {
        using var insertWord = connection.CreateCommand();
        insertWord.Transaction = transaction;
        insertWord.CommandText = """
            INSERT INTO AssessedWords (AssessmentId, OrdinalIndex, Word, Outcome) VALUES ($id, $ordinal, $word, $outcome);
            """;
        var assessmentIdParam = insertWord.Parameters.AddWithValue("$id", assessmentId);
        var wordOrdinalParam = insertWord.Parameters.Add("$ordinal", SqliteType.Integer);
        var wordTextParam = insertWord.Parameters.Add("$word", SqliteType.Text);
        var wordOutcomeParam = insertWord.Parameters.Add("$outcome", SqliteType.Text);

        using var lastRowId = connection.CreateCommand();
        lastRowId.Transaction = transaction;
        lastRowId.CommandText = "SELECT last_insert_rowid();";

        using var insertAnalysis = connection.CreateCommand();
        insertAnalysis.Transaction = transaction;
        insertAnalysis.CommandText = """
            INSERT INTO ParsedAnalyses (AssessedWordId, OrdinalIndex, CategoryGuid, MorphemeGuidsJson, RootIndex, IdentityDigest)
            VALUES ($wordId, $ordinal, $category, $morphemes, $rootIndex, $identity);
            """;
        var analysisWordIdParam = insertAnalysis.Parameters.Add("$wordId", SqliteType.Integer);
        var analysisOrdinalParam = insertAnalysis.Parameters.Add("$ordinal", SqliteType.Integer);
        var analysisCategoryParam = insertAnalysis.Parameters.Add("$category", SqliteType.Text);
        var analysisMorphemesParam = insertAnalysis.Parameters.Add("$morphemes", SqliteType.Text);
        var analysisRootIndexParam = insertAnalysis.Parameters.Add("$rootIndex", SqliteType.Integer);
        var analysisIdentityParam = insertAnalysis.Parameters.Add("$identity", SqliteType.Text);

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            assessmentIdParam.Value = assessmentId;
            wordOrdinalParam.Value = wordIndex;
            wordTextParam.Value = word.Word;
            wordOutcomeParam.Value = word.Outcome;
            insertWord.ExecuteNonQuery();

            var assessedWordId = (long)lastRowId.ExecuteScalar()!;

            for (var analysisIndex = 0; analysisIndex < word.Analyses.Count; analysisIndex++)
            {
                var analysis = word.Analyses[analysisIndex];
                analysisWordIdParam.Value = assessedWordId;
                analysisOrdinalParam.Value = analysisIndex;
                analysisCategoryParam.Value = (object?)analysis.CategoryGuid ?? DBNull.Value;
                analysisMorphemesParam.Value = JsonSerializer.Serialize(analysis.MorphemeGuids);
                analysisRootIndexParam.Value = analysis.RootIndex;
                analysisIdentityParam.Value = analysis.IdentityDigest;
                insertAnalysis.ExecuteNonQuery();
            }
        }
    }

    public StoredAssessment? Load(string assessmentId)
    {
        using var connection = SqliteMotifDatabase.OpenConnection(_databasePath);

        var header = LoadHeader(connection, assessmentId);
        if (header is null) return null;

        var words = LoadWords(connection, assessmentId);

        var report = new AssessReport(
            Words: words,
            OutcomeDigest: header.OutcomeDigest,
            SemanticDigest: header.SemanticDigest,
            GrammarSourceSha256: header.GrammarSourceSha256,
            ModelFingerprint: header.ModelFingerprint,
            Pipeline: header.Pipeline,
            DiagnosticCount: header.DiagnosticCount);

        var selection = new Selection(header.CorpusId, header.CorpusWords, header.CorpusSha256, header.CorpusProvenance);

        return new StoredAssessment(report, selection);
    }

    private sealed record AssessmentHeader(
        string CorpusId,
        IReadOnlyList<string> CorpusWords,
        string CorpusSha256,
        CorpusProvenance? CorpusProvenance,
        string OutcomeDigest,
        string SemanticDigest,
        string GrammarSourceSha256,
        string ModelFingerprint,
        string Pipeline,
        int DiagnosticCount);

    private static AssessmentHeader? LoadHeader(SqliteConnection connection, string assessmentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson,
                   OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline, DiagnosticCount
            FROM Assessments WHERE AssessmentId = $id;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new AssessmentHeader(
            CorpusId: reader.GetString(0),
            CorpusWords: JsonSerializer.Deserialize<List<string>>(reader.GetString(1))!,
            CorpusSha256: reader.GetString(2),
            CorpusProvenance: reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<CorpusProvenance>(reader.GetString(3)),
            OutcomeDigest: reader.GetString(4),
            SemanticDigest: reader.GetString(5),
            GrammarSourceSha256: reader.GetString(6),
            ModelFingerprint: reader.GetString(7),
            Pipeline: reader.GetString(8),
            DiagnosticCount: reader.GetInt32(9));
    }

    /// <summary>One streaming pass over a word/analysis join, grouped by word — no N+1 querying.</summary>
    private static List<AssessedWord> LoadWords(SqliteConnection connection, string assessmentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT aw.AssessedWordId, aw.Word, aw.Outcome, pa.CategoryGuid, pa.MorphemeGuidsJson, pa.RootIndex, pa.IdentityDigest
            FROM AssessedWords aw
            LEFT JOIN ParsedAnalyses pa ON pa.AssessedWordId = aw.AssessedWordId
            WHERE aw.AssessmentId = $id
            ORDER BY aw.OrdinalIndex, pa.OrdinalIndex;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);

        var words = new List<AssessedWord>();
        long? currentWordId = null;
        string currentWord = "";
        string currentOutcome = "";
        List<ParsedAnalysis> currentAnalyses = new();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var wordId = reader.GetInt64(0);
            if (wordId != currentWordId)
            {
                if (currentWordId is not null) words.Add(new AssessedWord(currentWord, currentOutcome, currentAnalyses));
                currentWordId = wordId;
                currentWord = reader.GetString(1);
                currentOutcome = reader.GetString(2);
                currentAnalyses = new List<ParsedAnalysis>();
            }

            if (!reader.IsDBNull(6))  // IdentityDigest is NOT NULL on ParsedAnalyses; NULL here means "no analysis row"
            {
                currentAnalyses.Add(new ParsedAnalysis(
                    CategoryGuid: reader.IsDBNull(3) ? null : reader.GetString(3),
                    MorphemeGuids: JsonSerializer.Deserialize<List<string>>(reader.GetString(4))!,
                    RootIndex: reader.GetInt32(5),
                    IdentityDigest: reader.GetString(6)));
            }
        }

        if (currentWordId is not null) words.Add(new AssessedWord(currentWord, currentOutcome, currentAnalyses));
        return words;
    }
}
