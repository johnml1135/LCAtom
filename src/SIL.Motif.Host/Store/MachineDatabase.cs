using Microsoft.Data.Sqlite;

namespace SIL.Motif.Host.Store;

/// <summary>Represents one process's connection boundary onto the machine store.</summary>
/// <remarks>
/// One machine store exists per logged-in user, in the worker root, holding <c>KnownProjects</c> and
/// <c>Usage</c> — nothing that belongs to any one project. A thin wrapper over
/// <see cref="MotifSqliteStore"/>, sharing its open-and-create ceremony and connection lifecycle with
/// <see cref="MotifDatabase"/>; this type owns only the <see cref="MachineSchema"/> descriptor and its own
/// path resolution.
/// </remarks>
public sealed class MachineDatabase : IDisposable
{
    private readonly MotifSqliteStore _store;

    private MachineDatabase(MotifSqliteStore store) => _store = store;

    /// <summary>Opens and owns the machine store at <c>&lt;root&gt;/motif.db</c>, creating it if absent.</summary>
    /// <param name="root">The worker root this installation uses for the logged-in user.</param>
    /// <returns>An owned database boundary whose connections are configured for worker use.</returns>
    /// <exception cref="InvalidDataException">The file identity or schema generation is invalid.</exception>
    /// <exception cref="NotSupportedException">The schema generation is newer than this build understands.</exception>
    public static MachineDatabase Open(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A worker root is required.", nameof(root));

        var path = Path.Combine(root, "motif.db");
        var descriptor = new MotifSqliteStoreDescriptor
        {
            Name = "Motif machine database",
            ApplicationId = MachineSchema.ApplicationId,
            CurrentSchema = MachineSchema.CurrentSchema,
            ValidateSchema = MachineSchema.ValidateSchema,
            Create = MachineSchema.Create
        };
        return new MachineDatabase(MotifSqliteStore.Open(path, descriptor));
    }

    /// <summary>Opens a configured connection while this process owns the database.</summary>
    public SqliteConnection OpenConnection() => _store.OpenConnection();

    /// <summary>Gets the fully resolved path of the owned machine database.</summary>
    public string FullPath => _store.FullPath;

    internal int TrackedConnectionCount => _store.TrackedConnectionCount;

    /// <summary>Closes this process's connections. Nothing is released for anyone else.</summary>
    public void Dispose() => _store.Dispose();
}
