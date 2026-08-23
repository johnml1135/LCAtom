using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Host.Store;

/// <summary>Represents the one worker-owned connection boundary for a project's Motif database.</summary>
public sealed class MotifDatabase : IDisposable
{
    private readonly string _path;
    private readonly FileStream _ownership;
    private bool _disposed;

    private MotifDatabase(string path, FileStream ownership)
    {
        _path = path;
        _ownership = ownership;
    }

    /// <summary>
    /// Opens and owns a project database, applying only migrations this worker understands.
    /// </summary>
    /// <param name="path">The sibling Motif database path.</param>
    /// <param name="project">The project locator that must match persisted metadata.</param>
    /// <param name="supportedSchema">The highest schema generation this worker supports.</param>
    /// <param name="workerVersion">The worker version used for compatibility checks.</param>
    /// <returns>An owned database boundary whose connections are configured for worker use.</returns>
    /// <exception cref="InvalidDataException">The file identity, metadata, or project binding is invalid.</exception>
    /// <exception cref="NotSupportedException">The schema or worker compatibility is unsupported.</exception>
    public static MotifDatabase OpenOwned(
        string path,
        ProjectLocator project,
        int supportedSchema,
        Version workerVersion)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A database path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.FullFwDataPath))
            throw new ArgumentException("A full .fwdata path is required.", nameof(project));
        if (string.IsNullOrWhiteSpace(project.FieldWorksProjectIdentity))
            throw new ArgumentException("A FieldWorks project identity is required.", nameof(project));
        project = project with { FullFwDataPath = Path.GetFullPath(project.FullFwDataPath) };
        ArgumentNullException.ThrowIfNull(workerVersion);
        if (supportedSchema < 1) throw new ArgumentOutOfRangeException(nameof(supportedSchema));
        if (supportedSchema > MotifSchema.CurrentSchema)
            throw new NotSupportedException($"Motif schema {supportedSchema} is not known to this worker.");
        if (workerVersion < MotifSchema.MinimumWorkerVersion(supportedSchema))
            throw new NotSupportedException(
                $"Worker {workerVersion} is older than schema {supportedSchema} minimum " +
                $"{MotifSchema.MinimumWorkerVersion(supportedSchema)}.");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        FileStream? ownership = null;
        try
        {
            ownership = AcquireOwnership(fullPath);
            using var connection = OpenInspectionConnection(fullPath);
            var applicationId = PragmaInt(connection, "application_id");
            var schema = PragmaInt(connection, "user_version");
            if (applicationId != 0 && applicationId != MotifSchema.ApplicationId)
                throw new InvalidDataException("The file is not a Motif database.");
            if (schema < 0)
                throw new InvalidDataException("The Motif database has an invalid schema generation.");
            if (schema > supportedSchema)
                throw new NotSupportedException($"Motif schema {schema} is newer than supported schema {supportedSchema}.");

            var hasTables = MotifSchema.HasUserTables(connection);
            if (applicationId == 0 && schema != 0)
                throw new InvalidDataException("A Motif database with a schema must have its application id.");
            if (schema == 0 && hasTables)
                throw new InvalidDataException("The existing database has no registered Motif schema.");

            if (schema > 0)
            {
                var metadata = MotifSchema.ReadMetadata(connection);
                EnsureLocatorMatches(metadata.Project, project);
                if (workerVersion < metadata.MinimumWorkerVersion)
                    throw new NotSupportedException($"Worker {workerVersion} is older than database minimum {metadata.MinimumWorkerVersion}.");
            }

            Execute(connection, "BEGIN IMMEDIATE;");
            try
            {
                if (applicationId == 0) SetApplicationId(connection);
                MotifSchema.Migrate(connection, null, schema, supportedSchema, project);
                Execute(connection, "COMMIT;");
            }
            catch
            {
                Execute(connection, "ROLLBACK;");
                throw;
            }

            MotifSchema.EnableWal(connection);
            return new MotifDatabase(fullPath, ownership);
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    /// <summary>Opens a configured connection while this worker owns the database.</summary>
    public SqliteConnection OpenConnection()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MotifDatabase));
        return OpenConfiguredConnection(_path);
    }

    /// <summary>Releases ownership so another worker can open the database.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ownership.Dispose();
    }

    private static SqliteConnection OpenInspectionConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        MotifSchema.ConfigureSession(connection);
        return connection;
    }

    private static SqliteConnection OpenConfiguredConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        MotifSchema.ConfigureConnection(connection);
        return connection;
    }

    private static FileStream AcquireOwnership(string path)
    {
        var lockPath = path + ".owner.lock";
        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            try
            {
                stream.Lock(0, 1);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException exception)
        {
            throw new IOException("Another Motif worker owns this database.", exception);
        }
    }

    private static void EnsureLocatorMatches(ProjectLocator stored, ProjectLocator requested)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(stored.FullFwDataPath, requested.FullFwDataPath) ||
            !StringComparer.Ordinal.Equals(stored.FieldWorksProjectIdentity, requested.FieldWorksProjectIdentity))
            throw new InvalidDataException("The database is registered to a different project locator.");
    }

    private static void SetApplicationId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id = {MotifSchema.ApplicationId};";
        command.ExecuteNonQuery();
    }

    private static int PragmaInt(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
