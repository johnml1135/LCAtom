using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Host.Store;

/// <summary>Represents one process's connection boundary onto a project's Motif database.</summary>
/// <remarks>
/// A thin wrapper over <see cref="MotifSqliteStore"/>: this type owns only what is specific to a project
/// database — validating <see cref="ProjectLocator"/> and worker-version arguments, and the
/// <see cref="MotifSchema"/> descriptor those arguments feed. The open-and-create ceremony, connection
/// lifecycle, and failure translation it delegates to are shared with <see cref="MachineDatabase"/>.
/// </remarks>
public sealed class MotifDatabase : IDisposable
{
    private readonly MotifSqliteStore _store;

    private MotifDatabase(MotifSqliteStore store) => _store = store;

    /// <summary>
    /// Opens a project database, creating it if absent. An existing database at any other schema is
    /// refused rather than migrated: pre-1.0 Motif has no upgrade path.
    /// </summary>
    /// <param name="path">The sibling Motif database path.</param>
    /// <param name="project">The project locator that must match persisted metadata.</param>
    /// <param name="supportedSchema">The schema generation this worker requires; usually <see cref="MotifSchema.CurrentSchema"/>.</param>
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
        ArgumentNullException.ThrowIfNull(workerVersion);
        if (supportedSchema < 1) throw new ArgumentOutOfRangeException(nameof(supportedSchema));
        if (supportedSchema > MotifSchema.CurrentSchema)
            throw new NotSupportedException($"Motif schema {supportedSchema} is not known to this worker.");
        if (workerVersion < MotifSchema.MinimumWorkerVersion(supportedSchema))
            throw new NotSupportedException(
                $"Worker {workerVersion} is older than schema {supportedSchema} minimum " +
                $"{MotifSchema.MinimumWorkerVersion(supportedSchema)}.");

        var descriptor = new MotifSqliteStoreDescriptor
        {
            Name = "Motif database",
            ApplicationId = MotifSchema.ApplicationId,
            CurrentSchema = supportedSchema,
            ValidateSchema = MotifSchema.ValidateSchema,
            Create = (connection, transaction) => MotifSchema.Create(connection, transaction, project),
            BeforeOpen = connection =>
            {
                var metadata = MotifSchema.ReadMetadata(connection);
                EnsureLocatorMatches(metadata.Project, project);
                if (workerVersion < metadata.MinimumWorkerVersion)
                    throw new NotSupportedException(
                        $"Worker {workerVersion} is older than database minimum {metadata.MinimumWorkerVersion}.");
            }
        };
        return new MotifDatabase(MotifSqliteStore.Open(path, descriptor));
    }

    /// <summary>Opens a configured connection while this worker owns the database.</summary>
    public SqliteConnection OpenConnection() => _store.OpenConnection();

    /// <summary>Gets the fully resolved path of the owned Motif database.</summary>
    public string FullPath => _store.FullPath;

    internal int TrackedConnectionCount => _store.TrackedConnectionCount;

    /// <summary>Closes this process's connections. Nothing is released for anyone else.</summary>
    public void Dispose() => _store.Dispose();

    internal static SqliteConnection OpenConfiguredConnectionForTesting(
        string path,
        Action<SqliteConnection> configure) => MotifSqliteStore.OpenConnectionForTesting(path, configure);

    private static void EnsureLocatorMatches(ProjectLocator stored, ProjectLocator requested)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(stored.FullFwDataPath, requested.FullFwDataPath) ||
            !StringComparer.Ordinal.Equals(stored.FieldWorksProjectIdentity, requested.FieldWorksProjectIdentity))
            throw new InvalidDataException("The database is registered to a different project locator.");
    }
}
