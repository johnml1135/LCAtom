using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>Resolves paired project paths and is the worker's only database construction boundary.</summary>
public sealed class ProjectDatabaseCatalog
{
    private readonly int _supportedSchema;
    private readonly Version _workerVersion;

    /// <summary>Creates a catalog for one worker schema and compatibility generation.</summary>
    /// <param name="supportedSchema">The highest database schema this worker can open.</param>
    /// <param name="workerVersion">The worker version to use for compatibility checks.</param>
    public ProjectDatabaseCatalog(int supportedSchema, Version workerVersion)
    {
        if (supportedSchema < 1) throw new ArgumentOutOfRangeException(nameof(supportedSchema));
        ArgumentNullException.ThrowIfNull(workerVersion);
        _supportedSchema = supportedSchema;
        _workerVersion = workerVersion;
    }

    /// <summary>Opens the paired database while retaining worker ownership for its lifetime.</summary>
    public MotifDatabase Open(ProjectLocator project) => OpenOwned(project);

    /// <summary>Opens the project sibling database through the worker-owned host boundary.</summary>
    public MotifDatabase OpenOwned(ProjectLocator project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return MotifDatabase.OpenOwned(DatabasePathFor(project), project, _supportedSchema, _workerVersion);
    }

    /// <summary>Derives the sibling <c>.motif.db</c> path from a project data-file locator.</summary>
    public static string DatabasePathFor(ProjectLocator project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var directory = Path.GetDirectoryName(project.FullFwDataPath)!;
        var stem = Path.GetFileNameWithoutExtension(project.FullFwDataPath);
        return Path.Combine(directory, stem + ".motif.db");
    }
}
