using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using System.Text;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class FileProposalStoreMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-file-migration-" + Guid.NewGuid().ToString("N"));

    public FileProposalStoreMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ImportsCanonicalObjectBytesAndManifestStateIdempotently()
    {
        var source = new ProposalStore(Path.Combine(_root, "files"));
        source.EnsureDirectoriesExist();
        var id = SIL.Motif.Contract.Ids.CanonicalId.Mint("proposal/").Value;
        var json = "  {\"contractVersions\":{},\"proposalId\":\"" + id +
            "\",\"requires\":[],\"operations\":[]}\r\n";
        var exactBytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var digest = IntentDigest.Compute(ProposalJsonParser.Parse(json));
        File.WriteAllBytes(source.ObjectPath(digest), exactBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(source.ManifestPath(id))!);
        File.WriteAllText(source.ManifestPath(id), "{\"proposalId\":\"" + id + "\",\"status\":\"approved\",\"label\":\"Labeled\",\"comment\":\"Review note\",\"supersededBy\":\"replacement\",\"currentIntentDigest\":\"" + digest +
            "\",\"decision\":{\"outcome\":\"approved\",\"actorType\":\"person\",\"actorId\":\"reviewer\",\"comment\":\"Looks good\",\"boundIntentDigest\":\"" + digest + "\",\"timestampUtc\":\"2026-08-22T12:00:00Z\"}}");
        var draftJson = "{\"proposalId\":\"" + id + "\",\"operations\":[ 1, 2 ],\"note\":\"draft\"}\r\n";
        File.WriteAllText(source.DraftPath("working"), draftJson);

        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var layout = new LegacyProposalStoreLayout(source.RootDirectory);
        var boundaries = new List<string>();
        var first = FileProposalStoreMigration.ImportInto(layout, database, boundaries.Add);
        var second = FileProposalStoreMigration.ImportInto(layout, database);

        Assert.Single(first.ProposalIds);
        Assert.Empty(second.ProposalIds);
        var record = new ProposalRepository(database).Get(SIL.Motif.Contract.Ids.CanonicalId.Parse(id));
        Assert.Equal(json, record.ProposalJson);
        Assert.Equal(exactBytes, record.ProposalJsonBytes);
        Assert.Equal("approved", record.Status);
        Assert.Equal("Labeled", record.Label);
        Assert.Equal("Review note", record.Comment);
        Assert.Equal("replacement", record.SupersededBy);
        Assert.NotNull(record.Decision);
        Assert.Equal(digest, record.Decision!.IntentDigest);
        Assert.Equal("reviewer", record.Decision.ActorId);
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DraftJson FROM Drafts WHERE DraftName = 'working';";
            Assert.Equal(draftJson, command.ExecuteScalar());
        }
        Assert.False(Directory.Exists(source.RootDirectory));
        Assert.True(Directory.Exists(source.RootDirectory + ".migrated"));
        Assert.Contains("Proposals", boundaries);
        Assert.Contains("ProposalRevisions", boundaries);
        Assert.Contains("Decisions", boundaries);
        Assert.Contains("Drafts", boundaries);
        Assert.Contains("MigrationLedger", boundaries);
    }

    [Theory]
    [InlineData("Proposals")]
    [InlineData("ProposalRevisions")]
    [InlineData("Decisions")]
    [InlineData("Drafts")]
    [InlineData("MigrationLedger")]
    public void RollsBackEveryFileImportBoundary(string boundary)
    {
        var root = Path.Combine(_root, boundary);
        var source = SeedSource(root);
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var observedPartial = false;

        Assert.Throws<InvalidOperationException>(() => FileProposalStoreMigration.ImportInto(source.Layout, database, reached =>
        {
            if (reached != boundary) return;
            using var client = database.OpenConnection();
            using var count = client.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Proposals;";
            observedPartial = Convert.ToInt64(count.ExecuteScalar()) != 0;
            throw new InvalidOperationException("injected");
        }, renameSourceAfterCommit: false));

        Assert.False(observedPartial);
        Assert.True(File.Exists(source.ObjectPath));
        Assert.Equal(source.ObjectBytes, File.ReadAllBytes(source.ObjectPath));
        using (var connection = database.OpenConnection())
        {
            foreach (var table in new[] { "Proposals", "ProposalRevisions", "Decisions", "Drafts", "MigrationLedger" })
            {
                using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM " + table + ";";
                Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
            }
        }

        var retry = FileProposalStoreMigration.ImportInto(source.Layout, database, renameSourceAfterCommit: false);
        Assert.Single(retry.ProposalIds);
        Assert.True(Directory.Exists(source.Layout.RootDirectory));
        using var ledger = database.OpenConnection();
        using var ledgerCount = ledger.CreateCommand();
        ledgerCount.CommandText = "SELECT COUNT(*) FROM MigrationLedger WHERE SourceKind = 'file-proposals';";
        Assert.Equal(1L, Convert.ToInt64(ledgerCount.ExecuteScalar()));
    }

    [Fact]
    public void PreservesArchiveCollisionAndRejectsInvalidUtf8()
    {
        var root = Path.Combine(_root, "collision");
        var source = SeedSource(root);
        Directory.Move(source.Layout.RootDirectory, source.Layout.RootDirectory + ".migrated");
        var replacement = SeedSource(root);
        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        FileProposalStoreMigration.ImportInto(replacement.Layout, database);
        Assert.True(Directory.Exists(replacement.Layout.RootDirectory + ".migrated-1"));
        Assert.True(File.Exists(source.Layout.RootDirectory + ".migrated" + Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar + Path.GetFileName(source.ObjectPath)));

        var invalid = SeedSource(Path.Combine(_root, "invalid"));
        File.WriteAllBytes(invalid.ObjectPath, [0xC3, 0x28]);
        Assert.Throws<InvalidDataException>(() => FileProposalStoreMigration.ImportInto(invalid.Layout, database, renameSourceAfterCommit: false));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("decision-mismatch")]
    public void RejectsFileIntegrityFailuresWithoutChangingDestination(string failure)
    {
        var root = Path.Combine(_root, "integrity-" + failure);
        var source = SeedSource(root);
        if (failure == "missing")
            File.Delete(source.ObjectPath);
        else if (failure == "tampered")
            File.WriteAllText(source.ObjectPath, "{\"contractVersions\":{},\"proposalId\":\"" + SIL.Motif.Contract.Ids.CanonicalId.Mint("other/").Value + "\",\"requires\":[],\"operations\":[]}");
        else
        {
            var manifestPath = Directory.GetFiles(Path.Combine(source.Layout.RootDirectory, "manifests"), "*.json", SearchOption.AllDirectories).Single();
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(
                "\"boundIntentDigest\":\"" + source.Digest + "\"",
                "\"boundIntentDigest\":\"sha256:" + new string('0', 64) + "\"", StringComparison.Ordinal));
        }

        var project = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.Throws<InvalidDataException>(() => FileProposalStoreMigration.ImportInto(source.Layout, database, renameSourceAfterCommit: false));
        using var connection = database.OpenConnection();
        foreach (var table in new[] { "Proposals", "ProposalRevisions", "Decisions", "Drafts", "MigrationLedger" })
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
        }
        Assert.True(Directory.Exists(source.Layout.RootDirectory));
    }

    private SeededSource SeedSource(string root)
    {
        var store = new ProposalStore(Path.Combine(root, "files"));
        store.EnsureDirectoriesExist();
        var id = SIL.Motif.Contract.Ids.CanonicalId.Mint("proposal/").Value;
        var json = "  {\"contractVersions\":{},\"proposalId\":\"" + id + "\",\"requires\":[],\"operations\":[]}\r\n";
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var digest = IntentDigest.Compute(ProposalJsonParser.Parse(json));
        var objectPath = store.ObjectPath(digest);
        File.WriteAllBytes(objectPath, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(store.ManifestPath(id))!);
        File.WriteAllText(store.ManifestPath(id), "{\"proposalId\":\"" + id + "\",\"status\":\"proposed\",\"currentIntentDigest\":\"" + digest +
            "\",\"decision\":{\"outcome\":\"approved\",\"actorType\":\"person\",\"actorId\":\"reviewer\",\"boundIntentDigest\":\"" + digest + "\",\"timestampUtc\":\"2026-08-22T12:00:00Z\"}}");
        File.WriteAllText(store.DraftPath("working"), "{\"proposalId\":\"" + id + "\",\"draft\":true}\r\n");
        return new SeededSource(new LegacyProposalStoreLayout(store.RootDirectory), objectPath, bytes, id, digest);
    }

    private sealed record SeededSource(LegacyProposalStoreLayout Layout, string ObjectPath, byte[] ObjectBytes, string ProposalId, string Digest);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
