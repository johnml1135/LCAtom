using System.Globalization;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Host.Store;

/// <summary>Owns the single, current SQLite schema for Motif's paired project database.</summary>
/// <remarks>
/// Motif is pre-1.0: a database is either exactly this shape or it is refused outright. There is no
/// migration ladder here — an older database is not upgraded, it is rejected with an actionable error
/// telling the developer to delete the file and let Motif recreate it.
/// </remarks>
public static class MotifSchema
{
    /// <summary>SQLite application identifier written to Motif-owned databases.</summary>
    public const int ApplicationId = 0x4D4F5446;

    /// <summary>The schema generation this assembly creates and requires.</summary>
    public const int CurrentSchema = 11;

    /// <summary>The worker version an open at the given schema ceiling requires.</summary>
    internal static Version MinimumWorkerVersion(int schema) => schema is >= 1 and <= CurrentSchema
        ? new Version(1, 0)
        : throw new NotSupportedException($"Motif schema {schema} is not known to this worker.");

    /// <summary>Builds every table, index and the identity row for a brand-new database, in one step.</summary>
    internal static void Create(SqliteConnection connection, SqliteTransaction? transaction, ProjectLocator project)
    {
        ValidateExistingTable(connection, transaction, "Corpora", "CorpusId", "ProvenanceJson");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = MetadataDdl + CorpusDdl + ProposalWorkflowDdl + AssessmentDdl + JobDdl + BaselineDdl;
            command.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO MotifMetadata
                (Id, FullFwDataPath, FieldWorksProjectIdentity, MinimumWorkerVersion, CreatedUtc)
            VALUES (1, $path, $identity, $version, $created);
            """;
        insert.Parameters.AddWithValue("$path", project.FullFwDataPath);
        insert.Parameters.AddWithValue("$identity", project.FieldWorksProjectIdentity);
        insert.Parameters.AddWithValue("$version", MinimumWorkerVersion(CurrentSchema).ToString());
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
    }

    /// <summary>Validates that an existing database has exactly the current schema's shape.</summary>
    internal static void ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var expectedTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
            "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions",
            "Decisions", "Receipts", "Reports", "AppliedIndex", "Jobs", "Baselines"
        };
        var expectedIndexes = new HashSet<string>(StringComparer.Ordinal)
        {
            "IX_AssessedWords_Assessment", "IX_AssessedWords_Word", "IX_ParsedAnalyses_Word",
            "IX_Jobs_Lineage_Attempt", "IX_Jobs_Status_Updated", "IX_Jobs_Lease", "IX_Jobs_QueueOrder",
            "IX_Proposals_DraftName", "IX_Assessments_Proposal", "IX_Assessments_Kind"
        };

        using (var objects = connection.CreateCommand())
        {
            objects.Transaction = transaction;
            objects.CommandText = "SELECT type, name FROM sqlite_master " +
                "WHERE name NOT LIKE 'sqlite_%' AND type IN ('table', 'index', 'view', 'trigger');";
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if ((type == "table" && expectedTables.Contains(name)) ||
                    (type == "index" && expectedIndexes.Contains(name)))
                    continue;
                throw new InvalidDataException($"Motif schema contains unexpected {type} {name}.");
            }
        }

        foreach (var table in expectedTables)
            ValidateTable(connection, transaction, table, ColumnsFor(table), ForeignKeysFor(table));
        foreach (var index in expectedIndexes)
            ValidateIndex(connection, transaction, index, IndexColumnsFor(index));
    }

    // Only the Corpus tables: an Assessment table exists because Create made it, never because of this.
    internal static void EnsureLegacyTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CorpusDdl;
        command.ExecuteNonQuery();
    }

    internal static (ProjectLocator Project, Version MinimumWorkerVersion) ReadMetadata(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM MotifMetadata;";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Motif database metadata must contain exactly one identity row.");

            command.CommandText = "SELECT FullFwDataPath, FieldWorksProjectIdentity, MinimumWorkerVersion FROM MotifMetadata WHERE Id = 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("Motif database metadata is missing its identity row.");

            var path = reader.GetString(0);
            var identity = reader.GetString(1);
            if (!Version.TryParse(reader.GetString(2), out var minimum))
                throw new InvalidDataException("Motif database metadata has an invalid minimum worker version.");
            return (new ProjectLocator(path, identity), minimum);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (SqliteException exception) when (SqliteConnections.IsCorruption(exception))
        {
            throw new InvalidDataException("Motif database metadata is corrupt.", exception);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidOperationException or
            InvalidCastException)
        {
            throw new InvalidDataException("Motif database metadata is corrupt.", exception);
        }
    }

    private static void ValidateTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ColumnShape> expectedColumns,
        IReadOnlyList<ForeignKeyShape> expectedForeignKeys)
    {
        var actual = ReadColumns(connection, transaction, table);
        if (!MatchesColumns(actual, expectedColumns))
            throw new InvalidDataException($"Motif table {table} does not match its registered schema.");

        ValidateForeignKeys(connection, transaction, table, expectedForeignKeys);
        ValidateTableInvariant(connection, transaction, table);
    }

    private static List<ColumnShape> ReadColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk " +
            "FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var actual = new List<ColumnShape>();
        while (reader.Read())
        {
            actual.Add(new ColumnShape(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4)));
        }
        return actual;
    }

    private static bool MatchesColumns(IReadOnlyList<ColumnShape> actual, IReadOnlyList<ColumnShape> expected) =>
        actual.Count == expected.Count && !actual.Where((column, index) => !column.Matches(expected[index])).Any();

    private static void ValidateTableInvariant(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var requiredSql = table switch
        {
            "MotifMetadata" => "CHECK (Id = 1)",
            "AssessedWords" => "AUTOINCREMENT",
            _ => null
        };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $table;";
        command.Parameters.AddWithValue("$table", table);
        var sql = command.ExecuteScalar() as string;
        if (sql is null || (requiredSql is not null && sql.IndexOf(requiredSql, StringComparison.OrdinalIgnoreCase) < 0))
            throw new InvalidDataException($"Motif table {table} is missing a required invariant.");
        if (table == "Jobs")
        {
            foreach (var invariant in new[] { "CHECK (Attempt > 0)", "CHECK (CancellationRequested IN (0, 1))",
                "CHECK (Version >= 0)", "CHECK (DryRunPublished IN (0, 1))" })
                if (sql.IndexOf(invariant, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException($"Motif table {table} is missing a required invariant.");
        }
    }

    private static void ValidateIndex(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string index,
        IReadOnlyList<string> expectedColumns)
    {
        using var list = connection.CreateCommand();
        list.Transaction = transaction;
        list.CommandText = "SELECT \"unique\" FROM pragma_index_list($table) WHERE name = $index;";
        list.Parameters.AddWithValue("$table", IndexTableFor(index));
        list.Parameters.AddWithValue("$index", index);
        var unique = Convert.ToInt32(list.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        if (unique != IsUniqueIndex(index))
            throw new InvalidDataException($"Motif index {index} has an unexpected uniqueness constraint.");

        using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno;";
        columns.Parameters.AddWithValue("$index", index);
        using var reader = columns.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read()) actual.Add(reader.GetString(0));
        reader.Dispose();
        if (!actual.SequenceEqual(expectedColumns, StringComparer.Ordinal))
            throw new InvalidDataException($"Motif index {index} does not match its registered schema.");

        using var details = connection.CreateCommand();
        details.Transaction = transaction;
        details.CommandText = "SELECT origin, partial FROM pragma_index_list($table) WHERE name = $index;";
        details.Parameters.AddWithValue("$table", IndexTableFor(index));
        details.Parameters.AddWithValue("$index", index);
        using var detailReader = details.ExecuteReader();
        if (!detailReader.Read() || !StringComparer.Ordinal.Equals(detailReader.GetString(0), "c") ||
            detailReader.GetInt32(1) != 0)
            throw new InvalidDataException($"Motif index {index} has unexpected registration details.");
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ForeignKeyShape> expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"table\", \"from\", \"to\", on_update, on_delete, match " +
            "FROM pragma_foreign_key_list($table) ORDER BY id, seq;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var actual = new List<ForeignKeyShape>();
        while (reader.Read())
            actual.Add(new ForeignKeyShape(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException($"Motif table {table} has an unexpected foreign-key shape.");
    }

    private static string IndexTableFor(string index) => index switch
    {
        "IX_AssessedWords_Assessment" or "IX_AssessedWords_Word" => "AssessedWords",
        "IX_ParsedAnalyses_Word" => "ParsedAnalyses",
        "IX_Jobs_Lineage_Attempt" or "IX_Jobs_Status_Updated" or "IX_Jobs_Lease" or "IX_Jobs_QueueOrder" => "Jobs",
        "IX_Proposals_DraftName" => "Proposals",
        "IX_Assessments_Proposal" or "IX_Assessments_Kind" => "Assessments",
        _ => throw new InvalidDataException($"Motif index {index} is not registered.")
    };

    private static bool IsUniqueIndex(string index) => index is "IX_Jobs_Lineage_Attempt" or "IX_Proposals_DraftName";

    private static IReadOnlyList<string> IndexColumnsFor(string index) => index switch
    {
        "IX_AssessedWords_Assessment" => ["AssessmentId"],
        "IX_AssessedWords_Word" => ["AssessmentId", "Word"],
        "IX_ParsedAnalyses_Word" => ["AssessedWordId"],
        "IX_Jobs_Lineage_Attempt" => ["LineageId", "Attempt"],
        "IX_Jobs_Status_Updated" => ["Status", "UpdatedUtc"],
        "IX_Jobs_Lease" => ["Status", "LeaseUntilUtc"],
        "IX_Jobs_QueueOrder" => ["QueueOrder"],
        "IX_Proposals_DraftName" => ["DraftName"],
        "IX_Assessments_Proposal" => ["ProposalId"],
        "IX_Assessments_Kind" => ["Kind"],
        _ => throw new InvalidDataException($"Motif index {index} is not registered.")
    };

    private static IReadOnlyList<ForeignKeyShape> ForeignKeysFor(string table) => table switch
    {
        "CorpusDocuments" => [new("Corpora", "CorpusId", "CorpusId", "NO ACTION", "NO ACTION", "NONE")],
        "Assessments" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "AssessedWords" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ParsedAnalyses" => [new("AssessedWords", "AssessedWordId", "AssessedWordId", "NO ACTION", "NO ACTION", "NONE")],
        "AssessmentPins" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ProposalRevisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Decisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Receipts" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Reports" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE"),
            new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        _ => []
    };

    private static IReadOnlyList<ColumnShape> ColumnsFor(string table) => table switch
    {
        "MotifMetadata" =>
        [C("Id", "INTEGER", false, 1), C("FullFwDataPath", "TEXT", true),
            C("FieldWorksProjectIdentity", "TEXT", true), C("MinimumWorkerVersion", "TEXT", true),
            C("CreatedUtc", "TEXT", true), C("CurrentAssessmentId", "TEXT")],
        "Corpora" => [C("CorpusId", "TEXT", false, 1), C("ProvenanceJson", "TEXT", true)],
        "CorpusDocuments" =>
        [C("CorpusId", "TEXT", true, 1), C("DocumentId", "TEXT", true, 2), C("OrdinalIndex", "INTEGER", true),
            C("Title", "TEXT", true), C("Source", "TEXT", true), C("Text", "TEXT", true),
            C("ContentSha256", "TEXT", true), C("IngestedUtc", "TEXT", true), C("Licence", "TEXT"),
            C("CapabilitiesJson", "TEXT"), C("AttributesJson", "TEXT")],
        "Assessments" =>
        [C("AssessmentId", "TEXT", false, 1), C("SelectionName", "TEXT", true), C("SelectionWordsJson", "TEXT", true),
            C("SelectionSha256", "TEXT", true), C("SelectionProvenanceJson", "TEXT"), C("OutcomeDigest", "TEXT", true),
            C("SemanticDigest", "TEXT", true), C("GrammarSourceSha256", "TEXT", true),
            C("ModelFingerprint", "TEXT", true), C("Pipeline", "TEXT", true), C("DiagnosticCount", "INTEGER", true),
            C("SavedUtc", "TEXT", true), C("ProposalId", "TEXT"), C("ProposalIntentDigest", "TEXT"),
            C("Assessor", "TEXT", true), C("Kind", "TEXT", true), C("ScopeJson", "TEXT", true),
            C("ScopeDigest", "TEXT", true), C("TokeniserName", "TEXT", true), C("TokeniserVersion", "TEXT", true),
            C("BaselineToken", "TEXT", true), C("CachePath", "TEXT"), C("CacheDigest", "TEXT")],
        "AssessedWords" =>
        [C("AssessedWordId", "INTEGER", false, 1), C("AssessmentId", "TEXT", true), C("OrdinalIndex", "INTEGER", true),
            C("Word", "TEXT", true), C("Outcome", "TEXT", true)],
        "ParsedAnalyses" =>
        [C("AssessedWordId", "INTEGER", true), C("OrdinalIndex", "INTEGER", true), C("CategoryGuid", "TEXT"),
            C("MorphemeGuidsJson", "TEXT", true), C("RootIndex", "INTEGER", true), C("IdentityDigest", "TEXT", true)],
        "AssessmentPins" =>
        [C("AssessmentId", "TEXT", true, 1), C("PinnedBy", "TEXT", true, 2), C("PinnedUtc", "TEXT", true)],
        "Proposals" =>
        [C("ProposalId", "TEXT", false, 1), C("CurrentIntentDigest", "TEXT"), C("Status", "TEXT", true),
            C("Label", "TEXT"), C("Comment", "TEXT"), C("SupersededBy", "TEXT"), C("AnchorJson", "TEXT"),
            C("ArchivedUtc", "TEXT"), C("DraftName", "TEXT"), C("DraftJson", "TEXT")],
        "ProposalRevisions" =>
        [C("ProposalId", "TEXT", true, 1), C("IntentDigest", "TEXT", true, 2),
            C("ProposalJson", "BLOB", true), C("CreatedUtc", "TEXT", true)],
        "Decisions" =>
        [C("ProposalId", "TEXT", true, 1), C("IntentDigest", "TEXT", true, 2),
            C("Outcome", "TEXT", true), C("ActorType", "TEXT", true), C("ActorId", "TEXT", true),
            C("Comment", "TEXT"), C("TimestampUtc", "TEXT", true)],
        "Receipts" =>
        [C("ReceiptId", "TEXT", false, 1), C("ProposalId", "TEXT", true),
            C("IntentDigest", "TEXT", true), C("ReceiptJson", "TEXT", true), C("RecordedUtc", "TEXT", true)],
        "Reports" =>
        [C("ReportId", "TEXT", false, 1), C("ProposalId", "TEXT"), C("AssessmentId", "TEXT"),
            C("ReportJson", "TEXT", true), C("EvidenceJson", "TEXT"), C("CreatedUtc", "TEXT", true),
            C("Kind", "TEXT"), C("RenderedText", "TEXT")],
        "AppliedIndex" =>
        [C("ProposalId", "TEXT", false, 1), C("IntentDigest", "TEXT", true),
            C("AppliedUtc", "TEXT", true), C("RecordJson", "TEXT")],
        "Jobs" =>
        [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true), C("Status", "TEXT", true),
            C("Attempt", "INTEGER", true, defaultValue: "1"), C("LineageId", "TEXT", true), C("InputJson", "TEXT", true),
            C("ResultJson", "TEXT"), C("ProgressJson", "TEXT"), C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
            C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true), C("Version", "INTEGER", true, defaultValue: "0"),
            C("DryRunPublished", "INTEGER", true, defaultValue: "0"), C("DryRunJson", "TEXT"),
            C("FailureCategory", "TEXT", true, defaultValue: "'none'"), C("NotBeforeUtc", "TEXT"), C("ArchivedUtc", "TEXT"),
            C("OwnerId", "TEXT"), C("ClaimToken", "TEXT"), C("LeaseUntilUtc", "TEXT"), C("HeartbeatUtc", "TEXT"),
            C("QueueOrder", "REAL", true, defaultValue: "CAST((julianday('now') - 2440587.5) * 86400000.0 AS REAL)")],
        "Baselines" =>
        [C("ProjectKey", "TEXT", false, 1), C("ProjectIdentity", "TEXT", true),
            C("SemanticSnapshotDigest", "TEXT", true), C("ProjectionVersion", "TEXT", true),
            C("CapturedUtc", "TEXT", true), C("BundleDigest", "TEXT", true),
            C("CapturedHostSessionId", "TEXT"), C("CapturedEditGeneration", "INTEGER"),
            C("RootDirectory", "TEXT", true), C("FwDataPath", "TEXT", true), C("PublishedUtc", "TEXT", true)],
        _ => throw new InvalidDataException($"Motif table {table} is not registered.")
    };

    private static ColumnShape C(string name, string type, bool notNull = false, int primaryKey = 0,
        string? defaultValue = null) => new(name, type, notNull, defaultValue, primaryKey);

    private sealed record ColumnShape(
        string Name,
        string Type,
        bool NotNull,
        string? DefaultValue,
        int PrimaryKey)
    {
        public bool Matches(ColumnShape expected) =>
            StringComparer.OrdinalIgnoreCase.Equals(Name, expected.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(Type, expected.Type) &&
            NotNull == expected.NotNull &&
            DefaultValue == expected.DefaultValue &&
            PrimaryKey == expected.PrimaryKey;
    }

    private sealed record ForeignKeyShape(
        string Table,
        string From,
        string To,
        string OnUpdate,
        string OnDelete,
        string Match);

    // Guards the race with EnsureLegacyTables: if the Corpus tables already exist, they must match.
    private static void ValidateExistingTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        params string[] columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) found.Add(reader.GetString(0));
        if (found.Count == 0) return;
        if (columns.Any(column => !found.Contains(column)))
            throw new InvalidDataException($"Existing Motif table {table} does not match the known schema.");
    }

    private const string MetadataDdl = """
        CREATE TABLE MotifMetadata (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            FullFwDataPath TEXT NOT NULL,
            FieldWorksProjectIdentity TEXT NOT NULL,
            MinimumWorkerVersion TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL,
            CurrentAssessmentId TEXT NULL
        );

        """;

    private const string CorpusDdl = """
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

        """;

    private const string ProposalWorkflowDdl = """
        CREATE TABLE Proposals (
            ProposalId TEXT PRIMARY KEY,
            CurrentIntentDigest TEXT NULL,
            Status TEXT NOT NULL,
            Label TEXT NULL,
            Comment TEXT NULL,
            SupersededBy TEXT NULL,
            AnchorJson TEXT NULL,
            ArchivedUtc TEXT NULL,
            DraftName TEXT NULL,
            DraftJson TEXT NULL
        );
        CREATE UNIQUE INDEX IX_Proposals_DraftName ON Proposals(DraftName);

        CREATE TABLE ProposalRevisions (
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            ProposalJson BLOB NOT NULL,
            CreatedUtc TEXT NOT NULL,
            PRIMARY KEY (ProposalId, IntentDigest)
        );

        CREATE TABLE Decisions (
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            ActorType TEXT NOT NULL,
            ActorId TEXT NOT NULL,
            Comment TEXT NULL,
            TimestampUtc TEXT NOT NULL,
            PRIMARY KEY (ProposalId, IntentDigest)
        );

        CREATE TABLE Receipts (
            ReceiptId TEXT PRIMARY KEY,
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            ReceiptJson TEXT NOT NULL,
            RecordedUtc TEXT NOT NULL
        );

        CREATE TABLE AppliedIndex (
            ProposalId TEXT PRIMARY KEY,
            IntentDigest TEXT NOT NULL,
            AppliedUtc TEXT NOT NULL,
            RecordJson TEXT NULL
        );

        """;

    private const string AssessmentDdl = """
        CREATE TABLE Assessments (
            AssessmentId TEXT PRIMARY KEY,
            SelectionName TEXT NOT NULL,
            SelectionWordsJson TEXT NOT NULL,
            SelectionSha256 TEXT NOT NULL,
            SelectionProvenanceJson TEXT NULL,
            OutcomeDigest TEXT NOT NULL,
            SemanticDigest TEXT NOT NULL,
            GrammarSourceSha256 TEXT NOT NULL,
            ModelFingerprint TEXT NOT NULL,
            Pipeline TEXT NOT NULL,
            DiagnosticCount INTEGER NOT NULL,
            SavedUtc TEXT NOT NULL,
            ProposalId TEXT NULL REFERENCES Proposals(ProposalId),
            ProposalIntentDigest TEXT NULL,
            Assessor TEXT NOT NULL,
            Kind TEXT NOT NULL,
            ScopeJson TEXT NOT NULL,
            ScopeDigest TEXT NOT NULL,
            TokeniserName TEXT NOT NULL,
            TokeniserVersion TEXT NOT NULL,
            BaselineToken TEXT NOT NULL,
            CachePath TEXT NULL,
            CacheDigest TEXT NULL
        );
        CREATE INDEX IX_Assessments_Proposal ON Assessments(ProposalId);
        CREATE INDEX IX_Assessments_Kind ON Assessments(Kind);

        CREATE TABLE AssessedWords (
            AssessedWordId INTEGER PRIMARY KEY AUTOINCREMENT,
            AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
            OrdinalIndex INTEGER NOT NULL,
            Word TEXT NOT NULL,
            Outcome TEXT NOT NULL
        );
        CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(AssessmentId);
        CREATE INDEX IX_AssessedWords_Word ON AssessedWords(AssessmentId, Word);

        CREATE TABLE ParsedAnalyses (
            AssessedWordId INTEGER NOT NULL REFERENCES AssessedWords(AssessedWordId),
            OrdinalIndex INTEGER NOT NULL,
            CategoryGuid TEXT NULL,
            MorphemeGuidsJson TEXT NOT NULL,
            RootIndex INTEGER NOT NULL,
            IdentityDigest TEXT NOT NULL
        );
        CREATE INDEX IX_ParsedAnalyses_Word ON ParsedAnalyses(AssessedWordId);

        CREATE TABLE AssessmentPins (
            AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
            PinnedBy TEXT NOT NULL,
            PinnedUtc TEXT NOT NULL,
            PRIMARY KEY (AssessmentId, PinnedBy)
        );

        CREATE TABLE Reports (
            ReportId TEXT PRIMARY KEY,
            ProposalId TEXT NULL REFERENCES Proposals(ProposalId),
            AssessmentId TEXT NULL REFERENCES Assessments(AssessmentId),
            ReportJson TEXT NOT NULL,
            EvidenceJson TEXT NULL,
            CreatedUtc TEXT NOT NULL,
            Kind TEXT NULL,
            RenderedText TEXT NULL
        );

        """;

    private const string JobDdl = """
        CREATE TABLE Jobs (
            JobId TEXT PRIMARY KEY,
            ProjectKey TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Status TEXT NOT NULL,
            Attempt INTEGER NOT NULL DEFAULT 1 CHECK (Attempt > 0),
            LineageId TEXT NOT NULL,
            InputJson TEXT NOT NULL,
            ResultJson TEXT NULL,
            ProgressJson TEXT NULL,
            CancellationRequested INTEGER NOT NULL DEFAULT 0 CHECK (CancellationRequested IN (0, 1)),
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            Version INTEGER NOT NULL DEFAULT 0 CHECK (Version >= 0),
            DryRunPublished INTEGER NOT NULL DEFAULT 0 CHECK (DryRunPublished IN (0, 1)),
            DryRunJson TEXT NULL,
            FailureCategory TEXT NOT NULL DEFAULT 'none',
            NotBeforeUtc TEXT NULL,
            ArchivedUtc TEXT NULL,
            OwnerId TEXT NULL,
            ClaimToken TEXT NULL,
            LeaseUntilUtc TEXT NULL,
            HeartbeatUtc TEXT NULL,
            QueueOrder REAL NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000.0 AS REAL))
        );
        CREATE UNIQUE INDEX IX_Jobs_Lineage_Attempt ON Jobs(LineageId, Attempt);
        CREATE INDEX IX_Jobs_Status_Updated ON Jobs(Status, UpdatedUtc);
        CREATE INDEX IX_Jobs_Lease ON Jobs(Status, LeaseUntilUtc);
        CREATE INDEX IX_Jobs_QueueOrder ON Jobs(QueueOrder);

        """;

    private const string BaselineDdl = """
        CREATE TABLE Baselines (
            ProjectKey TEXT PRIMARY KEY,
            ProjectIdentity TEXT NOT NULL,
            SemanticSnapshotDigest TEXT NOT NULL,
            ProjectionVersion TEXT NOT NULL,
            CapturedUtc TEXT NOT NULL,
            BundleDigest TEXT NOT NULL,
            CapturedHostSessionId TEXT NULL,
            CapturedEditGeneration INTEGER NULL,
            RootDirectory TEXT NOT NULL,
            FwDataPath TEXT NOT NULL,
            PublishedUtc TEXT NOT NULL,
            CHECK ((CapturedHostSessionId IS NULL) = (CapturedEditGeneration IS NULL)),
            CHECK (CapturedEditGeneration IS NULL OR CapturedEditGeneration >= 0)
        );
        """;
}
