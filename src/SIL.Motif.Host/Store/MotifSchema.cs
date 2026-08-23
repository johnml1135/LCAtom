using System.Globalization;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Host.Store;

/// <summary>Owns the ordered SQLite schema migrations for Motif's paired project database.</summary>
public static class MotifSchema
{
    /// <summary>SQLite application identifier written to Motif-owned databases.</summary>
    public const int ApplicationId = 0x4D4F5446;

    /// <summary>The newest ordered schema generation implemented by this assembly.</summary>
    public const int CurrentSchema = 4;

    /// <summary>The connection busy timeout used for short-lived worker database sessions.</summary>
    public const int BusyTimeoutMilliseconds = 15000;

    internal static Version MinimumWorkerVersion(int schema) => schema switch
    {
        1 or 2 or 3 or 4 => new Version(1, 0),
        _ => throw new NotSupportedException($"Motif schema {schema} is not known to this worker.")
    };

    internal static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = " + BusyTimeoutMilliseconds + "; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    internal static void ConfigureSession(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = " + BusyTimeoutMilliseconds + "; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    internal static void EnableWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }

    internal static void Migrate(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int currentSchema,
        int targetSchema,
        ProjectLocator project,
        Action<int>? afterMigrationStep = null)
    {
        for (var schema = currentSchema + 1; schema <= targetSchema; schema++)
        {
            switch (schema)
            {
                case 1:
                    CreateMetadata(connection, transaction, project);
                    break;
                case 2:
                    CreateCorpusAndAssessmentTables(connection, transaction);
                    break;
                case 3:
                    CreateProposalWorkflowTables(connection, transaction);
                    break;
                case 4:
                    CreateJobTables(connection, transaction);
                    break;
                default:
                    throw new NotSupportedException($"Motif schema {schema} is not known to this worker.");
            }

            ValidateSchema(connection, schema, transaction);
            afterMigrationStep?.Invoke(schema);
            SetUserVersion(connection, transaction, schema);
        }

        if (currentSchema > 0 && targetSchema > currentSchema)
        {
            var required = MinimumWorkerVersion(targetSchema);
            var existing = ReadMetadata(connection, transaction).MinimumWorkerVersion;
            if (required > existing)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE MotifMetadata SET MinimumWorkerVersion = $version WHERE Id = 1;";
                update.Parameters.AddWithValue("$version", required.ToString());
                update.ExecuteNonQuery();
            }
        }
    }

    internal static void ValidateSchema(
        SqliteConnection connection,
        int schema,
        SqliteTransaction? transaction = null)
    {
        var expectedTables = schema switch
        {
            1 => new HashSet<string>(StringComparer.Ordinal) { "MotifMetadata" },
            2 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins"
            },
            3 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions", "Drafts",
                "Decisions", "Receipts", "Reports", "AppliedIndex", "MigrationLedger"
            },
            4 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions", "Drafts",
                "Decisions", "Receipts", "Reports", "AppliedIndex", "MigrationLedger", "Jobs"
            },
            _ => throw new NotSupportedException($"Motif schema {schema} is not known to this worker.")
        };
        var expectedIndexes = schema >= 2
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                "IX_AssessedWords_Assessment", "IX_AssessedWords_Word", "IX_ParsedAnalyses_Word"
            }
            : [];
        if (schema >= 4)
        {
            expectedIndexes.Add("IX_Jobs_Lineage_Attempt");
            expectedIndexes.Add("IX_Jobs_Status_Updated");
        }

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
                throw new InvalidDataException($"Motif schema {schema} contains unexpected {type} {name}.");
            }
        }

        foreach (var table in expectedTables)
            ValidateTable(connection, transaction, table, ColumnsFor(table), ForeignKeysFor(table));
        foreach (var index in expectedIndexes)
            ValidateIndex(connection, transaction, index, IndexColumnsFor(index));
    }

    internal static bool IsCorruption(SqliteException exception) => IsCorruptionCode(exception.SqliteErrorCode);

    internal static bool IsCorruptionCode(int errorCode) => errorCode is 11 or 26;

    internal static void EnsureLegacyTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CorpusAndAssessmentDdl;
        command.ExecuteNonQuery();
    }

    internal static bool HasUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'index', 'view', 'trigger') AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
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
        catch (SqliteException exception) when (IsCorruption(exception))
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

    private static void CreateMetadata(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectLocator project)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MotifMetadata (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                FullFwDataPath TEXT NOT NULL,
                FieldWorksProjectIdentity TEXT NOT NULL,
                MinimumWorkerVersion TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            INSERT INTO MotifMetadata
                (Id, FullFwDataPath, FieldWorksProjectIdentity, MinimumWorkerVersion, CreatedUtc)
            VALUES (1, $path, $identity, $version, $created);
            """;
        command.Parameters.AddWithValue("$path", project.FullFwDataPath);
        command.Parameters.AddWithValue("$identity", project.FieldWorksProjectIdentity);
        command.Parameters.AddWithValue("$version", MinimumWorkerVersion(1).ToString());
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void CreateCorpusAndAssessmentTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ValidateExistingTable(connection, transaction, "Corpora", "CorpusId", "ProvenanceJson");
        ValidateExistingTable(connection, transaction, "Assessments", "AssessmentId", "CorpusId", "CorpusWordsJson");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CorpusAndAssessmentDdl;
        command.ExecuteNonQuery();
    }

    private static void CreateProposalWorkflowTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProposalWorkflowDdl;
        command.ExecuteNonQuery();
    }

    private static void CreateJobTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = JobDdl;
        command.ExecuteNonQuery();
    }

    private static void ValidateTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ColumnShape> expectedColumns,
        IReadOnlyList<ForeignKeyShape> expectedForeignKeys)
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

        if (actual.Count != expectedColumns.Count ||
            actual.Where((column, index) => !column.Matches(expectedColumns[index])).Any())
            throw new InvalidDataException($"Motif table {table} does not match its registered schema.");

        ValidateForeignKeys(connection, transaction, table, expectedForeignKeys);
        ValidateTableInvariant(connection, transaction, table);
    }

    private static void ValidateTableInvariant(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        var requiredSql = table switch
        {
            "MotifMetadata" => "CHECK (Id = 1)",
            "AssessedWords" => "AUTOINCREMENT",
            "Jobs" => null,
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
        "IX_Jobs_Lineage_Attempt" or "IX_Jobs_Status_Updated" => "Jobs",
        _ => throw new InvalidDataException($"Motif index {index} is not registered.")
    };

    private static bool IsUniqueIndex(string index) => index == "IX_Jobs_Lineage_Attempt";

    private static IReadOnlyList<string> IndexColumnsFor(string index) => index switch
    {
        "IX_AssessedWords_Assessment" => ["AssessmentId"],
        "IX_AssessedWords_Word" => ["AssessmentId", "Word"],
        "IX_ParsedAnalyses_Word" => ["AssessedWordId"],
        "IX_Jobs_Lineage_Attempt" => ["LineageId", "Attempt"],
        "IX_Jobs_Status_Updated" => ["Status", "UpdatedUtc"],
        _ => throw new InvalidDataException($"Motif index {index} is not registered.")
    };

    private static IReadOnlyList<ForeignKeyShape> ForeignKeysFor(string table) => table switch
    {
        "CorpusDocuments" => [new("Corpora", "CorpusId", "CorpusId", "NO ACTION", "NO ACTION", "NONE")],
        "AssessedWords" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ParsedAnalyses" => [new("AssessedWords", "AssessedWordId", "AssessedWordId", "NO ACTION", "NO ACTION", "NONE")],
        "AssessmentPins" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ProposalRevisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Decisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Receipts" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Reports" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE"),
            new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "AppliedIndex" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Jobs" => [],
        _ => []
    };

    private static IReadOnlyList<ColumnShape> ColumnsFor(string table) => table switch
    {
        "MotifMetadata" =>
        [C("Id", "INTEGER", false, 1), C("FullFwDataPath", "TEXT", true),
            C("FieldWorksProjectIdentity", "TEXT", true), C("MinimumWorkerVersion", "TEXT", true),
            C("CreatedUtc", "TEXT", true)],
        "Corpora" => [C("CorpusId", "TEXT", false, 1), C("ProvenanceJson", "TEXT", true)],
        "CorpusDocuments" =>
        [C("CorpusId", "TEXT", true, 1), C("DocumentId", "TEXT", true, 2), C("OrdinalIndex", "INTEGER", true),
            C("Title", "TEXT", true), C("Source", "TEXT", true), C("Text", "TEXT", true),
            C("ContentSha256", "TEXT", true), C("IngestedUtc", "TEXT", true), C("Licence", "TEXT"),
            C("CapabilitiesJson", "TEXT"), C("AttributesJson", "TEXT")],
        "Assessments" =>
        [C("AssessmentId", "TEXT", false, 1), C("CorpusId", "TEXT", true), C("CorpusWordsJson", "TEXT", true),
            C("CorpusSha256", "TEXT", true), C("CorpusProvenanceJson", "TEXT"), C("OutcomeDigest", "TEXT", true),
            C("SemanticDigest", "TEXT", true), C("GrammarSourceSha256", "TEXT", true),
            C("ModelFingerprint", "TEXT", true), C("Pipeline", "TEXT", true), C("DiagnosticCount", "INTEGER", true),
            C("SavedUtc", "TEXT", true)],
        "AssessedWords" =>
        [C("AssessedWordId", "INTEGER", false, 1), C("AssessmentId", "TEXT", true), C("OrdinalIndex", "INTEGER", true),
            C("Word", "TEXT", true), C("Outcome", "TEXT", true)],
        "ParsedAnalyses" =>
        [C("AssessedWordId", "INTEGER", true), C("OrdinalIndex", "INTEGER", true), C("CategoryGuid", "TEXT"),
            C("MorphemeGuidsJson", "TEXT", true), C("RootIndex", "INTEGER", true), C("IdentityDigest", "TEXT", true)],
        "AssessmentPins" =>
        [C("AssessmentId", "TEXT", true, 1), C("PinnedBy", "TEXT", true, 2), C("PinnedUtc", "TEXT", true)],
        "Proposals" =>
        [C("ProposalId", "TEXT", false, 1), C("CurrentIntentDigest", "TEXT", true),
            C("Status", "TEXT", true), C("Label", "TEXT"),
            C("Comment", "TEXT"), C("SupersededBy", "TEXT"), C("AnchorJson", "TEXT")],
        "ProposalRevisions" =>
        [C("ProposalId", "TEXT", true, 1), C("IntentDigest", "TEXT", true, 2),
            C("ProposalJson", "BLOB", true), C("CreatedUtc", "TEXT", true)],
        "Drafts" =>
        [C("DraftName", "TEXT", false, 1), C("ProposalId", "TEXT", true),
            C("DraftJson", "TEXT", true)],
        "Decisions" =>
        [C("ProposalId", "TEXT", true, 1), C("IntentDigest", "TEXT", true, 2),
            C("Outcome", "TEXT", true), C("ActorType", "TEXT", true), C("ActorId", "TEXT", true),
            C("Comment", "TEXT"), C("TimestampUtc", "TEXT", true)],
        "Receipts" =>
        [C("ReceiptId", "TEXT", false, 1), C("ProposalId", "TEXT", true),
            C("IntentDigest", "TEXT", true), C("ReceiptJson", "TEXT", true), C("RecordedUtc", "TEXT", true)],
        "Reports" =>
        [C("ReportId", "TEXT", false, 1), C("ProposalId", "TEXT"), C("AssessmentId", "TEXT"),
            C("ReportJson", "TEXT", true), C("EvidenceJson", "TEXT"), C("CreatedUtc", "TEXT", true)],
        "AppliedIndex" =>
        [C("ProposalId", "TEXT", false, 1), C("IntentDigest", "TEXT", true),
            C("AppliedUtc", "TEXT", true), C("RecordJson", "TEXT")],
        "MigrationLedger" =>
        [C("SourceKind", "TEXT", true, 1), C("SourcePath", "TEXT", true, 2),
            C("SourceDigest", "TEXT", true, 3), C("ImportedUtc", "TEXT", true)],
        "Jobs" =>
        [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true),
            C("Status", "TEXT", true), C("Attempt", "INTEGER", true, defaultValue: "1"),
            C("LineageId", "TEXT", true), C("InputJson", "TEXT", true), C("ResultJson", "TEXT"),
            C("ProgressJson", "TEXT"), C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
            C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true),
            C("Version", "INTEGER", true, defaultValue: "0"), C("DryRunPublished", "INTEGER", true, defaultValue: "0")],
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

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private const string CorpusAndAssessmentDdl = """
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

    private const string ProposalWorkflowDdl = """
        CREATE TABLE IF NOT EXISTS Proposals (
            ProposalId TEXT PRIMARY KEY,
            CurrentIntentDigest TEXT NOT NULL,
            Status TEXT NOT NULL,
            Label TEXT NULL,
            Comment TEXT NULL,
            SupersededBy TEXT NULL,
            AnchorJson TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ProposalRevisions (
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            ProposalJson BLOB NOT NULL,
            CreatedUtc TEXT NOT NULL,
            PRIMARY KEY (ProposalId, IntentDigest)
        );

        CREATE TABLE IF NOT EXISTS Drafts (
            DraftName TEXT PRIMARY KEY,
            ProposalId TEXT NOT NULL,
            DraftJson TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Decisions (
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            ActorType TEXT NOT NULL,
            ActorId TEXT NOT NULL,
            Comment TEXT NULL,
            TimestampUtc TEXT NOT NULL,
            PRIMARY KEY (ProposalId, IntentDigest)
        );

        CREATE TABLE IF NOT EXISTS Receipts (
            ReceiptId TEXT PRIMARY KEY,
            ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            ReceiptJson TEXT NOT NULL,
            RecordedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Reports (
            ReportId TEXT PRIMARY KEY,
            ProposalId TEXT NULL REFERENCES Proposals(ProposalId),
            AssessmentId TEXT NULL REFERENCES Assessments(AssessmentId),
            ReportJson TEXT NOT NULL,
            EvidenceJson TEXT NULL,
            CreatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS AppliedIndex (
            ProposalId TEXT PRIMARY KEY REFERENCES Proposals(ProposalId),
            IntentDigest TEXT NOT NULL,
            AppliedUtc TEXT NOT NULL,
            RecordJson TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS MigrationLedger (
            SourceKind TEXT NOT NULL,
            SourcePath TEXT NOT NULL,
            SourceDigest TEXT NOT NULL,
            ImportedUtc TEXT NOT NULL,
            PRIMARY KEY (SourceKind, SourcePath, SourceDigest)
        );
        """;

    private const string JobDdl = """
        CREATE TABLE IF NOT EXISTS Jobs (
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
            DryRunPublished INTEGER NOT NULL DEFAULT 0 CHECK (DryRunPublished IN (0, 1))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Jobs_Lineage_Attempt ON Jobs(LineageId, Attempt);
        CREATE INDEX IF NOT EXISTS IX_Jobs_Status_Updated ON Jobs(Status, UpdatedUtc);
        """;
}
