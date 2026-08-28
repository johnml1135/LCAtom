using Microsoft.Data.Sqlite;
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

    [Fact]
    public void RejectsInvalidUtf8WhenReadingExactProposalBytes()
    {
        var project = new ProjectLocator(Path.Combine(_root, "utf8.fwdata"), "utf8");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "utf8.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var id = CanonicalId.Mint("proposal/");
        var repository = new ProposalRepository(database);
        repository.SaveRevision(new ProposalRevisionRecord(
            id, "sha256:invalid", "{\"proposalId\":\"" + id.Value + "\"}", "proposed", null, null, null,
            ProposalJsonBytes: [0xC3, 0x28]));
        Assert.Throws<InvalidDataException>(() => repository.Get(id));
    }

    [Fact]
    public void RejectsTextProposalJsonCorruptionAsInvalidData()
    {
        var project = new ProjectLocator(Path.Combine(_root, "text-corrupt.fwdata"), "text-corrupt");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "text-corrupt.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var id = CanonicalId.Mint("proposal/");
        var repository = new ProposalRepository(database);
        repository.SaveRevision(new ProposalRevisionRecord(id, "sha256:text", "{\"proposalId\":\"" + id.Value + "\"}",
            "proposed", null, null, null));
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ProposalRevisions SET ProposalJson = CAST($json AS TEXT);";
            command.Parameters.AddWithValue("$json", "text corruption");
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => repository.Get(id));
    }

    [Fact]
    public void CreateDraftThenGetDraftRoundTripsAndRejectsADuplicateName()
    {
        var project = new ProjectLocator(Path.Combine(_root, "draft.fwdata"), "draft");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "draft.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var id = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", id, "{\"label\":\"in progress\"}");

        var draft = repository.GetDraft("working");
        Assert.Equal(id, draft.ProposalId);
        Assert.Equal("working", draft.DraftName);
        Assert.Null(draft.IntentDigest);
        Assert.Equal("{\"label\":\"in progress\"}", draft.ProposalJson);
        Assert.Equal("draft", draft.Status);

        Assert.Throws<InvalidOperationException>(
            () => repository.CreateDraft("working", CanonicalId.Mint("proposal/"), "{}"));
    }

    [Fact]
    public void FinalizeIsAtomicSettingRevisionDigestAndClearingTheDraftNameTogether()
    {
        var project = new ProjectLocator(Path.Combine(_root, "finalize.fwdata"), "finalize");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "finalize.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var id = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", id, "{\"draft\":true}");

        repository.Finalize("working", "sha256:first", "{\"proposalId\":\"" + id.Value + "\"}", "a label", "a comment");

        var committed = repository.Get(id);
        Assert.Equal("sha256:first", committed.IntentDigest);
        Assert.Equal("proposed", committed.Status);
        Assert.Equal("a label", committed.Label);
        Assert.Equal("a comment", committed.Comment);
        Assert.Null(committed.DraftName);
        Assert.Throws<KeyNotFoundException>(() => repository.GetDraft("working"));
    }

    [Fact]
    public void FinalizeRollsBackEverythingWhenTheRevisionInsertHitsAGenuinePrimaryKeyCollision()
    {
        var project = new ProjectLocator(Path.Combine(_root, "finalize-rollback.fwdata"), "finalize-rollback");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "finalize-rollback.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var id = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", id, "{\"draft\":true}");

        // Pre-seeds Finalize's own (ProposalId, IntentDigest) with different content: a real PK collision.
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO ProposalRevisions (ProposalId, IntentDigest, ProposalJson, CreatedUtc) " +
                "VALUES ($id, $digest, X'7B7D', '2026-08-27T00:00:00Z');";
            command.Parameters.AddWithValue("$id", id.Value);
            command.Parameters.AddWithValue("$digest", "sha256:collide");
            command.ExecuteNonQuery();
        }

        Assert.ThrowsAny<SqliteException>(
            () => repository.Finalize("working", "sha256:collide", "{\"different\":true}", "a label", "a comment"));

        var draft = repository.GetDraft("working");
        Assert.Equal("{\"draft\":true}", draft.ProposalJson);
        Assert.Null(draft.IntentDigest);
        Assert.Equal("draft", draft.Status);
        using var check = database.OpenConnection();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ProposalRevisions WHERE ProposalId = $id;";
        count.Parameters.AddWithValue("$id", id.Value);
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public void ListIncludesDraftsMarkedByDraftNameAlongsideCommittedProposals()
    {
        var project = new ProjectLocator(Path.Combine(_root, "list.fwdata"), "list");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "list.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var committedId = CanonicalId.Mint("proposal/");
        repository.SaveRevision(new ProposalRevisionRecord(
            committedId, "sha256:committed", "{\"proposalId\":\"" + committedId.Value + "\"}", "proposed", null, null, null));
        var draftId = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", draftId, "{\"draft\":true}");

        var all = repository.List(new ProposalListFilter());
        Assert.Equal(2, all.Count);
        var draftRecord = Assert.Single(all, p => p.ProposalId == draftId);
        Assert.Equal("working", draftRecord.DraftName);
        Assert.Null(draftRecord.IntentDigest);
        var committedRecord = Assert.Single(all, p => p.ProposalId == committedId);
        Assert.Null(committedRecord.DraftName);
        Assert.Equal("sha256:committed", committedRecord.IntentDigest);

        var onlyDrafts = repository.ListDrafts();
        Assert.Single(onlyDrafts);
        Assert.Equal(draftId, onlyDrafts[0].ProposalId);
    }

    [Fact]
    public void DiscardDraftRemovesANeverFinalizedDraftAndFreesItsNameImmediately()
    {
        var project = new ProjectLocator(Path.Combine(_root, "discard.fwdata"), "discard");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "discard.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var id = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", id, "{\"draft\":true}");

        repository.DiscardDraft("working");

        Assert.Throws<KeyNotFoundException>(() => repository.GetDraft("working"));
        Assert.False(repository.DraftNameExists("working"));
        // The name is free again, not merely absent from a listing: a fresh CreateDraft must succeed.
        repository.CreateDraft("working", CanonicalId.Mint("proposal/"), "{\"draft\":true}");
        Assert.True(repository.DraftNameExists("working"));
    }

    [Fact]
    public void DiscardDraftRevertsADraftReopenedFromAFinalizedProposalAndKeepsItsHistoryIntact()
    {
        var project = new ProjectLocator(Path.Combine(_root, "discard-reopened.fwdata"), "discard-reopened");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "discard-reopened.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);
        var id = CanonicalId.Mint("proposal/");
        repository.SaveRevision(new ProposalRevisionRecord(
            id, "sha256:committed", "{\"proposalId\":\"" + id.Value + "\"}", "proposed", null, null, null));
        repository.SaveDecision(new DecisionRecord(
            id, "sha256:committed", "approved", "human", "linguist", null, "2026-08-27T00:00:00Z"));
        repository.ReopenAsDraft(id, "reopened", "{\"draft\":true}");

        var wasReopened = repository.DiscardDraft("reopened");

        Assert.True(wasReopened);
        // The draft name is gone, and everything committed behind the Proposal is exactly as it was.
        Assert.False(repository.DraftNameExists("reopened"));
        var record = repository.Get(id);
        Assert.Equal("sha256:committed", record.IntentDigest);
        Assert.Equal("{\"proposalId\":\"" + id.Value + "\"}", record.ProposalJson);
        Assert.Null(record.DraftName);
        Assert.NotNull(record.Decision);
        Assert.Equal("approved", record.Decision!.Outcome);
        // The name is free again, not merely absent from a listing: a fresh CreateDraft must succeed.
        repository.CreateDraft("reopened", CanonicalId.Mint("proposal/"), "{\"draft\":true}");
        Assert.True(repository.DraftNameExists("reopened"));
    }

    [Fact]
    public void DiscardDraftOfAnUnknownNameThrowsKeyNotFound()
    {
        var project = new ProjectLocator(Path.Combine(_root, "discard-missing.fwdata"), "discard-missing");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "discard-missing.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new ProposalRepository(database);

        Assert.Throws<KeyNotFoundException>(() => repository.DiscardDraft("nope"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }
}
