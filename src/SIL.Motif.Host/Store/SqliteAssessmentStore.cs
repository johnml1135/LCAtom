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
public sealed class SqliteAssessmentStore
{
    private readonly string _databasePath;

    public SqliteAssessmentStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
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

        var selection = new Selection(header.SelectionName, header.SelectionWords, header.SelectionSha256, header.SelectionProvenance);

        return new StoredAssessment(report, selection);
    }

    private sealed record AssessmentHeader(
        string SelectionName,
        IReadOnlyList<string> SelectionWords,
        string SelectionSha256,
        CorpusProvenance? SelectionProvenance,
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
            SELECT SelectionName, SelectionWordsJson, SelectionSha256, SelectionProvenanceJson,
                   OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline, DiagnosticCount
            FROM Assessments WHERE AssessmentId = $id;
            """;
        command.Parameters.AddWithValue("$id", assessmentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new AssessmentHeader(
            SelectionName: reader.GetString(0),
            SelectionWords: JsonSerializer.Deserialize<List<string>>(reader.GetString(1))!,
            SelectionSha256: reader.GetString(2),
            SelectionProvenance: reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<CorpusProvenance>(reader.GetString(3)),
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
