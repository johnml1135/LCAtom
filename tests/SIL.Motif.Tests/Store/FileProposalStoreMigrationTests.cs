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
        File.WriteAllText(source.ManifestPath(id), "{\"proposalId\":\"" + id + "\",\"status\":\"proposed\",\"currentIntentDigest\":\"" + digest + "\"}");

        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var layout = new LegacyProposalStoreLayout(source.RootDirectory);
        var boundaries = new List<string>();
        var first = FileProposalStoreMigration.ImportInto(layout, database, boundaries.Add);
        var second = FileProposalStoreMigration.ImportInto(layout, database);

        Assert.Single(first.ProposalIds);
        Assert.Empty(second.ProposalIds);
        Assert.Equal(json, new ProposalRepository(database).Get(SIL.Motif.Contract.Ids.CanonicalId.Parse(id)).ProposalJson);
        Assert.Equal(exactBytes, new ProposalRepository(database).Get(SIL.Motif.Contract.Ids.CanonicalId.Parse(id)).ProposalJsonBytes);
        Assert.False(Directory.Exists(source.RootDirectory));
        Assert.True(Directory.Exists(source.RootDirectory + ".migrated"));
        Assert.Contains("Proposals", boundaries);
        Assert.Contains("ProposalRevisions", boundaries);
        Assert.Contains("MigrationLedger", boundaries);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
