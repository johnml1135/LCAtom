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
    public const int CurrentSchema = 2;

    /// <summary>The connection busy timeout used for short-lived worker database sessions.</summary>
    public const int BusyTimeoutMilliseconds = 15000;

    internal static Version MinimumWorkerVersion(int schema) => schema switch
    {
        1 or 2 => new Version(1, 0),
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
        ProjectLocator project)
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
                default:
                    throw new NotSupportedException($"Motif schema {schema} is not known to this worker.");
            }

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
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidOperationException or
            InvalidCastException or SqliteException)
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
}
