using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class ProposalRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-proposals-" + Guid.NewGuid().ToString("N"));

    public ProposalRepositoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SavesRevisionDecisionAndListsCurrentProposal()
    {
        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        using var database = MotifDatabase.OpenOwned(
            Path.Combine(_root, "project.motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
        var id = CanonicalId.Mint("proposal/");
        var repository = new ProposalRepository(database);
        var revision = new ProposalRevisionRecord(
            id, "sha256:abc", "{\"proposalId\":\"" + id.Value + "\"}",
            "proposed", "label", "comment", null);

        repository.SaveRevision(revision);
        repository.SaveDecision(new DecisionRecord(
            id, revision.IntentDigest, "approved", "human", "linguist", "ok", "2026-08-22T00:00:00Z"));

        var result = repository.Get(id);
        Assert.NotNull(result);
        Assert.Equal(revision.ProposalJson, result!.ProposalJson);
        Assert.Equal("approved", result.Status);
        Assert.Single(repository.List(new ProposalListFilter()));
    }

    [Fact]
    public void RejectsConflictingRevisionAndFiltersArchivedHistory()
    {
        var project = new ProjectLocator(Path.Combine(_root, "history.fwdata"), "history");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "history.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var id = CanonicalId.Mint("proposal/");
        var repository = new ProposalRepository(database);
        repository.SaveRevision(new ProposalRevisionRecord(id, "sha256:first", "{\"proposalId\":\"" + id.Value + "\"}", "proposed", null, null, null));
        Assert.Throws<InvalidDataException>(() => repository.SaveRevision(new ProposalRevisionRecord(
            id, "sha256:first", "{\"proposalId\":\"different\"}", "proposed", null, null, null)));
        repository.SaveDecision(new DecisionRecord(id, "sha256:first", "approved", "human", "a", null, "2026-08-22T00:00:00Z"));
        Assert.Empty(repository.List(new ProposalListFilter("proposed")));
        Assert.Single(repository.List(new ProposalListFilter("approved")));
        Assert.Throws<KeyNotFoundException>(() => repository.Get(CanonicalId.Mint("proposal/")));
    }

    [Fact]
    public void ReportRepositoryPreservesEvidenceBinding()
    {
        var project = new ProjectLocator(Path.Combine(_root, "reports.fwdata"), "reports");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "reports.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var id = CanonicalId.Mint("proposal/");
        var repository = new ProposalRepository(database);
        repository.SaveRevision(new ProposalRevisionRecord(id, "sha256:report", "{\"proposalId\":\"" + id.Value + "\"}", "proposed", null, null, null));
        var reports = new ReportRepository(database);
        reports.Save(new ReportRecord("r1", id, null, "{ \"finding\": \"é\" }", "{\"intentDigest\":\"sha256:report\"}"));
        var result = reports.Get("r1");
        Assert.Equal(id, result!.ProposalId);
        Assert.Equal("{\"intentDigest\":\"sha256:report\"}", result.EvidenceJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
