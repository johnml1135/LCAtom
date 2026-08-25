using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class ProjectStoreCutoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-cutover-" + Guid.NewGuid().ToString("N"));

    public ProjectStoreCutoverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TakesBothLegacySourcesAndRecordsOneCutover()
    {
        var store = SeedStore("both");
        using var database = OpenDestination("both");

        var result = ProjectStoreCutover.Run(store, database);

        Assert.NotNull(result.FileProposals);
        Assert.NotNull(result.LegacyBulk);
        Assert.Equal(1L, Count(database, "Proposals"));
        Assert.Equal(1L, Count(database, "Corpora"));
        Assert.Equal(1L, Scalar(database,
            "SELECT COUNT(*) FROM MigrationLedger WHERE SourceKind = '" + ProjectStoreCutover.CutoverKind + "';"));
    }

    [Fact]
    public void FailingTheSecondSourceLeavesNeitherSourceImportedNorArchived()
    {
        var store = SeedStore("atomic");
        var bulkPath = Path.Combine(store, "motif.db");
        // Wrong schema, not a corrupt file: this fails inside the transaction, after the file store landed.
        File.Delete(bulkPath);
        using (var wrong = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = bulkPath, Pooling = false }.ToString()))
        {
            wrong.Open();
            using var create = wrong.CreateCommand();
            create.CommandText = "CREATE TABLE Unrelated (Value TEXT);";
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using var database = OpenDestination("atomic");

        var reached = new List<string>();
        Assert.ThrowsAny<Exception>(() => ProjectStoreCutover.Run(store, database, onBoundary: reached.Add));

        // Proves the rollback is load-bearing: the file store really was imported before the bulk source failed.
        Assert.Contains("Proposals", reached);
        foreach (var table in new[] { "Proposals", "ProposalRevisions", "Decisions", "Drafts", "MigrationLedger" })
            Assert.Equal(0L, Count(database, table));
        Assert.True(Directory.Exists(Path.Combine(store, "manifests")));
        Assert.True(File.Exists(bulkPath));
    }

    [Fact]
    public void RollingBackLeavesTheDestinationsPreCutoverRowsIntact()
    {
        var store = SeedStore("preserve");
        using var database = OpenDestination("preserve");
        using (var connection = database.OpenConnection())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = "INSERT INTO Corpora VALUES ('existing','{\"source\":\"destination\"}');";
            seed.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => ProjectStoreCutover.Run(store, database,
            beforeCommit: () => throw new IOException("commit seam")));

        Assert.Equal(1L, Count(database, "Corpora"));
        Assert.Equal("destination", Scalar(database,
            "SELECT json_extract(ProvenanceJson, '$.source') FROM Corpora WHERE CorpusId = 'existing';"));
        Assert.Equal(0L, Count(database, "Proposals"));
    }

    [Fact]
    public void AFailedArchivalIsReportedAsDebtRatherThanAFailedCutover()
    {
        var store = SeedStore("archive-debt");
        using var database = OpenDestination("archive-debt");
        // Holding the nested source open blocks its move, and the container's move along with it.
        using var holder = File.Open(Path.Combine(store, "motif.db"), FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = ProjectStoreCutover.Run(store, database);

        Assert.False(result.ArchivalComplete);
        Assert.Empty(result.ArchivedPaths);
        Assert.Equal(
            new[] { FileProposalStoreMigration.FileProposalsKind, LegacyBulkStoreMigration.LegacyBulkKind },
            result.ArchiveFailures.Select(failure => failure.Kind).OrderBy(kind => kind, StringComparer.Ordinal));
        Assert.Equal(1L, Count(database, "Corpora"));
        Assert.Equal(1L, Count(database, "Proposals"));
        Assert.True(Directory.Exists(store));
    }

    [Fact]
    public void RetryingAfterAnArchiveFailureArchivesWithoutReimporting()
    {
        var store = SeedStore("retry");
        using var database = OpenDestination("retry");
        var bulkPath = Path.Combine(store, "motif.db");
        using (File.Open(bulkPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(ProjectStoreCutover.Run(store, database).ArchivalComplete);

        var retry = ProjectStoreCutover.Run(store, database);

        Assert.True(retry.ArchivalComplete);
        Assert.False(Directory.Exists(store));
        // The nested source is archived inside the archived container, having been moved aside first.
        Assert.True(File.Exists(Path.Combine(store + ".migrated", "motif.db.migrated")));
        Assert.Equal(1L, Count(database, "Corpora"));
        Assert.Equal(1L, Count(database, "Proposals"));
    }

    [Fact]
    public void TheNestedSourceIsArchivedBeforeTheContainerThatWouldCarryItAway()
    {
        var store = SeedStore("nesting");
        using var database = OpenDestination("nesting");

        var result = ProjectStoreCutover.Run(store, database);

        Assert.True(result.ArchivalComplete);
        Assert.Equal(
            new[] { Path.Combine(store, "motif.db"), store },
            result.ArchivedPaths);
        Assert.True(File.Exists(Path.Combine(store + ".migrated", "motif.db.migrated")));
    }

    [Fact]
    public void AnAttachedSourceCannotBeMovedUntilItIsDetached()
    {
        var path = Path.Combine(_root, "attach", "source.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        using (var source = new SqliteConnection(options.ToString()))
        {
            source.Open();
            using var create = source.CreateCommand();
            create.CommandText = "CREATE TABLE Present (Value TEXT);";
            create.ExecuteNonQuery();
        }
        using var database = OpenDestination("attach");
        using var connection = database.OpenConnection();

        using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE '" + path.Replace("'", "''") + "' AS legacy;";
        attach.ExecuteNonQuery();

        var moved = Path.Combine(Path.GetDirectoryName(path)!, "moved.db");
        var failure = Record.Exception(() => File.Move(path, moved));
        LegacyBulkStoreMigration.Detach(connection);
        File.Move(path, moved);

        Assert.IsType<IOException>(failure);
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void AStoreWithNoLegacySourcesStillRecordsItsCutover()
    {
        var store = Path.Combine(_root, "empty", "store");
        Directory.CreateDirectory(store);
        using var database = OpenDestination("empty");

        var result = ProjectStoreCutover.Run(store, database);

        Assert.Null(result.LegacyBulk);
        Assert.True(result.ArchivalComplete);
        Assert.Equal(1L, Scalar(database,
            "SELECT COUNT(*) FROM MigrationLedger WHERE SourceKind = '" + ProjectStoreCutover.CutoverKind + "';"));
    }

    private string SeedStore(string name)
    {
        var store = Path.Combine(_root, name, "store");
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
        return store;
    }

    private MotifDatabase OpenDestination(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        return MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
    }

    private static long Count(MotifDatabase database, string table) =>
        Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM " + table + ";"));

    private static object Scalar(MotifDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
