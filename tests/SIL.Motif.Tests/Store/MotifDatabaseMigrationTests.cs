using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Projects;
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
        Assert.Equal(SqliteConnections.BusyTimeoutMilliseconds, PragmaInt(connection, "busy_timeout"));
        Assert.Equal("new", Scalar(connection, "SELECT FieldWorksProjectIdentity FROM MotifMetadata WHERE Id = 1;"));
        Assert.Equal(Path.Combine(_root, "new.fwdata"), Scalar(connection, "SELECT FullFwDataPath FROM MotifMetadata WHERE Id = 1;"));
        Assert.Same(DBNull.Value, Scalar(connection, "SELECT CurrentAssessmentId FROM MotifMetadata WHERE Id = 1;"));
    }

    [Fact]
    public void CatalogUsesTheSiblingMotifDatabasePath()
    {
        var project = Locator("catalog.fwdata");
        Assert.Equal(DatabasePath("catalog.fwdata"), ProjectDatabaseCatalog.DatabasePathFor(project));

        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var database = catalog.OpenOwned(project);
        Assert.True(File.Exists(DatabasePath("catalog.fwdata")));
    }

    [Fact]
    public void EquivalentLocatorsShareDatabaseAndPersistCanonicalPath()
    {
        var canonicalPath = Path.Combine(_root, "equivalent", "project.fwdata");
        var slashAndDotPath = canonicalPath.Replace('\\', '/')
            .Replace("/project.fwdata", "/./project.fwdata", StringComparison.Ordinal);
        var first = new ProjectLocator(slashAndDotPath, "same-project");
        var second = new ProjectLocator(canonicalPath, "same-project");

        Assert.Equal(first.FullFwDataPath, second.FullFwDataPath);
        Assert.Equal(
            ProjectDatabaseCatalog.DatabasePathFor(first),
            ProjectDatabaseCatalog.DatabasePathFor(second));

        using (MotifDatabase.OpenOwned(
            ProjectDatabaseCatalog.DatabasePathFor(first), first, MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var reopened = MotifDatabase.OpenOwned(
                   ProjectDatabaseCatalog.DatabasePathFor(second), second, MotifSchema.CurrentSchema, new Version(1, 0)))
        using (var connection = reopened.OpenConnection())
        {
            Assert.Equal(first.FullFwDataPath,
                Scalar(connection, "SELECT FullFwDataPath FROM MotifMetadata WHERE Id = 1;"));
        }

        var differentDirectory = new ProjectLocator(
            Path.Combine(_root, "different", "project.fwdata"), "same-project");
        Assert.NotEqual(ProjectWorkspaceKey.Compute(first), ProjectWorkspaceKey.Compute(differentDirectory));
        Assert.NotEqual(
            ProjectDatabaseCatalog.DatabasePathFor(first),
            ProjectDatabaseCatalog.DatabasePathFor(differentDirectory));
    }

    [Fact]
    public void NewDatabaseRequiresTheTargetSchemaMinimumWorkerVersion()
    {
        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(
            DatabasePath("too-old.fwdata"), Locator("too-old.fwdata"), MotifSchema.CurrentSchema, new Version(0, 9)));
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
        using (MotifDatabase.OpenOwned(path, Locator("configuration-failure.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }

        Assert.Throws<InvalidOperationException>(() => MotifDatabase.OpenConfiguredConnectionForTesting(
            path, connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "BEGIN EXCLUSIVE;";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("injected configuration failure");
            }));
        using var reopened = MotifDatabase.OpenOwned(
            path, Locator("configuration-failure.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void DisposingOwnerClosesReturnedConnectionsBeforeReleasingOwnership()
    {
        var path = DatabasePath("drain.fwdata");
        var owner = MotifDatabase.OpenOwned(path, Locator("drain.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
        var connection = owner.OpenConnection();
        Assert.Equal(1, owner.TrackedConnectionCount);

        owner.Dispose();

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Throws<ObjectDisposedException>(() => connection.Open());
        using var reopened = MotifDatabase.OpenOwned(path, Locator("drain.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void ClosedConnectionRemainsOwnedUntilItIsDisposed()
    {
        var path = DatabasePath("closed.fwdata");
        var owner = MotifDatabase.OpenOwned(path, Locator("closed.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
        var connection = owner.OpenConnection();
        connection.Close();

        owner.Dispose();

        Assert.Throws<ObjectDisposedException>(() => connection.Open());
    }

    [Fact]
    public async Task ConcurrentDisposeAndOpenDoesNotReturnPostReleaseConnection()
    {
        var path = DatabasePath("race.fwdata");
        using var owner = MotifDatabase.OpenOwned(path, Locator("race.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
        var returned = new List<SqliteConnection>();
        var opening = Task.Run(() =>
        {
            try { returned.Add(owner.OpenConnection()); }
            catch (ObjectDisposedException) { }
        });

        owner.Dispose();
        await opening;

        Assert.All(returned, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        using var reopened = MotifDatabase.OpenOwned(path, Locator("race.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void ClosedConnectionsAreRemovedFromOwnershipTracking()
    {
        var path = DatabasePath("cycles.fwdata");
        using var owner = MotifDatabase.OpenOwned(path, Locator("cycles.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));

        for (var i = 0; i < 100; i++)
        {
            using var connection = owner.OpenConnection();
        }

        Assert.Equal(0, owner.TrackedConnectionCount);
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
        using (MotifDatabase.OpenOwned(path, Locator("missing-" + objectName + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, objectName.StartsWith("IX_", StringComparison.Ordinal)
                ? "DROP INDEX " + objectName + ";"
                : "DROP TABLE " + objectName + ";");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-" + objectName + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Equal(MotifSchema.CurrentSchema, PragmaFromPath(path, "user_version"));
    }

    [Theory]
    [InlineData("Proposals")]
    [InlineData("ProposalRevisions")]
    [InlineData("Decisions")]
    [InlineData("Receipts")]
    [InlineData("Reports")]
    [InlineData("AppliedIndex")]
    public void CurrentSchemaRejectsMissingWorkflowObjects(string objectName)
    {
        var path = DatabasePath("missing-workflow-" + objectName + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-workflow-" + objectName + ".fwdata"), MotifSchema.CurrentSchema,
                   new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "DROP TABLE " + objectName + ";");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-workflow-" + objectName + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRejectsUnexpectedWorkflowObjectsAndAnchorShape()
    {
        var path = DatabasePath("malformed-workflow.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-workflow.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "ALTER TABLE Proposals RENAME COLUMN AnchorJson TO WrongAnchor; CREATE TABLE UnexpectedWorkflow (Id TEXT);");
        }
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-workflow.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));

        var fkPath = DatabasePath("malformed-workflow-fk.fwdata");
        using (MotifDatabase.OpenOwned(fkPath, Locator("malformed-workflow-fk.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(fkPath))
        {
            Execute(connection, "DROP TABLE Decisions; CREATE TABLE Decisions (ProposalId TEXT NOT NULL REFERENCES Reports(ReportId), " +
                "IntentDigest TEXT NOT NULL, Outcome TEXT NOT NULL, ActorType TEXT NOT NULL, ActorId TEXT NOT NULL, " +
                "Comment TEXT NULL, TimestampUtc TEXT NOT NULL, PRIMARY KEY (ProposalId, IntentDigest));");
        }
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            fkPath, Locator("malformed-workflow-fk.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaPinsEveryTableColumnAndForeignKeyInventory()
    {
        var path = DatabasePath("schema-inventory.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("schema-inventory.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using var connection = NewConnection(path);

        // Name|Type|NotNull|PrimaryKeyOrdinal|Default, pinned independently of MotifSchema's own tables.
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["MotifMetadata"] = ["Id|INTEGER|0|1|", "FullFwDataPath|TEXT|1|0|", "FieldWorksProjectIdentity|TEXT|1|0|",
                "MinimumWorkerVersion|TEXT|1|0|", "CreatedUtc|TEXT|1|0|", "CurrentAssessmentId|TEXT|0|0|"],
            ["Corpora"] = ["CorpusId|TEXT|0|1|", "ProvenanceJson|TEXT|1|0|"],
            ["CorpusDocuments"] = ["CorpusId|TEXT|1|1|", "DocumentId|TEXT|1|2|", "OrdinalIndex|INTEGER|1|0|",
                "Title|TEXT|1|0|", "Source|TEXT|1|0|", "Text|TEXT|1|0|", "ContentSha256|TEXT|1|0|",
                "IngestedUtc|TEXT|1|0|", "Licence|TEXT|0|0|", "CapabilitiesJson|TEXT|0|0|", "AttributesJson|TEXT|0|0|"],
            ["Assessments"] = ["AssessmentId|TEXT|0|1|", "SelectionName|TEXT|1|0|", "SelectionWordsJson|TEXT|1|0|",
                "SelectionSha256|TEXT|1|0|", "SelectionProvenanceJson|TEXT|0|0|", "OutcomeDigest|TEXT|1|0|",
                "SemanticDigest|TEXT|1|0|", "GrammarSourceSha256|TEXT|1|0|", "ModelFingerprint|TEXT|1|0|",
                "Pipeline|TEXT|1|0|", "DiagnosticCount|INTEGER|1|0|", "SavedUtc|TEXT|1|0|", "ProposalId|TEXT|0|0|",
                "ProposalIntentDigest|TEXT|0|0|", "Assessor|TEXT|1|0|", "Kind|TEXT|1|0|", "ScopeJson|TEXT|1|0|",
                "ScopeDigest|TEXT|1|0|", "TokeniserName|TEXT|1|0|", "TokeniserVersion|TEXT|1|0|",
                "BaselineToken|TEXT|1|0|", "CachePath|TEXT|0|0|", "CacheDigest|TEXT|0|0|"],
            ["AssessedWords"] = ["AssessedWordId|INTEGER|0|1|", "AssessmentId|TEXT|1|0|", "OrdinalIndex|INTEGER|1|0|",
                "Word|TEXT|1|0|", "Outcome|TEXT|1|0|"],
            ["ParsedAnalyses"] = ["AssessedWordId|INTEGER|1|0|", "OrdinalIndex|INTEGER|1|0|", "CategoryGuid|TEXT|0|0|",
                "MorphemeGuidsJson|TEXT|1|0|", "RootIndex|INTEGER|1|0|", "IdentityDigest|TEXT|1|0|"],
            ["AssessmentPins"] = ["AssessmentId|TEXT|1|1|", "PinnedBy|TEXT|1|2|", "PinnedUtc|TEXT|1|0|"],
            ["Proposals"] = ["ProposalId|TEXT|0|1|", "CurrentIntentDigest|TEXT|0|0|", "Status|TEXT|1|0|",
                "Label|TEXT|0|0|", "Comment|TEXT|0|0|", "SupersededBy|TEXT|0|0|", "AnchorJson|TEXT|0|0|",
                "ArchivedUtc|TEXT|0|0|", "DraftName|TEXT|0|0|", "DraftJson|TEXT|0|0|"],
            ["ProposalRevisions"] = ["ProposalId|TEXT|1|1|", "IntentDigest|TEXT|1|2|", "ProposalJson|BLOB|1|0|",
                "CreatedUtc|TEXT|1|0|"],
            ["Decisions"] = ["ProposalId|TEXT|1|1|", "IntentDigest|TEXT|1|2|", "Outcome|TEXT|1|0|",
                "ActorType|TEXT|1|0|", "ActorId|TEXT|1|0|", "Comment|TEXT|0|0|", "TimestampUtc|TEXT|1|0|"],
            ["Receipts"] = ["ReceiptId|TEXT|0|1|", "ProposalId|TEXT|1|0|", "IntentDigest|TEXT|1|0|",
                "ReceiptJson|TEXT|1|0|", "RecordedUtc|TEXT|1|0|"],
            ["Reports"] = ["ReportId|TEXT|0|1|", "ProposalId|TEXT|0|0|", "AssessmentId|TEXT|0|0|",
                "ReportJson|TEXT|1|0|", "EvidenceJson|TEXT|0|0|", "CreatedUtc|TEXT|1|0|", "Kind|TEXT|0|0|",
                "RenderedText|TEXT|0|0|"],
            ["AppliedIndex"] = ["ProposalId|TEXT|0|1|", "IntentDigest|TEXT|1|0|", "AppliedUtc|TEXT|1|0|",
                "RecordJson|TEXT|0|0|"],
            ["Baselines"] = ["ProjectKey|TEXT|0|1|", "ProjectIdentity|TEXT|1|0|", "SemanticSnapshotDigest|TEXT|1|0|",
                "ProjectionVersion|TEXT|1|0|", "CapturedUtc|TEXT|1|0|", "BundleDigest|TEXT|1|0|",
                "CapturedHostSessionId|TEXT|0|0|", "CapturedEditGeneration|INTEGER|0|0|", "RootDirectory|TEXT|1|0|",
                "FwDataPath|TEXT|1|0|", "PublishedUtc|TEXT|1|0|"]
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
            ["CorpusDocuments"] = ["Corpora|CorpusId|CorpusId|NO ACTION|NO ACTION|NONE"],
            ["Assessments"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["AssessedWords"] = ["Assessments|AssessmentId|AssessmentId|NO ACTION|NO ACTION|NONE"],
            ["ParsedAnalyses"] = ["AssessedWords|AssessedWordId|AssessedWordId|NO ACTION|NO ACTION|NONE"],
            ["AssessmentPins"] = ["Assessments|AssessmentId|AssessmentId|NO ACTION|NO ACTION|NONE"],
            ["ProposalRevisions"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Decisions"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Receipts"] = ["Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["Reports"] = ["Assessments|AssessmentId|AssessmentId|NO ACTION|NO ACTION|NONE", "Proposals|ProposalId|ProposalId|NO ACTION|NO ACTION|NONE"],
            ["AppliedIndex"] = []
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
        using (MotifDatabase.OpenOwned(path, Locator("unexpected-object.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "CREATE TABLE UnexpectedUserTable (Value TEXT NOT NULL);");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("unexpected-object.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Equal(MotifSchema.CurrentSchema, PragmaFromPath(path, "user_version"));
    }

    [Fact]
    public void CurrentSchemaRejectsMalformedForeignKeyShape()
    {
        var path = DatabasePath("malformed-fk.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-fk.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE CorpusDocuments; CREATE TABLE CorpusDocuments " +
                "(CorpusId TEXT NOT NULL, DocumentId TEXT NOT NULL, OrdinalIndex INTEGER NOT NULL, " +
                "Title TEXT NOT NULL, Source TEXT NOT NULL, Text TEXT NOT NULL, ContentSha256 TEXT NOT NULL, " +
                "IngestedUtc TEXT NOT NULL, Licence TEXT NULL, CapabilitiesJson TEXT NULL, AttributesJson TEXT NULL, " +
                "PRIMARY KEY (CorpusId, DocumentId));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-fk.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Theory]
    [InlineData("type", "AssessmentId INTEGER NOT NULL")]
    [InlineData("nullability", "AssessmentId TEXT NULL")]
    [InlineData("primary-key", "AssessmentId TEXT NOT NULL")]
    public void CurrentSchemaRejectsMalformedColumnShape(string mutation, string assessmentIdColumn)
    {
        var path = DatabasePath("malformed-column-" + mutation + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-column-" + mutation + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessmentPins; CREATE TABLE AssessmentPins (" + assessmentIdColumn + ", " +
                "PinnedBy TEXT NOT NULL, PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-column-" + mutation + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Theory]
    [InlineData("partial", "CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(AssessmentId) WHERE Word IS NOT NULL;")]
    [InlineData("columns", "CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(Word);")]
    public void CurrentSchemaRejectsMalformedNamedIndex(string mutation, string createIndex)
    {
        var path = DatabasePath("malformed-index-" + mutation + ".fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-index-" + mutation + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "DROP INDEX IX_AssessedWords_Assessment; " + createIndex);

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-index-" + mutation + ".fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRejectsForeignKeyActionChange()
    {
        var path = DatabasePath("malformed-fk-action.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("malformed-fk-action.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessmentPins; CREATE TABLE AssessmentPins (" +
                "AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId) ON DELETE CASCADE, " +
                "PinnedBy TEXT NOT NULL, PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy));");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("malformed-fk-action.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRequiresMetadataCheckConstraint()
    {
        var path = DatabasePath("missing-metadata-check.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-metadata-check.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE MotifMetadata; CREATE TABLE MotifMetadata (" +
                "Id INTEGER PRIMARY KEY, FullFwDataPath TEXT NOT NULL, FieldWorksProjectIdentity TEXT NOT NULL, " +
                "MinimumWorkerVersion TEXT NOT NULL, CreatedUtc TEXT NOT NULL);");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-metadata-check.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CurrentSchemaRequiresAssessedWordsAutoincrement()
    {
        var path = DatabasePath("missing-autoincrement.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("missing-autoincrement.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
        {
            Execute(connection, "DROP TABLE AssessedWords; CREATE TABLE AssessedWords (" +
                "AssessedWordId INTEGER PRIMARY KEY, AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId), " +
                "OrdinalIndex INTEGER NOT NULL, Word TEXT NOT NULL, Outcome TEXT NOT NULL);");
        }

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-autoincrement.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void CorruptDatabaseBytesAreReportedAsInvalidData()
    {
        var path = DatabasePath("corrupt-bytes.fwdata");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x4F, 0x54, 0x49, 0x46, 0x00, 0x01 });

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("corrupt-bytes.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void AvailabilityErrorsAreNotClassifiedAsCorruption()
    {
        Assert.True(SqliteConnections.IsCorruptionCode(26));
        Assert.True(SqliteConnections.IsCorruptionCode(11));
        Assert.False(SqliteConnections.IsCorruptionCode(1));
        Assert.False(SqliteConnections.IsCorruptionCode(17));
        Assert.False(SqliteConnections.IsCorruptionCode(24));
        Assert.False(SqliteConnections.IsCorruptionCode(5));
        Assert.False(SqliteConnections.IsCorruptionCode(6));
        Assert.False(SqliteConnections.IsCorruptionCode(8));
        Assert.False(SqliteConnections.IsCorruptionCode(10));
        Assert.False(SqliteConnections.IsCorruptionCode(13));
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
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(path, Locator("wrong.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Equal(before, PragmaFromPath(path, "user_version"));
    }

    [Fact]
    public void NewerSchemaIsRefusedWithoutDowngrade()
    {
        var path = DatabasePath("newer.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, $"PRAGMA application_id = {MotifSchema.ApplicationId}; PRAGMA user_version = 99;");

        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(path, Locator("newer.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
        using var check = NewConnection(path);
        Assert.Equal(99, PragmaInt(check, "user_version"));
    }

    [Fact]
    public void MissingMetadataIsReportedAsCorruptionWithoutWrites()
    {
        var path = DatabasePath("missing-metadata.fwdata");
        using (Open("missing-metadata.fwdata", supportedSchema: MotifSchema.CurrentSchema)) { }
        using (var connection = NewConnection(path))
            Execute(connection, "DELETE FROM MotifMetadata;");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("missing-metadata.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Equal(MotifSchema.CurrentSchema, PragmaFromPath(path, "user_version"));
        Assert.Equal(MotifSchema.ApplicationId, PragmaFromPath(path, "application_id"));
    }

    [Fact]
    public void InvalidMetadataVersionIsReportedAsCorruption()
    {
        var path = DatabasePath("invalid-metadata.fwdata");
        using (Open("invalid-metadata.fwdata", supportedSchema: MotifSchema.CurrentSchema)) { }
        using (var connection = NewConnection(path))
            Execute(connection, "UPDATE MotifMetadata SET MinimumWorkerVersion = 'not-a-version' WHERE Id = 1;");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("invalid-metadata.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void WorkerOlderThanDatabaseMinimumIsRefused()
    {
        var path = DatabasePath("minimum.fwdata");
        using (var created = MotifDatabase.OpenOwned(path, Locator("minimum.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var connection = NewConnection(path))
            Execute(connection, "UPDATE MotifMetadata SET MinimumWorkerVersion = '2.0' WHERE Id = 1;");

        Assert.Throws<NotSupportedException>(() => MotifDatabase.OpenOwned(
            path, Locator("minimum.fwdata"), MotifSchema.CurrentSchema, new Version(1, 9)));
    }

    [Fact]
    public void NewerWorkerOpeningSameSchemaDoesNotRaiseMinimumForOlderWorker()
    {
        var path = DatabasePath("same-schema.fwdata");
        using (MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }

        using (MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), MotifSchema.CurrentSchema, new Version(9, 0))) { }
        using var older = MotifDatabase.OpenOwned(path, Locator("same-schema.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
        using var connection = older.OpenConnection();

        Assert.Equal("1.0", Scalar(connection, "SELECT MinimumWorkerVersion FROM MotifMetadata WHERE Id = 1;"));
    }

    [Fact]
    public void LocatorMismatchIsDetectedWithoutWrites()
    {
        var path = DatabasePath("mismatch.fwdata");
        using (var initial = MotifDatabase.OpenOwned(path, Locator("mismatch.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0))) { }
        var before = PragmaFromPath(path, "user_version");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, new ProjectLocator(Path.Combine(_root, "other.fwdata"), "new"), MotifSchema.CurrentSchema, new Version(1, 0)));
        Assert.Equal(before, PragmaFromPath(path, "user_version"));

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, new ProjectLocator(Path.Combine(_root, "mismatch.fwdata"), "different"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void ValidateSchemaAcceptsTheCurrentShapeAndRejectsATamperedOne()
    {
        var path = DatabasePath("tamper.fwdata");
        using var database = Open("tamper.fwdata", supportedSchema: MotifSchema.CurrentSchema);
        using (var connection = database.OpenConnection())
            MotifSchema.ValidateSchema(connection);

        using (var connection = NewConnection(path))
            Execute(connection, "DROP INDEX IX_Assessments_Kind;");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("tamper.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void UnrecognizedAppIdZeroDatabaseIsRefusedWithoutAdoption()
    {
        var path = DatabasePath("unrecognized.fwdata");
        using (var connection = NewConnection(path))
            Execute(connection, "CREATE TABLE Corpora (CorpusId TEXT PRIMARY KEY, ProvenanceJson TEXT NOT NULL);");

        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(
            path, Locator("unrecognized.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));
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
            path, Locator("unregistered-app-id.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0)));

        using var check = NewConnection(path);
        Assert.Equal(MotifSchema.ApplicationId, PragmaInt(check, "application_id"));
        Assert.Equal(0, PragmaInt(check, "user_version"));
        Assert.NotNull(Scalar(check, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'ArbitraryUserTable';"));
    }

    [Fact]
    public void OpeningAnExistingDatabaseTwiceIsAllowedAndLeavesNoLockBehind()
    {
        var path = DatabasePath("exclusive.fwdata");
        using var first = MotifDatabase.OpenOwned(path, Locator("exclusive.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));

        using var second = MotifDatabase.OpenOwned(path, Locator("exclusive.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));

        Assert.NotSame(first, second);
        // The lock guards creation only, so nothing holds it once the schema already exists.
        Assert.False(File.Exists(path + ".owner.lock"));
    }

    [Fact]
    public async Task OwnershipCanBeReleasedFromAnotherThread()
    {
        var path = DatabasePath("cross-thread.fwdata");
        var first = MotifDatabase.OpenOwned(path, Locator("cross-thread.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
        await Task.Run(first.Dispose);
        Assert.False(File.Exists(path + ".owner.lock"));

        using var reopened = MotifDatabase.OpenOwned(path, Locator("cross-thread.fwdata"), MotifSchema.CurrentSchema, new Version(1, 0));
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
