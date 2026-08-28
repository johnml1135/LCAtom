namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// A <c>.fwdata</c> stand-in for a test that only needs a project path to derive a store from, and
/// never loads the project itself.
/// </summary>
internal static class PlaceholderProject
{
    /// <summary>Creates an empty <c>.fwdata</c> file under a fresh directory and returns its path.</summary>
    public static string Create(string rootDirectoryName)
    {
        var root = Path.Combine(Path.GetTempPath(), rootDirectoryName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Project.fwdata");
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
