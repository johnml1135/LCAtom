using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>Owns the single, current SQLite schema for Motif's machine store.</summary>
/// <remarks>
/// The machine store is a second, unrelated database: it holds <c>KnownProjects</c> and <c>Usage</c>, and
/// nothing about any one project. Its schema, DDL and <see cref="ApplicationId"/> are entirely separate
/// from <see cref="MotifSchema"/>'s so the two files can never be mistaken for one another, pinned by
/// <c>OpeningAProjectDatabaseAsAMachineDatabaseIsRefused</c> and
/// <c>OpeningAMachineDatabaseAsAProjectDatabaseIsRefused</c>. Like <see cref="MotifSchema"/>, an existing
/// database at any other schema is refused rather than migrated.
/// </remarks>
public static class MachineSchema
{
    /// <summary>SQLite application identifier written to machine-store databases: ASCII "MACH".</summary>
    public const int ApplicationId = 0x4D414348;

    /// <summary>The schema this assembly creates and requires.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Builds every table this store needs, in one step.</summary>
    internal static void Create(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaDdl;
        command.ExecuteNonQuery();
    }

    internal static void ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var expectedTables = new HashSet<string>(StringComparer.Ordinal) { "KnownProjects", "Usage" };

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
                throw new InvalidDataException($"Machine schema contains unexpected {type} {name}.");
            }
        }

        foreach (var table in expectedTables)
            ValidateTable(connection, transaction, table, ColumnsFor(table));
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

    private const string SchemaDdl = """
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
