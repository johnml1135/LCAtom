using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>
/// Opens the single embedded database ADR 0036 decision 6 puts Corpora and Assessments in.
/// </summary>
/// <remarks>
/// One file for both, because decision 6 treats them as one half of the split ("bulk, queried in aggregate,
/// and pruned") against the other half (Proposals and Receipts, which stay files). <see cref="Corpus.SqliteCorpusStore"/>
/// and <see cref="SqliteAssessmentStore"/> each open their own short-lived connection per call rather than
/// holding one open for the store's lifetime, matching how <see cref="Corpus.FileCorpusStore"/> opens and closes a
/// file handle per call — a store here is a path, not a session.
/// </remarks>
internal static class SqliteMotifDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Corpora (
            CorpusId TEXT PRIMARY KEY,
            ProvenanceJson TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS CorpusDocuments (
            CorpusId TEXT NOT NULL REFERENCES Corpora(CorpusId),
            DocumentId TEXT NOT NULL,
            OrdinalIndex INTEGER NOT NULL,
            Title TEXT NOT NULL,
            Source TEXT NOT NULL,
            Text TEXT NOT NULL,
            ContentSha256 TEXT NOT NULL,
            IngestedUtc TEXT NOT NULL,
            Licence TEXT NULL,
            CapabilitiesJson TEXT NULL,
            AttributesJson TEXT NULL,
            PRIMARY KEY (CorpusId, DocumentId)
        );

        CREATE TABLE IF NOT EXISTS Assessments (
            AssessmentId TEXT PRIMARY KEY,
            CorpusId TEXT NOT NULL,
            CorpusWordsJson TEXT NOT NULL,
            CorpusSha256 TEXT NOT NULL,
            CorpusProvenanceJson TEXT NULL,
            OutcomeDigest TEXT NOT NULL,
            SemanticDigest TEXT NOT NULL,
            GrammarSourceSha256 TEXT NOT NULL,
            ModelFingerprint TEXT NOT NULL,
            Pipeline TEXT NOT NULL,
            DiagnosticCount INTEGER NOT NULL,
            SavedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AssessedWords (
            AssessedWordId INTEGER PRIMARY KEY AUTOINCREMENT,
            AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
            OrdinalIndex INTEGER NOT NULL,
            Word TEXT NOT NULL,
            Outcome TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_AssessedWords_Assessment ON AssessedWords(AssessmentId);
        CREATE INDEX IF NOT EXISTS IX_AssessedWords_Word ON AssessedWords(AssessmentId, Word);

        CREATE TABLE IF NOT EXISTS ParsedAnalyses (
            AssessedWordId INTEGER NOT NULL REFERENCES AssessedWords(AssessedWordId),
            OrdinalIndex INTEGER NOT NULL,
            CategoryGuid TEXT NULL,
            MorphemeGuidsJson TEXT NOT NULL,
            RootIndex INTEGER NOT NULL,
            IdentityDigest TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ParsedAnalyses_Word ON ParsedAnalyses(AssessedWordId);

        CREATE TABLE IF NOT EXISTS AssessmentPins (
            AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
            PinnedBy TEXT NOT NULL,
            PinnedUtc TEXT NOT NULL,
            PRIMARY KEY (AssessmentId, PinnedBy)
        );
        """;

    /// <summary>Opens a connection to <paramref name="databasePath"/>, creating the schema if it is new.</summary>
    public static SqliteConnection OpenConnection(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Pooling off: a pooled handle would outlive Dispose and keep the file locked (see remarks).
        var connection = new SqliteConnection($"Data Source={fullPath};Pooling=False");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        return connection;
    }
}
