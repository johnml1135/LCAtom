using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>Owns the ordered SQLite schema migrations for Motif's machine store.</summary>
/// <remarks>
/// The machine store is a second, unrelated database: it holds <c>KnownProjects</c> and <c>Usage</c>, and
/// nothing about any one project. Its schema generation, DDL and <see cref="ApplicationId"/> are entirely
/// separate from <see cref="MotifSchema"/>'s so the two files can never be mistaken for one another,
/// pinned by <c>OpeningAProjectDatabaseAsAMachineDatabaseIsRefused</c> and
/// <c>OpeningAMachineDatabaseAsAProjectDatabaseIsRefused</c>.
/// </remarks>
public static class MachineSchema
{
    /// <summary>SQLite application identifier written to machine-store databases: ASCII "MACH".</summary>
    public const int ApplicationId = 0x4D414348;

    /// <summary>The newest ordered schema generation implemented by this assembly.</summary>
    public const int CurrentSchema = 1;

    internal static void Migrate(SqliteConnection connection, SqliteTransaction? transaction, int currentSchema, int targetSchema)
    {
        for (var schema = currentSchema + 1; schema <= targetSchema; schema++)
        {
            switch (schema)
            {
                case 1:
                    CreateGenerationOneTables(connection, transaction);
                    break;
                default:
                    throw new NotSupportedException($"Machine schema {schema} is not known to this worker.");
            }

            ValidateSchema(connection, schema, transaction);
            SetUserVersion(connection, transaction, schema);
        }
    }

    internal static void ValidateSchema(SqliteConnection connection, int schema, SqliteTransaction? transaction = null)
    {
        var expectedTables = schema switch
        {
            1 => new HashSet<string>(StringComparer.Ordinal) { "KnownProjects", "Usage" },
            _ => throw new NotSupportedException($"Machine schema {schema} is not known to this worker.")
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
                if (type == "table" && expectedTables.Contains(name)) continue;
                throw new InvalidDataException($"Machine schema {schema} contains unexpected {type} {name}.");
            }
        }

        foreach (var table in expectedTables)
            ValidateTable(connection, transaction, table, ColumnsFor(table));
    }

    private static void CreateGenerationOneTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = GenerationOneDdl;
        command.ExecuteNonQuery();
    }

    private static void ValidateTable(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<ColumnShape> expectedColumns)
    {
        var actual = ReadColumns(connection, transaction, table);
        if (!MatchesColumns(actual, expectedColumns))
            throw new InvalidDataException($"Machine table {table} does not match its registered schema.");
    }

    private static List<ColumnShape> ReadColumns(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var actual = new List<ColumnShape>();
        while (reader.Read())
            actual.Add(new ColumnShape(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
        return actual;
    }

    private static bool MatchesColumns(IReadOnlyList<ColumnShape> actual, IReadOnlyList<ColumnShape> expected) =>
        actual.Count == expected.Count && !actual.Where((column, index) => !column.Matches(expected[index])).Any();

    private static IReadOnlyList<ColumnShape> ColumnsFor(string table) => table switch
    {
        "KnownProjects" =>
        [C("WorkspaceKey", "TEXT", false, 1), C("FullFwDataPath", "TEXT", true), C("LastSeenUtc", "TEXT", true)],
        "Usage" =>
        [C("Id", "INTEGER", false, 1), C("TimestampUtc", "TEXT", true), C("Command", "TEXT", true),
            C("ArgumentShapeJson", "TEXT", true)],
        _ => throw new InvalidDataException($"Machine table {table} is not registered.")
    };

    private static ColumnShape C(string name, string type, bool notNull = false, int primaryKey = 0) =>
        new(name, type, notNull, null, primaryKey);

    private sealed record ColumnShape(string Name, string Type, bool NotNull, string? DefaultValue, int PrimaryKey)
    {
        public bool Matches(ColumnShape expected) =>
            StringComparer.OrdinalIgnoreCase.Equals(Name, expected.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(Type, expected.Type) &&
            NotNull == expected.NotNull &&
            DefaultValue == expected.DefaultValue &&
            PrimaryKey == expected.PrimaryKey;
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private const string GenerationOneDdl = """
        CREATE TABLE IF NOT EXISTS KnownProjects (
            WorkspaceKey TEXT PRIMARY KEY,
            FullFwDataPath TEXT NOT NULL,
            LastSeenUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Usage (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc TEXT NOT NULL,
            Command TEXT NOT NULL,
            ArgumentShapeJson TEXT NOT NULL
        );
        """;
}
