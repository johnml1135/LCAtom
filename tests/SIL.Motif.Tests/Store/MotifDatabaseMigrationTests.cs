using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class MotifDatabaseMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-database-" + Guid.NewGuid().ToString("N"));

    public MotifDatabaseMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void NewDatabaseRegistersProjectAndConfiguresSQLite()
    {
        using var database = Open("new.fwdata", supportedSchema: MotifSchema.CurrentSchema);
        using var connection = database.OpenConnection();

        Assert.Equal(MotifSchema.ApplicationId, PragmaInt(connection, "application_id"));
        Assert.Equal(MotifSchema.CurrentSchema, PragmaInt(connection, "user_version"));
        Assert.Equal(1, PragmaInt(connection, "foreign_keys"));
        Assert.Equal("wal", PragmaText(connection, "journal_mode"));
        Assert.Equal(MotifSchema.BusyTimeoutMilliseconds, PragmaInt(connection, "busy_timeout"));
        Assert.Equal("new", Scalar(connection, "SELECT FieldWorksProjectIdentity FROM MotifMetadata WHERE Id = 1;"));
        Assert.Equal(Path.Combine(_root, "new.fwdata"), Scalar(connection, "SELECT FullFwDataPath FROM MotifMetadata WHERE Id = 1;"));
    }

    [Fact]
    public void CatalogUsesTheSiblingMotifDatabasePath()
    {
        var project = Locator("catalog.fwdata");
        Assert.Equal(DatabasePath("catalog.fwdata"), ProjectDatabaseCatalog.DatabasePathFor(project));

        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var database = catalog.Open(project);
        Assert.True(File.Exists(DatabasePath("catalog.fwdata")));
    }

    [Fact]
    public void NewDatabaseRequiresTheTargetSchemaMinimumWorkerVersion()
    {
        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(
            DatabasePath("too-old.fwdata"), Locator("too-old.fwdata"), 1, new Version(0, 9)));
        Assert.False(File.Exists(DatabasePath("too-old.fwdata")));
    }

    [Fact]
    public void DatabasePathMayContainSemicolons()
    {
        var fileName = "semi;colon.fwdata";
        using var database = Open(fileName, MotifSchema.CurrentSchema);
        using var connection = database.OpenConnection();
        Assert.Equal(MotifSchema.ApplicationId, PragmaInt(connection, "application_id"));
    }

    [Fact]
    public void ConfigurationFailureDisposesTheOpenedHandle()
    {
        var path = DatabasePath("configuration-failure.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("configuration-failure.fwdata"), 1, new Version(1, 0))) { }

        Assert.Throws<InvalidOperationException>(() => MotifDatabase.OpenConfiguredConnectionForTesting(
            path, connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "BEGIN EXCLUSIVE;";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("injected configuration failure");
            }));
        using var reopened = MotifDatabase.OpenOwned(
            path, Locator("configuration-failure.fwdata"), 1, new Version(1, 0));
    }

    [Fact]
    public void DisposingOwnerClosesReturnedConnectionsBeforeReleasingOwnership()
    {
        var path = DatabasePath("drain.fwdata");
        var owner = MotifDatabase.OpenOwned(path, Locator("drain.fwdata"), 1, new Version(1, 0));
        var connection = owner.OpenConnection();
        Assert.Equal(1, owner.TrackedConnectionCount);

        owner.Dispose();

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Throws<ObjectDisposedException>(() => connection.Open());
        using var reopened = MotifDatabase.OpenOwned(path, Locator("drain.fwdata"), 1, new Version(1, 0));
    }

    [Fact]
    public void ClosedConnectionRemainsOwnedUntilItIsDisposed()
    {
        var path = DatabasePath("closed.fwdata");
        var owner = MotifDatabase.OpenOwned(path, Locator("closed.fwdata"), 1, new Version(1, 0));
        var connection = owner.OpenConnection();
        connection.Close();

        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => connection.Open());
    }

    [Fact]
    public async Task ConcurrentDisposeAndOpenDoesNotReturnPostReleaseConnection()
    {
        var path = DatabasePath("race.fwdata");
        using var owner = MotifDatabase.OpenOwned(path, Locator("race.fwdata"), 1, new Version(1, 0));
        var returned = new List<SqliteConnection>();
        var opening = Task.Run(() =>
        {
            try { returned.Add(owner.OpenConnection()); }
            catch (ObjectDisposedException) { }
        });

        owner.Dispose();
        await opening;

        Assert.All(returned, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        using var reopened = MotifDatabase.OpenOwned(path, Locator("race.fwdata"), 1, new Version(1, 0));
    }

    [Fact]
    public void ClosedConnectionsAreRemovedFromOwnershipTracking()
    {
        var path = DatabasePath("cycles.fwdata");
        using var owner = MotifDatabase.OpenOwned(path, Locator("cycles.fwdata"), 1, new Version(1, 0));

        for (var i = 0; i < 100; i++)
        {
            using var connection = owner.OpenConnection();
        }

        Assert.Equal(0, owner.TrackedConnectionCount);
    }

    [Fact]
    public void UpgradeIsTransactionalAndKeepsExistingRows()
    {
        using (var initial = Open("upgrade.fwdata", supportedSchema: 1))
        {
            using var connection = initial.OpenConnection();
            Assert.Equal(1, PragmaInt(connection, "user_version"));
        }

        using var upgraded = Open("upgrade.fwdata", supportedSchema: MotifSchema.CurrentSchema);
        using var upgradedConnection = upgraded.OpenConnection();
        Assert.Equal(MotifSchema.CurrentSchema, PragmaInt(upgradedConnection, "user_version"));
        Assert.NotNull(Scalar(upgradedConnection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Assessments';"));
        Assert.NotNull(Scalar(upgradedConnection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'IX_AssessedWords_Assessment';"));
        Assert.NotNull(Scalar(upgradedConnection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'IX_AssessedWords_Word';"));
        Assert.NotNull(Scalar(upgradedConnection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'IX_ParsedAnalyses_Word';"));
    }

    [Theory]
    [InlineData("MotifMetadata")]
    [InlineData("Corpora")]
    [InlineData("CorpusDocuments")]
    [InlineData("Assessments")]
    [InlineData("AssessedWords")]
    [InlineData("ParsedAnalyses")]
    [InlineData("AssessmentPins")]
    [InlineData("IX_AssessedWords_Assessment")]
    [InlineData("IX_AssessedWords_Word")]
    [InlineData("IX_ParsedAnalyses_Word")]
    public void CurrentSchemaRejectsMissingRequiredObjects(string objectName)
    {
        var path = DatabasePath("missing-" + objectName + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-" + objectName + ".fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, objectName.StartsWith("IX_", StringComparison.Ordinal)
                ? "DROP INDEX " + objectName + ";"
                : "DROP TABLE " + objectName + ";");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-" + objectName + ".fwdata"), 2, new Version(1, 0)));
        Assert.Equal(2, PragmaFromPath(path, "user_version"));
    }

    [Theory]
    [InlineData("Proposals")]
    [InlineData("ProposalRevisions")]
    [InlineData("Drafts")]
    [InlineData("Decisions")]
    [InlineData("Receipts")]
    [InlineData("Reports")]
    [InlineData("AppliedIndex")]
    [InlineData("MigrationLedger")]
    public void Generation3RejectsMissingWorkflowObjects(string objectName)
    {
        var path = DatabasePath("missing-v3-" + objectName + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-v3-" + objectName + ".fwdata"), 3,
                   new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "DROP TABLE " + objectName + ";");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-v3-" + objectName + ".fwdata"), 3, new Version(1, 0)));
    }

    [Fact]
    public void Generation3RejectsUnexpectedWorkflowObjectsAndAnchorShape()
    {
        var path = DatabasePath("malformed-v3-workflow.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-v3-workflow.fwdata"), 3, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "ALTER TABLE Proposals RENAME COLUMN AnchorJson TO WrongAnchor; CREATE TABLE UnexpectedWorkflow (Id TEXT);");
        }
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-v3-workflow.fwdata"), 3, new Version(1, 0)));

        var fkPath = DatabasePath("malformed-v3-fk.fwdata");
        using (MotifDatabase.OpenOwned(fkPath, Locator("malformed-v3-fk.fwdata"), 3, new Version(1, 0))) { }
        using (var connection = NewConnection(fkPath))
        {
            Execute(connection, "DROP TABLE Decisions; CREATE TABLE Decisions (ProposalId TEXT NOT NULL REFERENCES Reports(ReportId), " +
                "IntentDigest TEXT NOT NULL, Outcome TEXT NOT NULL, ActorType TEXT NOT NULL, ActorId TEXT NOT NULL, " +
                "Comment TEXT NULL, TimestampUtc TEXT NOT NULL, PRIMARY KEY (ProposalId, IntentDigest));");
        }
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            fkPath, Locator("malformed-v3-fk.fwdata"), 3, new Version(1, 0)));
    }

    [Fact]
    public void Generation3PinsEveryWorkflowTableColumnAndForeignKeyInventory()
    {
        var path = DatabasePath("workflow-v3-inventory.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("workflow-v3-inventory.fwdata"), 3, new Version(1, 0))) { }
        using var connection = NewConnection(path);
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Proposals"] = ["ProposalId|TEXT|0|1|", "CurrentIntentDigest|TEXT|1|0|", "Status|TEXT|1|0|", "Label|TEXT|0|0|", "Comment|TEXT|0|0|", "SupersededBy|TEXT|0|0|", "AnchorJson|TEXT|0|0|"],
            ["ProposalRevisions"] = ["ProposalId|TEXT|1|1|", "IntentDigest|TEXT|1|2|", "ProposalJson|BLOB|1|0|", "CreatedUtc|TEXT|1|0|"],
            ["Drafts"] = ["DraftName|TEXT|0|1|", "ProposalId|TEXT|1|0|", "DraftJson|TEXT|1|0|"],
            ["Decisions"] = ["ProposalId|TEXT|1|1|", "IntentDigest|TEXT|1|2|", "Outcome|TEXT|1|0|", "ActorType|TEXT|1|0|", "ActorId|TEXT|1|0|", "Comment|TEXT|0|0|", "TimestampUtc|TEXT|1|0|"],
            ["Receipts"] = ["ReceiptId|TEXT|0|1|", "ProposalId|TEXT|1|0|", "IntentDigest|TEXT|1|0|", "ReceiptJson|TEXT|1|0|", "RecordedUtc|TEXT|1|0|"],
            ["Reports"] = ["ReportId|TEXT|0|1|", "ProposalId|TEXT|0|0|", "AssessmentId|TEXT|0|0|", "ReportJson|TEXT|1|0|", "EvidenceJson|TEXT|0|0|", "CreatedUtc|TEXT|1|0|"],
            ["AppliedIndex"] = ["ProposalId|TEXT|0|1|", "IntentDigest|TEXT|1|0|", "AppliedUtc|TEXT|1|0|", "RecordJson|TEXT|0|0|"],
            ["MigrationLedger"] = ["SourceKind|TEXT|1|1|", "SourcePath|TEXT|1|2|", "SourceDigest|TEXT|1|3|", "ImportedUtc|TEXT|1|0|"]
        };
        foreach (var pair in expected)
        {
            using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT name, type, \"notnull\", pk, COALESCE(dflt_value, '') FROM pragma_table_info($table) ORDER BY cid;";
            columns.Parameters.AddWithValue("$table", pair.Key);
            var actual = new List<string>();
            using var reader = columns.ExecuteReader();
            while (reader.Read()) actual.Add(string.Join("|", reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
            Assert.Equal(pair.Value, actual);
        }

        var foreignKeys = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ProposalRevisions"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Decisions"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Receipts"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Reports"] = ["Assessments|AssessmentId|AssessmentId|NO ACTION|NO ACTION|NONE", "Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["AppliedIndex"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Drafts"] = []
        };
        foreach (var pair in foreignKeys)
        {
            using var fks = connection.CreateCommand();
            fks.CommandText = "SELECT \"table\", \"from\", \"to\", on_update, on_delete, \"match\" FROM pragma_foreign_key_list($table) ORDER BY id, seq;";
            fks.Parameters.AddWithValue("$table", pair.Key);
            var actual = new List<string>();
            using var reader = fks.ExecuteReader();
            while (reader.Read()) actual.Add(string.Join("|", reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            Assert.Equal(pair.Value, actual);
        }
    }

    [Fact]
    public void CurrentSchemaRejectsUnexpectedUserObjects()
    {
        var path = DatabasePath("unexpected-object.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("unexpected-object.fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "CREATE TABLE UnexpectedUserTable (Value TEXT NOT NULL);");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("unexpected-object.fwdata"), 2, new Version(1, 0)));
        Assert.Equal(2, PragmaFromPath(path, "user_version"));
    }

    [Fact]
    public void CurrentSchemaRejectsMalformedForeignKeyShape()
    {
        var path = DatabasePath("malformed-fk.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-fk.fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE CorpusDocuments; CREATE TABLE CorpusDocuments " +
                "(CorpusId TEXT NOT NULL, DocumentId TEXT NOT NULL, OrdinalIndex INTEGER NOT NULL, " +
                "Title TEXT NOT NULL, Source TEXT NOT NULL, Text TEXT NOT NULL, ContentSha256 TEXT NOT NULL, " +
                "IngestedUtc TEXT NOT NULL, Licence TEXT NULL, CapabilitiesJson TEXT NULL, AttributesJson TEXT NULL, " +
                "PRIMARY KEY (CorpusId, DocumentId));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-fk.fwdata"), 2, new Version(1, 0)));
    }

    [Theory]
    [InlineData("type", "AssessmentId INTEGER NOT NULL")]
    [InlineData("nullability", "AssessmentId TEXT NULL")]
    [InlineData("primary-key", "AssessmentId TEXT NOT NULL")]
    public void CurrentSchemaRejectsMalformedColumnShape(string mutation, string assessmentIdColumn)
    {
        var path = DatabasePath("malformed-column-" + mutation + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-column-" + mutation + ".fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessmentPins; CREATE TABLE AssessmentPins (" + assessmentIdColumn + ", " +
                "PinnedBy TEXT NOT NULL, PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-column-" + mutation + ".fwdata"), 2, new Version(1, 0)));
    }

    [Theory]
    [InlineData("partial", "CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(AssessmentId) WHERE Word IS NOT NULL;")]
    [InlineData("columns", "CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(Word);")]
    public void CurrentSchemaRejectsMalformedNamedIndex(string mutation, string createIndex)
    {
        var path = DatabasePath("malformed-index-" + mutation + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-index-" + mutation + ".fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "DROP INDEX IX_AssessedWords_Assessment; " + createIndex);

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-index-" + mutation + ".fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRejectsForeignKeyActionChange()
    {
        var path = DatabasePath("malformed-fk-action.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-fk-action.fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessmentPins; CREATE TABLE AssessmentPins (" +
                "AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId) ON DELETE CASCADE, " +
                "PinnedBy TEXT NOT NULL, PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-fk-action.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRequiresMetadataCheckConstraint()
    {
        var path = DatabasePath("missing-metadata-check.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-metadata-check.fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE MotifMetadata; CREATE TABLE MotifMetadata (" +
                "Id INTEGER PRIMARY KEY, FullFwDataPath TEXT NOT NULL, FieldWorksProjectIdentity TEXT NOT NULL, " +
                "MinimumWorkerVersion TEXT NOT NULL, CreatedUtc TEXT NOT NULL);");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-metadata-check.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRequiresAssessedWordsAutoincrement()
    {
        var path = DatabasePath("missing-autoincrement.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-autoincrement.fwdata"), 2, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessedWords; CREATE TABLE AssessedWords (" +
                "AssessedWordId INTEGER PRIMARY KEY, AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId), " +
                "OrdinalIndex INTEGER NOT NULL, Word TEXT NOT NULL, Outcome TEXT NOT NULL);");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-autoincrement.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void CorruptDatabaseBytesAreReportedAsInvalidData()
    {
        var path = DatabasePath("corrupt-bytes.fwdata");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x4F, 0x54, 0x49, 0x46, 0x00, 0x01 });

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("corrupt-bytes.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void AvailabilityErrorsAreNotClassifiedAsCorruption()
    {
        Assert.True(MotifSchema.IsCorruptionCode(26));
        Assert.True(MotifSchema.IsCorruptionCode(11));
        Assert.False(MotifSchema.IsCorruptionCode(1));
        Assert.False(MotifSchema.IsCorruptionCode(17));
        Assert.False(MotifSchema.IsCorruptionCode(24));
        Assert.False(MotifSchema.IsCorruptionCode(5));
        Assert.False(MotifSchema.IsCorruptionCode(6));
        Assert.False(MotifSchema.IsCorruptionCode(8));
        Assert.False(MotifSchema.IsCorruptionCode(10));
        Assert.False(MotifSchema.IsCorruptionCode(13));
    }

    [Fact]
    public void WrongApplicationIdIsRejectedBeforeWrites()
    {
        var path = DatabasePath("wrong.fwdata");
        using (var connection = NewConnection(path))
        {
            Execute(connection, "PRAGMA application_id = 12345; PRAGMA user_version = 1;");
        }

        var before = PragmaFromPath(path, "user_version");
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(path, Locator("wrong.fwdata"), 2, new Version(1, 0)));
        Assert.Equal(before, PragmaFromPath(path, "user_version"));
    }

    [Fact]
    public void NewerSchemaIsRefusedWithoutDowngrade()
    {
        var path = DatabasePath("newer.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, $"PRAGMA application_id = {MotifSchema.ApplicationId}; PRAGMA user_version = 99;");

        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(path, Locator("newer.fwdata"), 2, new Version(1, 0)));
        using var check = NewConnection(path);
        Assert.Equal(99, PragmaInt(check, "user_version"));
    }

    [Fact]
    public void MissingMetadataIsReportedAsCorruptionWithoutWrites()
    {
        var path = DatabasePath("missing-metadata.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, $"PRAGMA application_id = {MotifSchema.ApplicationId}; PRAGMA user_version = 1;");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-metadata.fwdata"), 2, new Version(1, 0)));
        Assert.Equal(1, PragmaFromPath(path, "user_version"));
        Assert.Equal(MotifSchema.ApplicationId, PragmaFromPath(path, "application_id"));
    }

    [Fact]
    public void InvalidMetadataVersionIsReportedAsCorruption()
    {
        var path = DatabasePath("invalid-metadata.fwdata");
        using (var connection = NewConnection(path))
        {
            Execute(connection, $"PRAGMA application_id = {MotifSchema.ApplicationId}; PRAGMA user_version = 1; " +
                "CREATE TABLE MotifMetadata (Id INTEGER PRIMARY KEY, FullFwDataPath TEXT, " +
                "FieldWorksProjectIdentity TEXT, MinimumWorkerVersion TEXT, CreatedUtc TEXT); " +
                "INSERT INTO MotifMetadata VALUES (1, 'project.fwdata', 'project', 'not-a-version', 'now');");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("invalid-metadata.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public void WorkerOlderThanDatabaseMinimumIsRefused()
    {
        var path = DatabasePath("minimum.fwdata");
        using (var created = MotifDatabase.OpenOwned(path, Locator("minimum.fwdata"), 1, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "UPDATE MotifMetadata SET MinimumWorkerVersion = '2.0' WHERE Id = 1;");

        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(
            path, Locator("minimum.fwdata"), 1, new Version(1, 9)));
    }

    [Fact]
    public void NewerWorkerOpeningSameSchemaDoesNotRaiseMinimumForOlderWorker()
    {
        var path = DatabasePath("same-schema.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), 1, new Version(1, 0))) { }

        using (MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), 1, new Version(9, 0))) { }
        using var older = MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), 1, new Version(1, 0));
        using var connection = older.OpenConnection();

        Assert.Equal("1.0", Scalar(connection, "SELECT MinimumWorkerVersion FROM MotifMetadata WHERE Id = 1;"));
    }

    [Fact]
    public void LocatorMismatchIsDetectedBeforeUpgradeWrites()
    {
        var path = DatabasePath("mismatch.fwdata");
        using (var initial = MotifDatabase.OpenOwned(path, Locator("mismatch.fwdata"), 1, new Version(1, 0))) { }
        var before = PragmaFromPath(path, "user_version");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, new ProjectLocator(Path.Combine(_root, "other.fwdata"), "new"), 2, new Version(1, 0)));
        Assert.Equal(before, PragmaFromPath(path, "user_version"));

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, new ProjectLocator(Path.Combine(_root, "mismatch.fwdata"), "different"), 2, new Version(1, 0)));
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaVersionAndTables()
    {
        var path = DatabasePath("failed.fwdata");
        using (var initial = MotifDatabase.OpenOwned(path, Locator("failed.fwdata"), 1, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "CREATE TABLE Corpora (CorpusId TEXT NOT NULL); ");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(path, Locator("failed.fwdata"), 2, new Version(1, 0)));
        using var check = NewConnection(path);
        Assert.Equal(1, PragmaInt(check, "user_version"));
        Assert.Null(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Assessments';"));
    }

    [Fact]
    public void InjectedPostDdlFailureRollsBackAndCleanRetrySucceeds()
    {
        var path = DatabasePath("injected-failure.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("injected-failure.fwdata"), 1, new Version(1, 0))) { }

        Assert.Throws<InvalidOperationException>(() => MotifDatabase.OpenOwnedForTesting(
            path, Locator("injected-failure.fwdata"), 2, new Version(1, 0),
            schema =>
            {
                if (schema == 2) throw new InvalidOperationException("injected migration failure");
            }));

        using (var check = NewConnection(path))
        {
            Assert.Equal(1, PragmaInt(check, "user_version"));
            Assert.NotNull(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'MotifMetadata';"));
            Assert.Null(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Assessments';"));
        }

        using var retry = MotifDatabase.OpenOwned(path, Locator("injected-failure.fwdata"), 2, new Version(1, 0));
        Assert.Equal(2, PragmaInt(retry.OpenConnection(), "user_version"));
    }

    [Fact]
    public void GenerationFiveRowsReceiveTypedArchiveFactsAndAppliedIndexLosesProposalForeignKey()
    {
        var path = DatabasePath("recovery-v5.fwdata");
        using (Open("recovery-v5.fwdata", supportedSchema: 5)) { }
        var proposalId = "proposal-recovery";
        using (var connection = NewConnection(path))
        {
            Execute(connection, "INSERT INTO Proposals (ProposalId, CurrentIntentDigest, Status) " +
                "VALUES ('proposal-recovery', 'digest', 'applied');");
            Execute(connection, "INSERT INTO ProposalRevisions (ProposalId, IntentDigest, ProposalJson, CreatedUtc) " +
                "VALUES ('proposal-recovery', 'digest', X'7B7D', '2026-08-01T00:00:00Z');");
            Execute(connection, "INSERT INTO AppliedIndex (ProposalId, IntentDigest, AppliedUtc) " +
                "VALUES ('proposal-recovery', 'digest', '2026-08-01T00:00:00Z');");
            foreach (var row in new[]
            {
                (Status: "cancelled", Cancellation: 1, Id: "cancelled-job"),
                (Status: "interrupted", Cancellation: 1, Id: "cancelled-interrupted"),
                (Status: "interrupted", Cancellation: 0, Id: "interrupted-job"),
                (Status: "failed", Cancellation: 0, Id: "failed-job"),
                (Status: "queued", Cancellation: 0, Id: "queued-job")
            })
                Execute(connection, $"INSERT INTO Jobs (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, " +
                    $"CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished) VALUES " +
                    $"('{row.Id}', 'project', 'dry-run', '{row.Status}', 1, '{row.Id}', '{{}}', {row.Cancellation}, " +
                    "'2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z', 0, 0);");
        }

        using (var upgraded = Open("recovery-v5.fwdata", supportedSchema: MotifSchema.CurrentSchema))
        using (var check = upgraded.OpenConnection())
        {
            MotifSchema.ValidateSchema(check, 6);
            Assert.Equal("cancellation", Scalar(check, "SELECT FailureCategory FROM Jobs WHERE JobId = 'cancelled-job';"));
            Assert.Equal("cancellation", Scalar(check, "SELECT FailureCategory FROM Jobs WHERE JobId = 'cancelled-interrupted';"));
            Assert.Equal("infrastructure", Scalar(check, "SELECT FailureCategory FROM Jobs WHERE JobId = 'interrupted-job';"));
            Assert.Equal("unknown", Scalar(check, "SELECT FailureCategory FROM Jobs WHERE JobId = 'failed-job';"));
            Assert.Equal("none", Scalar(check, "SELECT FailureCategory FROM Jobs WHERE JobId = 'queued-job';"));
            Assert.All(new[] { "cancelled-job", "cancelled-interrupted", "interrupted-job", "failed-job" }, job =>
                Assert.Equal("2026-08-01T00:00:00Z", Scalar(check, "SELECT ArchivedUtc FROM Jobs WHERE JobId = '" + job + "';")));
            Assert.Same(DBNull.Value, Scalar(check, "SELECT ArchivedUtc FROM Jobs WHERE JobId = 'queued-job';"));
            Assert.NotSame(DBNull.Value, Scalar(check, "SELECT ArchivedUtc FROM Proposals WHERE ProposalId = '" + proposalId + "';"));
            Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM AppliedIndex WHERE ProposalId = '" + proposalId + "';"));
            Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM pragma_foreign_key_list('AppliedIndex');"));
            Assert.Equal(6, PragmaInt(check, "user_version"));
        }
        using var reopened = Open("recovery-v5.fwdata", supportedSchema: MotifSchema.CurrentSchema);
        using var reopenedCheck = reopened.OpenConnection();
        Assert.Equal(5L, Scalar(reopenedCheck, "SELECT COUNT(*) FROM Jobs;"));
        Assert.Equal(1L, Scalar(reopenedCheck, "SELECT COUNT(*) FROM AppliedIndex WHERE ProposalId = '" + proposalId + "';"));
        Assert.Equal(6, PragmaInt(reopenedCheck, "user_version"));
    }

    [Fact]
    public void GenerationSixBoundaryFailureRollsBackAndCanBeRetried()
    {
        var path = DatabasePath("recovery-rollback.fwdata");
        using (Open("recovery-rollback.fwdata", supportedSchema: 5)) { }
        Assert.Throws<InvalidOperationException>(() => MotifDatabase.OpenOwnedForTesting(
            path, Locator("recovery-rollback.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0),
            schema => { if (schema == 6) throw new InvalidOperationException("injected gen6 failure"); }));
        using (var check = NewConnection(path))
        {
            Assert.Equal(5, PragmaInt(check, "user_version"));
            Assert.Null(Scalar(check, "SELECT name FROM pragma_table_info('Jobs') WHERE name = 'FailureCategory';"));
            Assert.NotNull(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'AppliedIndex';"));
        }
        using var retried = Open("recovery-rollback.fwdata", supportedSchema: MotifSchema.CurrentSchema);
        Assert.Equal(6, PragmaInt(retried.OpenConnection(), "user_version"));
    }

    [Fact]
    public void UnrecognizedAppIdZeroDatabaseIsRefusedWithoutAdoption()
    {
        var path = DatabasePath("unrecognized.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, "CREATE TABLE Corpora (CorpusId TEXT PRIMARY KEY, ProvenanceJson TEXT NOT NULL);");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("unrecognized.fwdata"), 2, new Version(1, 0)));
        Assert.Equal(0, PragmaFromPath(path, "application_id"));
    }

    [Fact]
    public void AppIdCorrectSchemaZeroDatabaseWithUserTablesIsRefusedWithoutAdoption()
    {
        var path = DatabasePath("unregistered-app-id.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, $"PRAGMA application_id = {MotifSchema.ApplicationId}; " +
                "CREATE TABLE ArbitraryUserTable (Value TEXT NOT NULL);");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("unregistered-app-id.fwdata"), 2, new Version(1, 0)));

        using var check = NewConnection(path);
        Assert.Equal(MotifSchema.ApplicationId, PragmaInt(check, "application_id"));
        Assert.Equal(0, PragmaInt(check, "user_version"));
        Assert.NotNull(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'ArbitraryUserTable';"));
    }

    [Fact]
    public void OnlyOneOwnerMayMigrateAndOpenAtATime()
    {
        var path = DatabasePath("exclusive.fwdata");
        using var first = MotifDatabase.OpenOwned(path, Locator("exclusive.fwdata"), 2, new Version(1, 0));

        Assert.Throws<IOException>(() => MotifDatabase.OpenOwned(path, Locator("exclusive.fwdata"), 2, new Version(1, 0)));
    }

    [Fact]
    public async Task OwnershipCanBeReleasedFromAnotherThread()
    {
        var path = DatabasePath("cross-thread.fwdata");
        var first = MotifDatabase.OpenOwned(path, Locator("cross-thread.fwdata"), 2, new Version(1, 0));
        await Task.Run(first.Dispose);
        Assert.False(File.Exists(path + ".owner.lock"));

        using var reopened = MotifDatabase.OpenOwned(path, Locator("cross-thread.fwdata"), 2, new Version(1, 0));
    }

    private MotifDatabase Open(string fileName, int supportedSchema) => MotifDatabase.OpenOwned(
        DatabasePath(fileName), Locator(fileName), supportedSchema, new Version(1, 0));

    private ProjectLocator Locator(string fileName) => new(ProjectPath(fileName), "new");

    private string ProjectPath(string fileName) => Path.Combine(_root, fileName);

    private string DatabasePath(string fileName) => Path.Combine(
        _root, Path.GetFileNameWithoutExtension(fileName) + ".motif.db");

    private static SqliteConnection NewConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int PragmaInt(SqliteConnection connection, string name) => Convert.ToInt32(Scalar(connection, $"PRAGMA {name};"));

    private static int PragmaFromPath(string path, string name)
    {
        using var connection = NewConnection(path);
        return PragmaInt(connection, name);
    }

    private static string PragmaText(SqliteConnection connection, string name) => (string)Scalar(connection, $"PRAGMA {name};")!;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
