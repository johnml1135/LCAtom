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
    public const int CurrentSchema = 11;

    internal static Version MinimumWorkerVersion(int schema) => schema switch
    {
        1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 => new Version(1, 0),
        _ => throw new NotSupportedException($"Motif schema {schema} is not known to this worker.")
    };

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
                case 5:
                    RebuildJobsForGenerationFive(connection, transaction);
                    break;
                case 6:
                    AddRecoveryAndArchiveFacts(connection, transaction);
                    break;
                case 7:
                    CreateBaselineTable(connection, transaction);
                    break;
                case 8:
                    AddJobLeaseColumns(connection, transaction);
                    break;
                case 9:
                    MigrateToGenerationNine(connection, transaction);
                    break;
                case 10:
                    MigrateToGenerationTen(connection, transaction);
                    break;
                case 11:
                    MigrateToGenerationEleven(connection, transaction);
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
            4 or 5 or 6 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions", "Drafts",
                "Decisions", "Receipts", "Reports", "AppliedIndex", "MigrationLedger", "Jobs"
            },
            7 or 8 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions", "Drafts",
                "Decisions", "Receipts", "Reports", "AppliedIndex", "MigrationLedger", "Jobs", "Baselines"
            },
            9 or 10 or 11 => new HashSet<string>(StringComparer.Ordinal)
            {
                "MotifMetadata", "Corpora", "CorpusDocuments", "Assessments", "AssessedWords",
                "ParsedAnalyses", "AssessmentPins", "Proposals", "ProposalRevisions",
                "Decisions", "Receipts", "Reports", "AppliedIndex", "Jobs", "Baselines"
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
        if (schema >= 8) expectedIndexes.Add("IX_Jobs_Lease");
        if (schema >= 9)
        {
            expectedIndexes.Add("IX_Jobs_QueueOrder");
            expectedIndexes.Add("IX_Proposals_DraftName");
        }
        if (schema >= 10)
        {
            expectedIndexes.Add("IX_Assessments_Proposal");
            expectedIndexes.Add("IX_Assessments_Kind");
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
        {
            if (table == "Jobs" && schema == 4)
                ValidateGenerationFourJobs(connection, transaction);
            else
                ValidateTable(connection, transaction, table, ColumnsFor(table, schema), ForeignKeysFor(table, schema));
        }
        foreach (var index in expectedIndexes)
            ValidateIndex(connection, transaction, index, IndexColumnsFor(index));
    }

    internal static void EnsureLegacyTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = StoreOnlyDdl;
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

    private static void RebuildJobsForGenerationFive(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DROP INDEX IF EXISTS IX_Jobs_Lineage_Attempt; " +
            "DROP INDEX IF EXISTS IX_Jobs_Status_Updated; " +
            "ALTER TABLE Jobs RENAME TO Jobs_GenerationFour; " + CanonicalJobDdl;
        command.ExecuteNonQuery();

        var hasDryRun = HasColumn(connection, transaction, "Jobs_GenerationFour", "DryRunJson");
        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = hasDryRun
            ? "INSERT INTO Jobs (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, " +
              "ProgressJson, CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, DryRunJson) " +
              "SELECT JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson, " +
              "CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, DryRunJson FROM Jobs_GenerationFour;"
            : "INSERT INTO Jobs (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, " +
              "ProgressJson, CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, DryRunJson) " +
              "SELECT JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson, " +
              "CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, " +
              "CASE WHEN DryRunPublished = 1 THEN ProgressJson ELSE NULL END FROM Jobs_GenerationFour;";
        copy.ExecuteNonQuery();
        using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DROP TABLE Jobs_GenerationFour;";
        drop.ExecuteNonQuery();
    }

    // Additive and nullable: an existing row stays valid with all four null, meaning nobody has claimed it.
    private static void AddJobLeaseColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE Jobs ADD COLUMN OwnerId TEXT NULL; " +
            "ALTER TABLE Jobs ADD COLUMN ClaimToken TEXT NULL; " +
            "ALTER TABLE Jobs ADD COLUMN LeaseUntilUtc TEXT NULL; " +
            "ALTER TABLE Jobs ADD COLUMN HeartbeatUtc TEXT NULL; " +
            "CREATE INDEX IF NOT EXISTS IX_Jobs_Lease ON Jobs(Status, LeaseUntilUtc);";
        command.ExecuteNonQuery();
    }

    private static void MigrateToGenerationNine(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (var drop = connection.CreateCommand())
        {
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE Drafts; DROP TABLE MigrationLedger;";
            drop.ExecuteNonQuery();
        }
        RebuildProposalsForGenerationNine(connection, transaction);
        RebuildJobsForGenerationNine(connection, transaction);
    }

    /// A referenced table can't be dropped, so its four FK-bearing children are rebuilt onto the new one first.
    private static void RebuildProposalsForGenerationNine(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                ALTER TABLE Proposals RENAME TO Proposals_GenerationEight;
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
                INSERT INTO Proposals
                    (ProposalId, CurrentIntentDigest, Status, Label, Comment, SupersededBy, AnchorJson, ArchivedUtc)
                SELECT ProposalId, CurrentIntentDigest, Status, Label, Comment, SupersededBy, AnchorJson, ArchivedUtc
                    FROM Proposals_GenerationEight;
                """;
            command.ExecuteNonQuery();
        }

        RebuildProposalChildForGenerationNine(connection, transaction, "ProposalRevisions",
            "ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId), IntentDigest TEXT NOT NULL, " +
            "ProposalJson BLOB NOT NULL, CreatedUtc TEXT NOT NULL, PRIMARY KEY (ProposalId, IntentDigest)");
        RebuildProposalChildForGenerationNine(connection, transaction, "Decisions",
            "ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId), IntentDigest TEXT NOT NULL, " +
            "Outcome TEXT NOT NULL, ActorType TEXT NOT NULL, ActorId TEXT NOT NULL, Comment TEXT NULL, " +
            "TimestampUtc TEXT NOT NULL, PRIMARY KEY (ProposalId, IntentDigest)");
        RebuildProposalChildForGenerationNine(connection, transaction, "Receipts",
            "ReceiptId TEXT PRIMARY KEY, ProposalId TEXT NOT NULL REFERENCES Proposals(ProposalId), " +
            "IntentDigest TEXT NOT NULL, ReceiptJson TEXT NOT NULL, RecordedUtc TEXT NOT NULL");
        RebuildProposalChildForGenerationNine(connection, transaction, "Reports",
            "ReportId TEXT PRIMARY KEY, ProposalId TEXT NULL REFERENCES Proposals(ProposalId), " +
            "AssessmentId TEXT NULL REFERENCES Assessments(AssessmentId), ReportJson TEXT NOT NULL, " +
            "EvidenceJson TEXT NULL, CreatedUtc TEXT NOT NULL");

        using var dropOld = connection.CreateCommand();
        dropOld.Transaction = transaction;
        dropOld.CommandText = "DROP TABLE Proposals_GenerationEight;";
        dropOld.ExecuteNonQuery();
    }

    /// One rename-recreate-copy-drop cycle, so the child ends up referencing the rebuilt Proposals by name.
    private static void RebuildProposalChildForGenerationNine(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string columnDefinitions)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"ALTER TABLE {table} RENAME TO {table}_GenerationEight; " +
            $"CREATE TABLE {table} ({columnDefinitions}); " +
            $"INSERT INTO {table} SELECT * FROM {table}_GenerationEight; " +
            $"DROP TABLE {table}_GenerationEight;";
        command.ExecuteNonQuery();
    }

    /// Backfills QueueOrder from UpdatedUtc as epoch milliseconds, preserving the claim order rows already had.
    private static void RebuildJobsForGenerationNine(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DROP INDEX IF EXISTS IX_Jobs_Lineage_Attempt; " +
                "DROP INDEX IF EXISTS IX_Jobs_Status_Updated; " +
                "DROP INDEX IF EXISTS IX_Jobs_Lease; " +
                "ALTER TABLE Jobs RENAME TO Jobs_GenerationEight; " + CanonicalJobDdlGenerationNine;
            command.ExecuteNonQuery();
        }

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO Jobs (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson,
                    ProgressJson, CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished,
                    DryRunJson, FailureCategory, NotBeforeUtc, ArchivedUtc, OwnerId, ClaimToken, LeaseUntilUtc,
                    HeartbeatUtc, QueueOrder)
                SELECT JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson,
                    ProgressJson, CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished,
                    DryRunJson, FailureCategory, NotBeforeUtc, ArchivedUtc, OwnerId, ClaimToken, LeaseUntilUtc,
                    HeartbeatUtc, (julianday(UpdatedUtc) - 2440587.5) * 86400000.0
                FROM Jobs_GenerationEight;
                """;
            copy.ExecuteNonQuery();
        }

        using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DROP TABLE Jobs_GenerationEight;";
        drop.ExecuteNonQuery();
    }

    // Rebuilds Assessments onto ADR 0042's shape and adds the project's current-Assessment pointer.
    private static void MigrateToGenerationTen(SqliteConnection connection, SqliteTransaction? transaction)
    {
        RequireNoAssessmentRows(connection, transaction);
        RenameAndRecreateAssessmentsForGenerationTen(connection, transaction);
        RebuildAssessedWordsForGenerationTen(connection, transaction);
        RebuildParsedAnalysesForGenerationTen(connection, transaction);
        RebuildLeafForGenerationTen(connection, transaction, "AssessmentPins",
            "AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId), PinnedBy TEXT NOT NULL, " +
            "PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy)",
            "AssessmentId, PinnedBy, PinnedUtc");
        RebuildLeafForGenerationTen(connection, transaction, "Reports",
            "ReportId TEXT PRIMARY KEY, ProposalId TEXT NULL REFERENCES Proposals(ProposalId), " +
            "AssessmentId TEXT NULL REFERENCES Assessments(AssessmentId), ReportJson TEXT NOT NULL, " +
            "EvidenceJson TEXT NULL, CreatedUtc TEXT NOT NULL, Kind TEXT NULL, RenderedText TEXT NULL",
            "ReportId, ProposalId, AssessmentId, ReportJson, EvidenceJson, CreatedUtc");
        using var dropOldAssessments = connection.CreateCommand();
        dropOldAssessments.Transaction = transaction;
        dropOldAssessments.CommandText = "DROP TABLE Assessments_GenerationNine;";
        dropOldAssessments.ExecuteNonQuery();

        using var pointer = connection.CreateCommand();
        pointer.Transaction = transaction;
        pointer.CommandText = "ALTER TABLE MotifMetadata ADD COLUMN CurrentAssessmentId TEXT NULL;";
        pointer.ExecuteNonQuery();
    }

    // There is no way to assign Assessor, Kind, or scope values to a row this migration did not write.
    private static void RequireNoAssessmentRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Assessments;";
        var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count != 0)
            throw new InvalidDataException(
                $"Motif schema migration to generation 10 found {count} existing Assessments row(s); none " +
                "were ever expected, and their Assessor, Kind, and Assessment scope cannot be recovered.");
    }

    // Not dropped yet: AssessedWords, AssessmentPins, and Reports still reference it until they are rebuilt.
    private static void RenameAndRecreateAssessmentsForGenerationTen(
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE Assessments RENAME TO Assessments_GenerationNine;
            CREATE TABLE Assessments (
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
                SavedUtc TEXT NOT NULL,
                ProposalId TEXT NULL REFERENCES Proposals(ProposalId),
                ProposalIntentDigest TEXT NULL,
                Assessor TEXT NOT NULL,
                Kind TEXT NOT NULL,
                ScopeJson TEXT NOT NULL,
                ScopeDigest TEXT NOT NULL,
                TokeniserName TEXT NOT NULL,
                TokeniserVersion TEXT NOT NULL,
                BaselineToken TEXT NOT NULL
            );
            CREATE INDEX IX_Assessments_Proposal ON Assessments(ProposalId);
            CREATE INDEX IX_Assessments_Kind ON Assessments(Kind);
            INSERT INTO Assessments (AssessmentId, CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson,
                OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline, DiagnosticCount,
                SavedUtc)
            SELECT AssessmentId, CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson,
                OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline, DiagnosticCount,
                SavedUtc
            FROM Assessments_GenerationNine;
            """;
        command.ExecuteNonQuery();
    }

    // Renamed but not dropped: ParsedAnalyses still references it, and is rebuilt next.
    private static void RebuildAssessedWordsForGenerationTen(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS IX_AssessedWords_Assessment;
            DROP INDEX IF EXISTS IX_AssessedWords_Word;
            ALTER TABLE AssessedWords RENAME TO AssessedWords_GenerationNine;
            CREATE TABLE AssessedWords (
                AssessedWordId INTEGER PRIMARY KEY AUTOINCREMENT,
                AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
                OrdinalIndex INTEGER NOT NULL,
                Word TEXT NOT NULL,
                Outcome TEXT NOT NULL
            );
            CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(AssessmentId);
            CREATE INDEX IX_AssessedWords_Word ON AssessedWords(AssessmentId, Word);
            INSERT INTO AssessedWords SELECT * FROM AssessedWords_GenerationNine;
            """;
        command.ExecuteNonQuery();
    }

    // Drops the old ParsedAnalyses and AssessedWords: the latter is unreferenced only once this rebuild runs.
    private static void RebuildParsedAnalysesForGenerationTen(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS IX_ParsedAnalyses_Word;
            ALTER TABLE ParsedAnalyses RENAME TO ParsedAnalyses_GenerationNine;
            CREATE TABLE ParsedAnalyses (
                AssessedWordId INTEGER NOT NULL REFERENCES AssessedWords(AssessedWordId),
                OrdinalIndex INTEGER NOT NULL,
                CategoryGuid TEXT NULL,
                MorphemeGuidsJson TEXT NOT NULL,
                RootIndex INTEGER NOT NULL,
                IdentityDigest TEXT NOT NULL
            );
            CREATE INDEX IX_ParsedAnalyses_Word ON ParsedAnalyses(AssessedWordId);
            INSERT INTO ParsedAnalyses SELECT * FROM ParsedAnalyses_GenerationNine;
            DROP TABLE ParsedAnalyses_GenerationNine;
            DROP TABLE AssessedWords_GenerationNine;
            """;
        command.ExecuteNonQuery();
    }

    /// One rename-recreate-copy-drop cycle, safe only for a table nothing else declares a foreign key onto.
    private static void RebuildLeafForGenerationTen(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string columnDefinitions,
        string copyColumns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"ALTER TABLE {table} RENAME TO {table}_GenerationNine; " +
            $"CREATE TABLE {table} ({columnDefinitions}); " +
            $"INSERT INTO {table} ({copyColumns}) SELECT {copyColumns} FROM {table}_GenerationNine; " +
            $"DROP TABLE {table}_GenerationNine;";
        command.ExecuteNonQuery();
    }

    // Renames the Corpus*-named columns to Selection* and adds a cache path and digest; preserves existing rows.
    private static void MigrateToGenerationEleven(SqliteConnection connection, SqliteTransaction? transaction)
    {
        RenameAndRecreateAssessmentsForGenerationEleven(connection, transaction);
        RebuildAssessedWordsForGenerationEleven(connection, transaction);
        RebuildParsedAnalysesForGenerationEleven(connection, transaction);
        RebuildLeafForGenerationEleven(connection, transaction, "AssessmentPins",
            "AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId), PinnedBy TEXT NOT NULL, " +
            "PinnedUtc TEXT NOT NULL, PRIMARY KEY (AssessmentId, PinnedBy)",
            "AssessmentId, PinnedBy, PinnedUtc");
        RebuildLeafForGenerationEleven(connection, transaction, "Reports",
            "ReportId TEXT PRIMARY KEY, ProposalId TEXT NULL REFERENCES Proposals(ProposalId), " +
            "AssessmentId TEXT NULL REFERENCES Assessments(AssessmentId), ReportJson TEXT NOT NULL, " +
            "EvidenceJson TEXT NULL, CreatedUtc TEXT NOT NULL, Kind TEXT NULL, RenderedText TEXT NULL",
            "ReportId, ProposalId, AssessmentId, ReportJson, EvidenceJson, CreatedUtc, Kind, RenderedText");
        using var dropOldAssessments = connection.CreateCommand();
        dropOldAssessments.Transaction = transaction;
        dropOldAssessments.CommandText = "DROP TABLE Assessments_GenerationTen;";
        dropOldAssessments.ExecuteNonQuery();
    }

    // Not dropped yet: AssessedWords, AssessmentPins, and Reports still reference it until they are rebuilt.
    private static void RenameAndRecreateAssessmentsForGenerationEleven(
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS IX_Assessments_Proposal;
            DROP INDEX IF EXISTS IX_Assessments_Kind;
            ALTER TABLE Assessments RENAME TO Assessments_GenerationTen;
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
            INSERT INTO Assessments (AssessmentId, SelectionName, SelectionWordsJson, SelectionSha256,
                SelectionProvenanceJson, OutcomeDigest, SemanticDigest, GrammarSourceSha256, ModelFingerprint,
                Pipeline, DiagnosticCount, SavedUtc, ProposalId, ProposalIntentDigest, Assessor, Kind, ScopeJson,
                ScopeDigest, TokeniserName, TokeniserVersion, BaselineToken)
            SELECT AssessmentId, CorpusId, CorpusWordsJson, CorpusSha256, CorpusProvenanceJson, OutcomeDigest,
                SemanticDigest, GrammarSourceSha256, ModelFingerprint, Pipeline, DiagnosticCount, SavedUtc,
                ProposalId, ProposalIntentDigest, Assessor, Kind, ScopeJson, ScopeDigest, TokeniserName,
                TokeniserVersion, BaselineToken
            FROM Assessments_GenerationTen;
            """;
        command.ExecuteNonQuery();
    }

    // Renamed but not dropped: ParsedAnalyses still references it, and is rebuilt next.
    private static void RebuildAssessedWordsForGenerationEleven(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS IX_AssessedWords_Assessment;
            DROP INDEX IF EXISTS IX_AssessedWords_Word;
            ALTER TABLE AssessedWords RENAME TO AssessedWords_GenerationTen;
            CREATE TABLE AssessedWords (
                AssessedWordId INTEGER PRIMARY KEY AUTOINCREMENT,
                AssessmentId TEXT NOT NULL REFERENCES Assessments(AssessmentId),
                OrdinalIndex INTEGER NOT NULL,
                Word TEXT NOT NULL,
                Outcome TEXT NOT NULL
            );
            CREATE INDEX IX_AssessedWords_Assessment ON AssessedWords(AssessmentId);
            CREATE INDEX IX_AssessedWords_Word ON AssessedWords(AssessmentId, Word);
            INSERT INTO AssessedWords SELECT * FROM AssessedWords_GenerationTen;
            """;
        command.ExecuteNonQuery();
    }

    // Drops the old ParsedAnalyses and AssessedWords: the latter is unreferenced only once this rebuild runs.
    private static void RebuildParsedAnalysesForGenerationEleven(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS IX_ParsedAnalyses_Word;
            ALTER TABLE ParsedAnalyses RENAME TO ParsedAnalyses_GenerationTen;
            CREATE TABLE ParsedAnalyses (
                AssessedWordId INTEGER NOT NULL REFERENCES AssessedWords(AssessedWordId),
                OrdinalIndex INTEGER NOT NULL,
                CategoryGuid TEXT NULL,
                MorphemeGuidsJson TEXT NOT NULL,
                RootIndex INTEGER NOT NULL,
                IdentityDigest TEXT NOT NULL
            );
            CREATE INDEX IX_ParsedAnalyses_Word ON ParsedAnalyses(AssessedWordId);
            INSERT INTO ParsedAnalyses SELECT * FROM ParsedAnalyses_GenerationTen;
            DROP TABLE ParsedAnalyses_GenerationTen;
            DROP TABLE AssessedWords_GenerationTen;
            """;
        command.ExecuteNonQuery();
    }

    /// One rename-recreate-copy-drop cycle, safe only for a table nothing else declares a foreign key onto.
    private static void RebuildLeafForGenerationEleven(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string columnDefinitions,
        string copyColumns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"ALTER TABLE {table} RENAME TO {table}_GenerationTen; " +
            $"CREATE TABLE {table} ({columnDefinitions}); " +
            $"INSERT INTO {table} ({copyColumns}) SELECT {copyColumns} FROM {table}_GenerationTen; " +
            $"DROP TABLE {table}_GenerationTen;";
        command.ExecuteNonQuery();
    }

    private static void AddRecoveryAndArchiveFacts(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE Jobs ADD COLUMN FailureCategory TEXT NOT NULL DEFAULT 'none'; " +
            "ALTER TABLE Jobs ADD COLUMN NotBeforeUtc TEXT NULL; " +
            "ALTER TABLE Jobs ADD COLUMN ArchivedUtc TEXT NULL; " +
            "ALTER TABLE Proposals ADD COLUMN ArchivedUtc TEXT NULL; " +
            "UPDATE Jobs SET ArchivedUtc = UpdatedUtc WHERE Status IN " +
            "('completed','completed-dry-run-only','completed-with-assessment-failure','failed','cancelled','interrupted'); " +
            "UPDATE Jobs SET FailureCategory = CASE " +
            "WHEN Status = 'cancelled' OR (Status = 'interrupted' AND CancellationRequested = 1) THEN 'cancellation' " +
            "WHEN Status = 'interrupted' THEN 'infrastructure' " +
            "WHEN Status = 'failed' THEN 'unknown' ELSE 'none' END; " +
            "UPDATE Proposals SET ArchivedUtc = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE Status IN " +
            "('applied','rejected','superseded','withdrawn') AND ArchivedUtc IS NULL; " +
            "ALTER TABLE AppliedIndex RENAME TO AppliedIndex_GenerationFive; " +
            "CREATE TABLE AppliedIndex (ProposalId TEXT PRIMARY KEY, IntentDigest TEXT NOT NULL, " +
            "AppliedUtc TEXT NOT NULL, RecordJson TEXT NULL); " +
            "INSERT INTO AppliedIndex SELECT ProposalId, IntentDigest, AppliedUtc, RecordJson FROM AppliedIndex_GenerationFive; " +
            "DROP TABLE AppliedIndex_GenerationFive;";
        command.ExecuteNonQuery();
    }

    private static void CreateBaselineTable(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BaselineDdl;
        command.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction? transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
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

    private static void ValidateGenerationFourJobs(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var actual = ReadColumns(connection, transaction, "Jobs");
        if (!MatchesColumns(actual, ColumnsFor("Jobs", 4)) && !MatchesColumns(actual, GenerationFourDryRunColumns()))
            throw new InvalidDataException("Motif generation-four Jobs table has an unknown shape.");

        ValidateForeignKeys(connection, transaction, "Jobs", []);
        ValidateTableInvariant(connection, transaction, "Jobs");
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

    private static IReadOnlyList<ForeignKeyShape> ForeignKeysFor(string table, int schema) => table switch
    {
        "CorpusDocuments" => [new("Corpora", "CorpusId", "CorpusId", "NO ACTION", "NO ACTION", "NONE")],
        "Assessments" => schema >= 10
            ? [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")]
            : [],
        "AssessedWords" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ParsedAnalyses" => [new("AssessedWords", "AssessedWordId", "AssessedWordId", "NO ACTION", "NO ACTION", "NONE")],
        "AssessmentPins" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE")],
        "ProposalRevisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Decisions" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Receipts" => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Reports" => [new("Assessments", "AssessmentId", "AssessmentId", "NO ACTION", "NO ACTION", "NONE"),
            new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "AppliedIndex" when schema < 6 => [new("Proposals", "ProposalId", "ProposalId", "NO ACTION", "NO ACTION", "NONE")],
        "Jobs" => [],
        "Baselines" => [],
        _ => []
    };

    private static IReadOnlyList<ColumnShape> ColumnsFor(string table, int schema) => table switch
    {
        "MotifMetadata" => schema >= 10
        ? [C("Id", "INTEGER", false, 1), C("FullFwDataPath", "TEXT", true),
            C("FieldWorksProjectIdentity", "TEXT", true), C("MinimumWorkerVersion", "TEXT", true),
            C("CreatedUtc", "TEXT", true), C("CurrentAssessmentId", "TEXT")]
        : [C("Id", "INTEGER", false, 1), C("FullFwDataPath", "TEXT", true),
            C("FieldWorksProjectIdentity", "TEXT", true), C("MinimumWorkerVersion", "TEXT", true),
            C("CreatedUtc", "TEXT", true)],
        "Corpora" => [C("CorpusId", "TEXT", false, 1), C("ProvenanceJson", "TEXT", true)],
        "CorpusDocuments" =>
        [C("CorpusId", "TEXT", true, 1), C("DocumentId", "TEXT", true, 2), C("OrdinalIndex", "INTEGER", true),
            C("Title", "TEXT", true), C("Source", "TEXT", true), C("Text", "TEXT", true),
            C("ContentSha256", "TEXT", true), C("IngestedUtc", "TEXT", true), C("Licence", "TEXT"),
            C("CapabilitiesJson", "TEXT"), C("AttributesJson", "TEXT")],
        "Assessments" => AssessmentColumns(schema),
        "AssessedWords" =>
        [C("AssessedWordId", "INTEGER", false, 1), C("AssessmentId", "TEXT", true), C("OrdinalIndex", "INTEGER", true),
            C("Word", "TEXT", true), C("Outcome", "TEXT", true)],
        "ParsedAnalyses" =>
        [C("AssessedWordId", "INTEGER", true), C("OrdinalIndex", "INTEGER", true), C("CategoryGuid", "TEXT"),
            C("MorphemeGuidsJson", "TEXT", true), C("RootIndex", "INTEGER", true), C("IdentityDigest", "TEXT", true)],
        "AssessmentPins" =>
        [C("AssessmentId", "TEXT", true, 1), C("PinnedBy", "TEXT", true, 2), C("PinnedUtc", "TEXT", true)],
        "Proposals" => ProposalColumns(schema),
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
        "Reports" => schema >= 10
        ? [C("ReportId", "TEXT", false, 1), C("ProposalId", "TEXT"), C("AssessmentId", "TEXT"),
            C("ReportJson", "TEXT", true), C("EvidenceJson", "TEXT"), C("CreatedUtc", "TEXT", true),
            C("Kind", "TEXT"), C("RenderedText", "TEXT")]
        : [C("ReportId", "TEXT", false, 1), C("ProposalId", "TEXT"), C("AssessmentId", "TEXT"),
            C("ReportJson", "TEXT", true), C("EvidenceJson", "TEXT"), C("CreatedUtc", "TEXT", true)],
        "AppliedIndex" =>
        [C("ProposalId", "TEXT", false, 1), C("IntentDigest", "TEXT", true),
            C("AppliedUtc", "TEXT", true), C("RecordJson", "TEXT")],
        "MigrationLedger" =>
        [C("SourceKind", "TEXT", true, 1), C("SourcePath", "TEXT", true, 2),
            C("SourceDigest", "TEXT", true, 3), C("ImportedUtc", "TEXT", true)],
        "Jobs" => schema == 4
        ? [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true),
            C("Status", "TEXT", true), C("Attempt", "INTEGER", true, defaultValue: "1"),
            C("LineageId", "TEXT", true), C("InputJson", "TEXT", true), C("ResultJson", "TEXT"),
            C("ProgressJson", "TEXT"), C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
            C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true),
            C("Version", "INTEGER", true, defaultValue: "0"), C("DryRunPublished", "INTEGER", true, defaultValue: "0")]
        : JobsColumns(schema),
        "Baselines" =>
        [C("ProjectKey", "TEXT", false, 1), C("ProjectIdentity", "TEXT", true),
            C("SemanticSnapshotDigest", "TEXT", true), C("ProjectionVersion", "TEXT", true),
            C("CapturedUtc", "TEXT", true), C("BundleDigest", "TEXT", true),
            C("CapturedHostSessionId", "TEXT"), C("CapturedEditGeneration", "INTEGER"),
            C("RootDirectory", "TEXT", true), C("FwDataPath", "TEXT", true), C("PublishedUtc", "TEXT", true)],
        _ => throw new InvalidDataException($"Motif table {table} is not registered.")
    };

    private static IReadOnlyList<ColumnShape> AssessmentColumns(int schema) => schema switch
    {
        >= 11 =>
        [C("AssessmentId", "TEXT", false, 1), C("SelectionName", "TEXT", true), C("SelectionWordsJson", "TEXT", true),
            C("SelectionSha256", "TEXT", true), C("SelectionProvenanceJson", "TEXT"), C("OutcomeDigest", "TEXT", true),
            C("SemanticDigest", "TEXT", true), C("GrammarSourceSha256", "TEXT", true),
            C("ModelFingerprint", "TEXT", true), C("Pipeline", "TEXT", true), C("DiagnosticCount", "INTEGER", true),
            C("SavedUtc", "TEXT", true), C("ProposalId", "TEXT"), C("ProposalIntentDigest", "TEXT"),
            C("Assessor", "TEXT", true), C("Kind", "TEXT", true), C("ScopeJson", "TEXT", true),
            C("ScopeDigest", "TEXT", true), C("TokeniserName", "TEXT", true), C("TokeniserVersion", "TEXT", true),
            C("BaselineToken", "TEXT", true), C("CachePath", "TEXT"), C("CacheDigest", "TEXT")],
        10 =>
        [C("AssessmentId", "TEXT", false, 1), C("CorpusId", "TEXT", true), C("CorpusWordsJson", "TEXT", true),
            C("CorpusSha256", "TEXT", true), C("CorpusProvenanceJson", "TEXT"), C("OutcomeDigest", "TEXT", true),
            C("SemanticDigest", "TEXT", true), C("GrammarSourceSha256", "TEXT", true),
            C("ModelFingerprint", "TEXT", true), C("Pipeline", "TEXT", true), C("DiagnosticCount", "INTEGER", true),
            C("SavedUtc", "TEXT", true), C("ProposalId", "TEXT"), C("ProposalIntentDigest", "TEXT"),
            C("Assessor", "TEXT", true), C("Kind", "TEXT", true), C("ScopeJson", "TEXT", true),
            C("ScopeDigest", "TEXT", true), C("TokeniserName", "TEXT", true), C("TokeniserVersion", "TEXT", true),
            C("BaselineToken", "TEXT", true)],
        _ =>
        [C("AssessmentId", "TEXT", false, 1), C("CorpusId", "TEXT", true), C("CorpusWordsJson", "TEXT", true),
            C("CorpusSha256", "TEXT", true), C("CorpusProvenanceJson", "TEXT"), C("OutcomeDigest", "TEXT", true),
            C("SemanticDigest", "TEXT", true), C("GrammarSourceSha256", "TEXT", true),
            C("ModelFingerprint", "TEXT", true), C("Pipeline", "TEXT", true), C("DiagnosticCount", "INTEGER", true),
            C("SavedUtc", "TEXT", true)],
    };

    private static IReadOnlyList<ColumnShape> ProposalColumns(int schema) => schema switch
    {
        >= 9 =>
        [C("ProposalId", "TEXT", false, 1), C("CurrentIntentDigest", "TEXT"), C("Status", "TEXT", true),
            C("Label", "TEXT"), C("Comment", "TEXT"), C("SupersededBy", "TEXT"), C("AnchorJson", "TEXT"),
            C("ArchivedUtc", "TEXT"), C("DraftName", "TEXT"), C("DraftJson", "TEXT")],
        >= 6 =>
        [C("ProposalId", "TEXT", false, 1), C("CurrentIntentDigest", "TEXT", true), C("Status", "TEXT", true),
            C("Label", "TEXT"), C("Comment", "TEXT"), C("SupersededBy", "TEXT"), C("AnchorJson", "TEXT"), C("ArchivedUtc", "TEXT")],
        _ =>
        [C("ProposalId", "TEXT", false, 1), C("CurrentIntentDigest", "TEXT", true), C("Status", "TEXT", true),
            C("Label", "TEXT"), C("Comment", "TEXT"), C("SupersededBy", "TEXT"), C("AnchorJson", "TEXT")]
    };

    private static IReadOnlyList<ColumnShape> JobsColumns(int schema) => schema >= 6
        ? [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true), C("Status", "TEXT", true),
            C("Attempt", "INTEGER", true, defaultValue: "1"), C("LineageId", "TEXT", true), C("InputJson", "TEXT", true),
            C("ResultJson", "TEXT"), C("ProgressJson", "TEXT"), C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
            C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true), C("Version", "INTEGER", true, defaultValue: "0"),
            C("DryRunPublished", "INTEGER", true, defaultValue: "0"), C("DryRunJson", "TEXT"),
            C("FailureCategory", "TEXT", true, defaultValue: "'none'"), C("NotBeforeUtc", "TEXT"), C("ArchivedUtc", "TEXT"),
            .. schema >= 8
                ? new[] { C("OwnerId", "TEXT"), C("ClaimToken", "TEXT"), C("LeaseUntilUtc", "TEXT"),
                    C("HeartbeatUtc", "TEXT") }
                : [],
            .. schema >= 9
                ? new[] { C("QueueOrder", "REAL", true,
                    defaultValue: "CAST((julianday('now') - 2440587.5) * 86400000.0 AS REAL)") }
                : []]
        : [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true), C("Status", "TEXT", true),
            C("Attempt", "INTEGER", true, defaultValue: "1"), C("LineageId", "TEXT", true), C("InputJson", "TEXT", true),
            C("ResultJson", "TEXT"), C("ProgressJson", "TEXT"), C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
            C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true), C("Version", "INTEGER", true, defaultValue: "0"),
            C("DryRunPublished", "INTEGER", true, defaultValue: "0"), C("DryRunJson", "TEXT")];

    private static IReadOnlyList<ColumnShape> GenerationFourDryRunColumns() =>
    [C("JobId", "TEXT", false, 1), C("ProjectKey", "TEXT", true), C("Kind", "TEXT", true),
        C("Status", "TEXT", true), C("Attempt", "INTEGER", true, defaultValue: "1"),
        C("LineageId", "TEXT", true), C("InputJson", "TEXT", true), C("ResultJson", "TEXT"),
        C("ProgressJson", "TEXT"), C("DryRunJson", "TEXT"),
        C("CancellationRequested", "INTEGER", true, defaultValue: "0"),
        C("CreatedUtc", "TEXT", true), C("UpdatedUtc", "TEXT", true),
        C("Version", "INTEGER", true, defaultValue: "0"), C("DryRunPublished", "INTEGER", true, defaultValue: "0")];

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

    // Frozen: what generation 2 created. Generation 11 renames these columns, so this must not follow it.
    private const string AssessmentsDdlGenerationTwo = """
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

        """;

    // A database no worker has migrated still has to answer the column names generation 11 leaves behind.
    private const string AssessmentsDdlForStoreOnlyDatabases = """
        CREATE TABLE IF NOT EXISTS Assessments (
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
            SavedUtc TEXT NOT NULL
        );

        """;

    private const string AssessmentSupportDdl = """
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

    private const string CorpusAndAssessmentDdl =
        CorpusDdl + AssessmentsDdlGenerationTwo + AssessmentSupportDdl;

    private const string StoreOnlyDdl =
        CorpusDdl + AssessmentsDdlForStoreOnlyDatabases + AssessmentSupportDdl;
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

    private const string CanonicalJobDdl = """
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
            DryRunJson TEXT NULL
        );
        CREATE UNIQUE INDEX IX_Jobs_Lineage_Attempt ON Jobs(LineageId, Attempt);
        CREATE INDEX IX_Jobs_Status_Updated ON Jobs(Status, UpdatedUtc);
        """;

    private const string CanonicalJobDdlGenerationNine = """
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
