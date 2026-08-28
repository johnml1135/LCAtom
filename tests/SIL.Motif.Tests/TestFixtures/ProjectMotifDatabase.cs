using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>Opens a project's paired database directly, for tests that set up or corrupt Proposal state without going through the CLI.</summary>
internal static class ProjectMotifDatabase
{
    /// <summary>Opens the database paired with <paramref name="fwDataPath"/>, creating it if this is the first touch.</summary>
    public static MotifDatabase Open(string fwDataPath)
    {
        var full = Path.GetFullPath(fwDataPath);
        var project = new ProjectLocator(full, Path.GetFileNameWithoutExtension(full));
        return MotifDatabase.OpenOwned(
            ProjectDatabaseCatalog.DatabasePathFor(project), project, MotifSchema.CurrentSchema, new Version(1, 0));
    }
}
