using Microsoft.Data.Sqlite;
using SIL.Motif.Cli;
using SIL.Motif.Contract.Responses;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Covers the table that decides what a verb tells a caller when the paired store will not open.
/// </summary>
/// <remarks>
/// This is the module's real content: the exceptions come out of the store, and the reason a verb reports
/// decides whether a caller retries. Each case here is the store failing for one distinct cause.
/// </remarks>
public sealed class ProjectStoreCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-store-command-" + Guid.NewGuid().ToString("N"));

    public ProjectStoreCommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AnAbsentProjectFileIsAnInvocationErrorAndTheVerbNeverRuns()
    {
        var ran = false;

        var result = ProjectStoreCommand.Run(Path.Combine(_root, "absent.fwdata"), "1.0",
            (_, _) => { ran = true; return new CommandResult(0, string.Empty); });

        Assert.False(ran);
        Assert.Equal(FailureReason.InvalidArgument, result.Reason);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Project file not found", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAMotifDatabaseIsStoreInconsistent()
    {
        var project = Project("garbled");
        File.WriteAllText(Path.ChangeExtension(project, ".motif.db"), "this is not a database");

        var result = ProjectStoreCommand.Run(project, "1.0", (_, _) => new CommandResult(0, string.Empty));

        // Exit 4: nothing the caller did, and nothing a retry fixes.
        Assert.Equal(FailureReason.StoreInconsistent, result.Reason);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public void AStoreThisBuildIsTooOldForIsRefusedRatherThanRetried()
    {
        var project = Project("newer");
        ProjectStoreCommand.Run(project, "99.0", (_, _) => new CommandResult(0, string.Empty));
        RequireWorkerVersion(Path.ChangeExtension(project, ".motif.db"), "99.0");

        var result = ProjectStoreCommand.Run(project, "1.0", (_, _) => new CommandResult(0, string.Empty));

        Assert.Equal(FailureReason.Refused, result.Reason);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void AnUnopenableDatabaseIsBusySoTheCallerMayTryAgain()
    {
        var project = Project("blocked");
        // A directory where the database file belongs: the same catch carries the owner-lock case.
        Directory.CreateDirectory(Path.ChangeExtension(project, ".motif.db"));

        var result = ProjectStoreCommand.Run(project, "1.0", (_, _) => new CommandResult(0, string.Empty));

        Assert.Equal(FailureReason.Busy, result.Reason);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void AVerbGetsAnOpenStoreAndItsOwnResultIsReturnedUntouched()
    {
        var project = Project("working");

        var result = ProjectStoreCommand.Run(project, "1.0", (database, located) =>
        {
            Assert.NotNull(database);
            Assert.Equal("working", located.FieldWorksProjectIdentity);
            return new CommandResult(0, "the verb's own output");
        });

        Assert.Null(result.Reason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("the verb's own output", result.Output);
        Assert.True(File.Exists(Path.ChangeExtension(project, ".motif.db")));
    }

    [Fact]
    public void AMalformedProductVersionOpensTheStoreRatherThanRefusingTheVerb()
    {
        var result = ProjectStoreCommand.Run(Project("loose"), "not-a-version",
            (_, _) => new CommandResult(0, string.Empty));

        Assert.Null(result.Reason);
    }

    private string Project(string name)
    {
        var path = Path.Combine(_root, name + ".fwdata");
        File.WriteAllText(path, "<languageproject/>");
        return path;
    }

    private static void RequireWorkerVersion(string databasePath, string version)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MotifMetadata SET MinimumWorkerVersion = $version WHERE Id = 1;";
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
