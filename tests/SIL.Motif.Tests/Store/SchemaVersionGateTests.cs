using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>Covers what a person is told when their build cannot open the database in front of it.</summary>
/// <remarks>
/// The CLI and the job runner both open this database, so an upgrade race puts an ordinary user in front
/// of this refusal. What it says therefore has to be actionable rather than merely accurate.
/// </remarks>
public sealed class SchemaVersionGateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-gate-" + Guid.NewGuid().ToString("N"));

    public SchemaVersionGateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AnOlderBuildIsRefusedWithSomethingTheUserCanActOn()
    {
        var path = Path.Combine(_root, "project.motif.db");
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using (MotifDatabase.OpenOwned(path, locator, MotifSchema.CurrentSchema, new Version(1, 0))) { }

        var refusal = Assert.Throws<NotSupportedException>(() =>
            MotifDatabase.OpenOwned(path, locator, MotifSchema.CurrentSchema - 1, new Version(1, 0)));

        // Naming the generations alone tells a user nothing they can do; the remedy has to be in the text.
        Assert.Contains("update Motif", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MotifSchema.CurrentSchema.ToString(), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingTheLeaseGenerationDidNotRaiseTheCompatibilityFloor()
    {
        var path = Path.Combine(_root, "floor.motif.db");
        var locator = new ProjectLocator(Path.Combine(_root, "floor.fwdata"), "floor");

        // The columns are additive and nullable, so a build that predates them stays welcome.
        using var database = MotifDatabase.OpenOwned(path, locator, MotifSchema.CurrentSchema,
            new Version(1, 0));

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MinimumWorkerVersion FROM MotifMetadata WHERE Id = 1;";
        Assert.Equal("1.0", command.ExecuteScalar() as string);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
