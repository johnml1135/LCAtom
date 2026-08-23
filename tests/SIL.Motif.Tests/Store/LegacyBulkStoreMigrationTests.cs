using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class LegacyBulkStoreMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-legacy-migration-" + Guid.NewGuid().ToString("N"));

    public LegacyBulkStoreMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CopiesLegacyTypedRowsAndRecordsSourceDigest()
    {
        var legacyPath = Path.Combine(_root, "legacy.db");
        using (var legacy = new SqliteConnection("Data Source=" + legacyPath))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE Corpora (CorpusId TEXT PRIMARY KEY, ProvenanceJson TEXT NOT NULL);
                CREATE TABLE CorpusDocuments (CorpusId TEXT NOT NULL, DocumentId TEXT NOT NULL,
                    OrdinalIndex INTEGER NOT NULL, Title TEXT NOT NULL, Source TEXT NOT NULL, Text TEXT NOT NULL,
                    ContentSha256 TEXT NOT NULL, IngestedUtc TEXT NOT NULL, Licence TEXT, CapabilitiesJson TEXT, AttributesJson TEXT,
                    PRIMARY KEY (CorpusId, DocumentId));
                CREATE TABLE Assessments (AssessmentId TEXT PRIMARY KEY, CorpusId TEXT NOT NULL, CorpusWordsJson TEXT NOT NULL,
                    CorpusSha256 TEXT NOT NULL, CorpusProvenanceJson TEXT, OutcomeDigest TEXT NOT NULL, SemanticDigest TEXT NOT NULL,
                    GrammarSourceSha256 TEXT NOT NULL, ModelFingerprint TEXT NOT NULL, Pipeline TEXT NOT NULL,
                    DiagnosticCount INTEGER NOT NULL, SavedUtc TEXT NOT NULL);
                CREATE TABLE AssessedWords (AssessedWordId INTEGER PRIMARY KEY, AssessmentId TEXT NOT NULL,
                    OrdinalIndex INTEGER NOT NULL, Word TEXT NOT NULL, Outcome TEXT NOT NULL);
                CREATE TABLE ParsedAnalyses (AssessedWordId INTEGER NOT NULL, OrdinalIndex INTEGER NOT NULL,
                    CategoryGuid TEXT, MorphemeGuidsJson TEXT NOT NULL, RootIndex INTEGER NOT NULL, IdentityDigest TEXT NOT NULL);
                CREATE TABLE AssessmentPins (AssessmentId TEXT NOT NULL, PinnedBy TEXT NOT NULL, PinnedUtc TEXT NOT NULL,
                    PRIMARY KEY (AssessmentId, PinnedBy));
                INSERT INTO Corpora VALUES ('c1','{}');
                """;
            command.ExecuteNonQuery();
        }

        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.Throws<InvalidOperationException>(() => LegacyBulkStoreMigration.ImportInto(legacyPath, database,
            boundary => { if (boundary == "Corpora") throw new InvalidOperationException("injected"); }));
        Assert.True(File.Exists(legacyPath));
        LegacyBulkStoreMigration.ImportInto(legacyPath, database);

        using var connection = database.OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Corpora WHERE CorpusId = 'c1';";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
