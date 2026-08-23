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
    public void RejectsSelfImportBeforeAttachingOrMovingDestination()
    {
        var path = Path.Combine(_root, "self.motif.db");
        var project = new ProjectLocator(Path.Combine(_root, "self.fwdata"), "self");
        using var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));

        Assert.Throws<InvalidOperationException>(() => LegacyBulkStoreMigration.ImportInto(path, database));
        Assert.True(File.Exists(path));
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM MotifMetadata;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void RetriesArchiveAfterCommitFailureUsingLedger()
    {
        var root = Path.Combine(_root, "archive-retry");
        var path = CreateLegacySource(root);
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));

        Assert.Throws<IOException>(() => LegacyBulkStoreMigration.ImportInto(path, database,
            renameSourceAfterCommit: true, beforeArchive: () => throw new IOException("archive seam")));
        Assert.True(File.Exists(path));
        var retry = LegacyBulkStoreMigration.ImportInto(path, database);
        Assert.True(retry.SourceRenamed);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".migrated"));
    }

    [Fact]
    public void CopiesLegacyTypedRowsAndRecordsSourceDigest()
    {
        var legacyPath = Path.Combine(_root, "legacy.db");
        using (var legacy = new SqliteConnection("Data Source=" + legacyPath + ";Pooling=False"))
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
                INSERT INTO Corpora VALUES ('c1','{"source":"legacy"}');
                INSERT INTO CorpusDocuments VALUES ('c1','d1',0,'Title','source','Tēxt','sha-doc','2026-08-22T12:00:00Z',NULL,'{"cap":true}','{"tag":"x"}');
                INSERT INTO Assessments VALUES ('a1','c1','["word"]','sha-corpus','{"p":1}','sha-outcome','sha-semantic','sha-grammar','model','pipeline',2,'2026-08-22T12:01:00Z');
                INSERT INTO AssessedWords VALUES (1,'a1',0,'word','ok');
                INSERT INTO ParsedAnalyses VALUES (1,0,NULL,'["m1"]',0,'sha-analysis');
                INSERT INTO AssessmentPins VALUES ('a1','tester','2026-08-22T12:02:00Z');
                """;
            command.ExecuteNonQuery();
        }

        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.Throws<InvalidOperationException>(() => LegacyBulkStoreMigration.ImportInto(legacyPath, database,
            boundary => { if (boundary == "Corpora") throw new InvalidOperationException("injected"); }));
        Assert.True(File.Exists(legacyPath));
        File.WriteAllText(legacyPath + ".migrated", "prior archive");
        LegacyBulkStoreMigration.ImportInto(legacyPath, database);
        Assert.Equal("prior archive", File.ReadAllText(legacyPath + ".migrated"));
        Assert.True(File.Exists(legacyPath + ".migrated-1"));

        using var connection = database.OpenConnection();
        Assert.Equal("{\"source\":\"legacy\"}", Scalar(connection, "SELECT ProvenanceJson FROM Corpora WHERE CorpusId = 'c1';"));
        Assert.Equal("Tēxt", Scalar(connection, "SELECT Text FROM CorpusDocuments WHERE CorpusId = 'c1' AND DocumentId = 'd1';"));
        Assert.Equal("[\"word\"]", Scalar(connection, "SELECT CorpusWordsJson FROM Assessments WHERE AssessmentId = 'a1';"));
        Assert.Equal("word", Scalar(connection, "SELECT Word FROM AssessedWords WHERE AssessedWordId = 1;"));
        Assert.Equal("[\"m1\"]", Scalar(connection, "SELECT MorphemeGuidsJson FROM ParsedAnalyses WHERE AssessedWordId = 1 AND OrdinalIndex = 0;"));
        Assert.Equal("tester", Scalar(connection, "SELECT PinnedBy FROM AssessmentPins WHERE AssessmentId = 'a1';"));
    }

    [Theory]
    [InlineData("Corpora")]
    [InlineData("CorpusDocuments")]
    [InlineData("Assessments")]
    [InlineData("AssessedWords")]
    [InlineData("ParsedAnalyses")]
    [InlineData("AssessmentPins")]
    [InlineData("MigrationLedger")]
    public void RollsBackEveryBulkImportBoundary(string boundary)
    {
        var root = Path.Combine(_root, "boundary-" + boundary);
        var legacyPath = CreateLegacySource(root);
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var observedPartial = false;
        Assert.Throws<InvalidOperationException>(() => LegacyBulkStoreMigration.ImportInto(legacyPath, database, reached =>
        {
            if (reached != boundary) return;
            using var client = database.OpenConnection();
            observedPartial = Convert.ToInt64(Scalar(client, "SELECT COUNT(*) FROM Corpora;")) != 0;
            throw new InvalidOperationException("injected");
        }, renameSourceAfterCommit: false));
        Assert.False(observedPartial);
        Assert.True(File.Exists(legacyPath));
        using (var connection = database.OpenConnection())
        {
            foreach (var table in new[] { "Corpora", "CorpusDocuments", "Assessments", "AssessedWords", "ParsedAnalyses", "AssessmentPins", "MigrationLedger" })
                Assert.Equal(0L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM " + table + ";")));
        }
        var retry = LegacyBulkStoreMigration.ImportInto(legacyPath, database, renameSourceAfterCommit: false);
        Assert.Equal(6, retry.RowCount);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void AttachesLegacyDatabaseReadOnly()
    {
        var legacyPath = CreateLegacySource(Path.Combine(_root, "read-only"));
        var project = new ProjectLocator(Path.Combine(_root, "read-only.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "read-only.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        using var connection = database.OpenConnection();
        var uri = new Uri(Path.GetFullPath(legacyPath)).AbsoluteUri + "?mode=ro";
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE '" + uri.Replace("'", "''", StringComparison.Ordinal) + "' AS legacy;";
            attach.ExecuteNonQuery();
        }
        foreach (var sql in new[] { "INSERT INTO legacy.Corpora VALUES ('c2','{}');", "UPDATE legacy.Corpora SET ProvenanceJson = '{}' WHERE CorpusId = 'c1';", "CREATE TABLE legacy.NewTable (Id INTEGER);" })
        {
            using var write = connection.CreateCommand();
            write.CommandText = sql;
            Assert.Throws<SqliteException>(() => write.ExecuteNonQuery());
        }
        using var detach = connection.CreateCommand();
        detach.CommandText = "DETACH DATABASE legacy;";
        detach.ExecuteNonQuery();
    }

    [Fact]
    public void IncludesCommittedRowsStillInLegacyWal()
    {
        var root = Path.Combine(_root, "wal");
        var legacyPath = CreateLegacySource(root);
        using var writer = new SqliteConnection("Data Source=" + legacyPath + ";Pooling=False");
        writer.Open();
        using (var mode = writer.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode = WAL;";
            mode.ExecuteScalar();
        }
        using (var insert = writer.CreateCommand())
        {
            insert.CommandText = "INSERT INTO Corpora VALUES ('c2','{\"wal\":true}');";
            insert.ExecuteNonQuery();
        }
        Assert.True(File.Exists(legacyPath + "-wal"));
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var result = LegacyBulkStoreMigration.ImportInto(legacyPath, database, renameSourceAfterCommit: false);
        Assert.NotEmpty(result.SourceDigest);
        using var connection = database.OpenConnection();
        Assert.Equal("{\"wal\":true}", Scalar(connection, "SELECT ProvenanceJson FROM Corpora WHERE CorpusId = 'c2';"));
    }

    [Fact]
    public void RejectsMissingExpectedLegacySchemaAndConflictingDestinationRow()
    {
        var missingPath = Path.Combine(_root, "missing.db");
        using (var source = new SqliteConnection("Data Source=" + missingPath + ";Pooling=False"))
        {
            source.Open();
            using var command = source.CreateCommand();
            command.CommandText = "CREATE TABLE Corpora (CorpusId TEXT PRIMARY KEY, ProvenanceJson TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }
        var project = new ProjectLocator(Path.Combine(_root, "missing.fwdata"), "project");
        using var missingDatabase = MotifDatabase.OpenOwned(Path.Combine(_root, "missing.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.Throws<InvalidDataException>(() => LegacyBulkStoreMigration.ImportInto(missingPath, missingDatabase, renameSourceAfterCommit: false));

        var malformedPath = CreateLegacySource(Path.Combine(_root, "malformed"));
        using (var malformed = new SqliteConnection("Data Source=" + malformedPath + ";Pooling=False"))
        {
            malformed.Open();
            using var command = malformed.CreateCommand();
            command.CommandText = "ALTER TABLE Corpora RENAME COLUMN ProvenanceJson TO WrongColumn;";
            command.ExecuteNonQuery();
        }
        var malformedProject = new ProjectLocator(Path.Combine(_root, "malformed.fwdata"), "project");
        using var malformedDatabase = MotifDatabase.OpenOwned(Path.Combine(_root, "malformed.motif.db"), malformedProject, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.Throws<InvalidDataException>(() => LegacyBulkStoreMigration.ImportInto(malformedPath, malformedDatabase, renameSourceAfterCommit: false));

        var collisionPath = CreateLegacySource(Path.Combine(_root, "collision"));
        var collisionProject = new ProjectLocator(Path.Combine(_root, "collision.fwdata"), "project");
        using var collisionDatabase = MotifDatabase.OpenOwned(Path.Combine(_root, "collision.motif.db"), collisionProject, MotifSchema.CurrentSchema, new Version(1, 0));
        using (var connection = collisionDatabase.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO Corpora (CorpusId, ProvenanceJson) VALUES ('c1','{\"different\":true}');";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => LegacyBulkStoreMigration.ImportInto(collisionPath, collisionDatabase, renameSourceAfterCommit: false));
        using var verify = collisionDatabase.OpenConnection();
        Assert.Equal("{\"different\":true}", Scalar(verify, "SELECT ProvenanceJson FROM Corpora WHERE CorpusId = 'c1';"));
        Assert.Equal(0L, Convert.ToInt64(Scalar(verify, "SELECT COUNT(*) FROM MigrationLedger;")));
    }

    private string CreateLegacySource(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.db");
        using var legacy = new SqliteConnection("Data Source=" + path + ";Pooling=False");
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
            INSERT INTO Corpora VALUES ('c1','{"source":"legacy"}');
            INSERT INTO CorpusDocuments VALUES ('c1','d1',0,'Title','source','Tēxt','sha-doc','2026-08-22T12:00:00Z',NULL,'{"cap":true}','{"tag":"x"}');
            INSERT INTO Assessments VALUES ('a1','c1','["word"]','sha-corpus','{"p":1}','sha-outcome','sha-semantic','sha-grammar','model','pipeline',2,'2026-08-22T12:01:00Z');
            INSERT INTO AssessedWords VALUES (1,'a1',0,'word','ok');
            INSERT INTO ParsedAnalyses VALUES (1,0,NULL,'["m1"]',0,'sha-analysis');
            INSERT INTO AssessmentPins VALUES ('a1','tester','2026-08-22T12:02:00Z');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
