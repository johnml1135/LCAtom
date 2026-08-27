using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Generator;
using SIL.Motif.Host.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Argv-level proof for the first CLI verb that is a worker command rather than local file work.
/// </summary>
/// <remarks>
/// These drive the real <c>motif.exe</c>, so they cover what only the executable decides: verb routing, flag
/// validation, exit codes, and that a failure is reported as an actionable message rather than a stack
/// trace. <see cref="ACutoverRunsEndToEndInTheCliProcess"/> also covers the successful round trip: the
/// executable takes a real store into a real sibling database and archives the sources, with no second
/// process involved.
/// </remarks>
public sealed class StoreCutoverArgvTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-cutover-argv-" + Guid.NewGuid().ToString("N"));

    public StoreCutoverArgvTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TheVerbIsRoutedAndListedRatherThanReportedUnknown()
    {
        var unknown = Run("store-rollback");

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("Unknown command 'store-rollback'", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("store-cutover --project", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OmittingTheProjectIsAUsageFailureThatNamesTheFlag()
    {
        var result = Run("store-cutover");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: motif store-cutover --project <fwdata>", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void AProjectThatDoesNotExistIsRefusedBeforeAnyWorkerIsStarted()
    {
        var missing = Path.Combine(_root, "absent.fwdata");

        var result = Run("store-cutover --project \"" + missing + "\" --store \"" + _root + "\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error: Project file not found", result.Error, StringComparison.Ordinal);
        Assert.Contains(missing, result.Error, StringComparison.Ordinal);
        // A stack trace here would mean the CLI let an exception escape instead of reporting the refusal.
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutoverRunsEndToEndInTheCliProcess()
    {
        var project = Path.Combine(_root, "project.fwdata");
        File.WriteAllText(project, string.Empty);
        var store = SeedStore();

        var result = Run("store-cutover --project \"" + project + "\" --store \"" + store + "\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains("Proposals imported: 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Legacy rows imported: 1", result.Output, StringComparison.Ordinal);
        // The sibling database is the destination, and the legacy sources are moved aside once it holds them.
        Assert.True(File.Exists(Path.Combine(_root, "project.motif.db")));
        Assert.False(Directory.Exists(store));
        Assert.True(Directory.Exists(store + ".migrated"));
    }

    [Fact]
    public void ASecondCutoverOfTheSameStoreImportsNothingTwice()
    {
        var project = Path.Combine(_root, "project.fwdata");
        File.WriteAllText(project, string.Empty);
        var store = SeedStore();
        Assert.Equal(0, Run("store-cutover --project \"" + project + "\" --store \"" + store + "\"").ExitCode);

        var again = Run("store-cutover --project \"" + project + "\" --store \"" + store + "\"");

        Assert.Equal(0, again.ExitCode);
        Assert.Contains("already taken", again.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutoverStillRunsWhileAnotherProcessHasTheDatabaseOpen()
    {
        var project = Path.Combine(_root, "project.fwdata");
        File.WriteAllText(project, string.Empty);
        var store = SeedStore();
        var locator = new ProjectLocator(project, "project");
        // Standing in for a second motif invocation or a job runner: it owns the database for the duration.
        using var held = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));

        var result = Run("store-cutover --project \"" + project + "\" --store \"" + store + "\"");

        // Exclusion is SQLite's write lock, which serialises writers rather than locking them out.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Proposals imported: 1", result.Output, StringComparison.Ordinal);
    }

    /// Mirrors the fixture shape <c>ProjectStoreCutoverTests</c> seeds: one file proposal and one legacy row.
    private string SeedStore()
    {
        var store = Path.Combine(_root, "store");
        var proposals = new ProposalStore(store);
        proposals.EnsureDirectoriesExist();
        var id = CanonicalId.Mint("proposal/").Value;
        var json = "{\"contractVersions\":{},\"proposalId\":\"" + id + "\",\"requires\":[],\"operations\":[]}";
        var digest = IntentDigest.Compute(ProposalJsonParser.Parse(json));
        File.WriteAllText(proposals.ObjectPath(digest), json);
        Directory.CreateDirectory(Path.GetDirectoryName(proposals.ManifestPath(id))!);
        File.WriteAllText(proposals.ManifestPath(id),
            "{\"proposalId\":\"" + id + "\",\"status\":\"proposed\",\"currentIntentDigest\":\"" + digest + "\"}");

        var options = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store, "motif.db"),
            Pooling = false
        };
        using var legacy = new SqliteConnection(options.ToString());
        legacy.Open();
        MotifSchema.EnsureLegacyTables(legacy);
        using var command = legacy.CreateCommand();
        command.CommandText = "INSERT INTO Corpora VALUES ('c1','{\"source\":\"legacy\"}');";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
        return store;
    }

    private static CliRun Run(string arguments)
    {
        var executable = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "SIL.Motif.Cli", "bin", "Debug", "net10.0", "motif.exe");
        var start = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "The CLI did not exit within its bound.");
        return new CliRun(process.ExitCode, output, error);
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
