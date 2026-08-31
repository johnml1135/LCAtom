using System.Threading;
using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>
/// What one schema supplies so <see cref="MotifSqliteStore"/> can open, create and validate a database
/// whose shape it does not itself know.
/// </summary>
/// <remarks>
/// <see cref="ValidateSchema"/> and <see cref="Create"/> close over whatever else that schema needs —
/// <see cref="MotifSchema.Create"/> closes over the requesting <c>ProjectLocator</c>, for instance — so
/// this descriptor only has to carry the four things that are not already fixed at the call site: the
/// application id, the schema this build requires, schema validation and one-step creation, and an
/// optional extra check on an existing database's identity before it is handed back.
/// </remarks>
internal sealed class MotifSqliteStoreDescriptor
{
    /// <summary>What to call this database when refusing it, so a user with both can tell them apart.</summary>
    public required string Name { get; init; }

    /// <summary>The SQLite <c>application_id</c> this schema's databases are stamped with.</summary>
    public required int ApplicationId { get; init; }

    /// <summary>The schema this build requires: an empty file is created at it, anything else is refused.</summary>
    public required int CurrentSchema { get; init; }

    /// <summary>Validates that an existing database has exactly <see cref="CurrentSchema"/>'s shape.</summary>
    public required Action<SqliteConnection, SqliteTransaction?> ValidateSchema { get; init; }

    /// <summary>Builds every table, index and identity row a brand-new database needs, in one step.</summary>
    public required Action<SqliteConnection, SqliteTransaction?> Create { get; init; }

    /// <summary>
    /// Runs once schema validation passes on an existing database, before it is handed back. <see
    /// cref="MotifSchema"/> uses this to reject a database registered to a different project or too old a
    /// worker; <see cref="MachineSchema"/> has nothing to check here and leaves it null.
    /// </summary>
    public Action<SqliteConnection>? BeforeOpen { get; init; }
}

/// <summary>
/// The open-and-create ceremony and connection lifecycle shared by every database Motif owns — a
/// project's Motif database and the machine store alike. <see cref="MotifDatabase"/> and
/// <see cref="MachineDatabase"/> are thin wrappers around this: each supplies its own
/// <see cref="MotifSqliteStoreDescriptor"/> and keeps its own public factory and argument checks, and this
/// module does the rest identically for both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opening does not claim the database.</b> The ownership lock is taken only while a fresh database is
/// being created, not for the object's whole lifetime, because creation is the one operation two processes
/// must never interleave — everything else is ordinary concurrent SQLite that WAL and row versions already
/// make safe. A lock held for the whole lifetime would mean whichever process opened first excluded every
/// other one entirely, which is exactly what lets a <c>motif</c> invocation and the job runner (or two
/// invocations of either) stay open at once.
/// </para>
/// <para>
/// Creation itself runs inside <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c>, with an explicit <c>ROLLBACK</c> on
/// failure, so a process that dies mid-creation leaves the file empty rather than half built — the next
/// opener sees an ordinary, uncreated database and tries again. An existing database is never migrated:
/// it is either exactly <see cref="MotifSqliteStoreDescriptor.CurrentSchema"/> or it is refused.
/// </para>
/// </remarks>
internal sealed class MotifSqliteStore : IDisposable
{
    private readonly string _path;
    private readonly string _name;
    private readonly object _stateGate = new();
    private readonly HashSet<SqliteConnection> _connections = [];
    private bool _disposed;

    private MotifSqliteStore(string path, string name)
    {
        _path = path;
        _name = name;
    }

