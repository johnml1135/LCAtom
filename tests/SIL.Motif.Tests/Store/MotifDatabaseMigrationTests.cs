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