    public static MotifSqliteStore Open(string path, MotifSqliteStoreDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        FileStream? ownership = null;
        try
        {
            // Taken only when there is a fresh database to create, so readers never contend.
            if (NeedsCreation(fullPath)) ownership = AcquireOwnership(fullPath);
            using var connection = OpenInspectionConnection(fullPath);
            var applicationId = PragmaInt(connection, "application_id");
            var schema = PragmaInt(connection, "user_version");
            if (applicationId != 0 && applicationId != descriptor.ApplicationId)
                throw new InvalidDataException($"The file is not a {descriptor.Name}.");
            if (schema < 0)
                throw new InvalidDataException($"The {descriptor.Name} has an invalid schema generation.");
            if (schema > descriptor.CurrentSchema)
                throw new NotSupportedException(
                    $"This {descriptor.Name} is at schema {schema} and this build understands " +
                    $"{descriptor.CurrentSchema}. " +
                    "Something newer opened it; update Motif and try again.");

            var hasTables = HasUserTables(connection);
            if (applicationId == 0 && schema != 0)
                throw new InvalidDataException($"A {descriptor.Name} with a schema must have its application id.");
            if (schema == 0 && hasTables)
                throw new InvalidDataException($"The existing database has no registered {descriptor.Name} schema.");

            // Pre-1.0 Motif never migrates: a stored schema below this build's is refused, not upgraded.
            if (schema != 0 && schema != descriptor.CurrentSchema)
                throw new NotSupportedException(
                    $"The {descriptor.Name} at '{fullPath}' is schema {schema}, but this build requires " +
                    $"exactly schema {descriptor.CurrentSchema}. Motif does not migrate before 1.0 — delete " +
                    "the database file and let Motif recreate it.");

            if (schema == 0)
            {
                Execute(connection, "BEGIN IMMEDIATE;");
                try
                {
                    SetApplicationId(connection, descriptor.ApplicationId);
                    descriptor.Create(connection, null);
                    descriptor.ValidateSchema(connection, null);
                    SetUserVersion(connection, null, descriptor.CurrentSchema);
                    Execute(connection, "COMMIT;");
                }
                catch
                {
                    try { Execute(connection, "ROLLBACK;"); }
                    catch (SqliteException) { }
                    throw;
                }
            }
            else
            {
                descriptor.ValidateSchema(connection, null);
                descriptor.BeforeOpen?.Invoke(connection);
            }

            SqliteConnections.EnableWal(connection);
            ownership?.Dispose();
            ownership = null;
            return new MotifSqliteStore(fullPath, descriptor.Name);
        }
        catch (SqliteException exception) when (SqliteConnections.IsCorruption(exception))
        {
            ownership?.Dispose();
            throw new InvalidDataException($"The {descriptor.Name} is corrupt or is not a database.", exception);
        }
        catch (SqliteException exception)
        {
            ownership?.Dispose();
            throw new IOException($"The {descriptor.Name} is unavailable.", exception);
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    public SqliteConnection OpenConnection()
    {
        lock (_stateGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MotifSqliteStore));
            SqliteConnection connection;
            try
            {
                connection = OpenConfiguredConnection(_path, RemoveConnection);
            }
            catch (SqliteException exception) when (SqliteConnections.IsCorruption(exception))
            {
                throw new InvalidDataException($"The {_name} is corrupt or is not a database.", exception);
            }
            catch (SqliteException exception)
            {
                throw new IOException($"The {_name} is unavailable.", exception);
            }
            _connections.Add(connection);
            return connection;
        }
    }

    public string FullPath => _path;

    public int TrackedConnectionCount
    {
        get
        {
            lock (_stateGate) return _connections.Count;
        }
    }

    public void Dispose()
    {
        List<SqliteConnection> connections;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            connections = [.. _connections];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            if (connection is OwnedSqliteConnection owned)
                owned.DisposeFromOwner();
            else
                connection.Dispose();
        }
    }

    internal static SqliteConnection OpenConnectionForTesting(string path, Action<SqliteConnection> configure) =>
        OpenConnectionCore(path, configure);

    private static SqliteConnection OpenInspectionConnection(string path)
        => OpenConnectionCore(path, SqliteConnections.ConfigureSession);

    private static SqliteConnection OpenConfiguredConnection(
        string path,
        Action<SqliteConnection>? onDisposed = null)
        => OpenConnectionCore(path, SqliteConnections.ConfigureConnection, onDisposed);

    private static SqliteConnection OpenConnectionCore(
        string path,
        Action<SqliteConnection> configure,
        Action<SqliteConnection>? onDisposed = null)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            // Opening the main file through a URI enables read-only URI ATTACH elsewhere in this process.
            DataSource = new Uri(path).AbsoluteUri,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        var connection = onDisposed is null
            ? new SqliteConnection(connectionString)
            : new OwnedSqliteConnection(connectionString, onDisposed);
        try
        {
            connection.Open();
            configure(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void RemoveConnection(SqliteConnection connection)
    {
        lock (_stateGate)
            _connections.Remove(connection);
    }

    private sealed class OwnedSqliteConnection : SqliteConnection
    {
        private readonly Action<SqliteConnection> _onDisposed;
        private bool _disposedByOwner;

        public OwnedSqliteConnection(string connectionString, Action<SqliteConnection> onDisposed)
            : base(connectionString) => _onDisposed = onDisposed;

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing) _onDisposed(this);
            }
        }

        public void DisposeFromOwner()
        {
            _disposedByOwner = true;
            Dispose();
        }

        public override void Open()
        {
            if (_disposedByOwner) throw new ObjectDisposedException(nameof(OwnedSqliteConnection));
            base.Open();
        }
    }

    /// True when the file is absent or empty, so it still needs its schema created.
    private static bool NeedsCreation(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            using var connection = OpenInspectionConnection(path);
            return PragmaInt(connection, "user_version") == 0;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    // Waits rather than fails: two processes opening a database that needs migrating is ordinary.
    private static FileStream AcquireOwnership(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return AcquireOwnershipCore(path); }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static FileStream AcquireOwnershipCore(string path)
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

    private static bool HasUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'index', 'view', 'trigger') " +
            "AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void SetApplicationId(SqliteConnection connection, int applicationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id = {applicationId};";
        command.ExecuteNonQuery();
    }

    private static int PragmaInt(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
