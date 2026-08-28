using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Store;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli.Store;

/// <summary>
/// A Draft has no committed revision, so it has no intent digest (ADR 0041 decision 3):
/// <see cref="ProposalRecordMapping.ToManifest"/> must carry that absence through as <c>null</c>,
/// never flatten it to <c>""</c> — the sentinel this test exists to keep out.
/// </summary>
public sealed class ProposalRecordMappingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-record-mapping-" + Guid.NewGuid().ToString("N"));

    public ProposalRecordMappingTests() => Directory.CreateDirectory(_root);

    private MotifDatabase OpenDatabase(string name)
    {
        var project = new ProjectLocator(Path.Combine(_root, name + ".fwdata"), name);
        return MotifDatabase.OpenOwned(
            Path.Combine(_root, name + ".motif.db"), project, MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void ToManifest_DraftHasNoCommittedRevision_CurrentIntentDigestIsNull()
    {
        using var database = OpenDatabase("draft-mapping");
        var repository = new ProposalRepository(database);
        var proposalId = CanonicalId.Mint("proposal/");
        repository.CreateDraft("working", proposalId, "{\"proposalId\":\"" + proposalId.Value + "\"}");

        var draft = repository.GetDraft("working");
        var manifest = ProposalRecordMapping.ToManifest(draft);

        Assert.Null(draft.IntentDigest);
        Assert.Null(manifest.CurrentIntentDigest);
    }

    [Fact]
    public void ToManifest_CommittedProposal_CurrentIntentDigestIsUnchanged()
    {
        using var database = OpenDatabase("committed-mapping");
        var repository = new ProposalRepository(database);
        var proposalId = CanonicalId.Mint("proposal/");
        repository.SaveRevision(new ProposalRevisionRecord(
            proposalId, "sha256:committed", "{\"proposalId\":\"" + proposalId.Value + "\"}",
            "proposed", "label", "comment", null));

        var record = repository.Get(proposalId);
        var manifest = ProposalRecordMapping.ToManifest(record);

        Assert.Equal("sha256:committed", manifest.CurrentIntentDigest);
    }

    /// <summary>
    /// <c>list</c> renders Drafts alongside committed Proposals; neither surface names a digest for
    /// either kind of row, so the mapping's own null-vs-empty-string choice does not yet show up in
    /// what a caller reading <c>list</c> sees. Pinned anyway, so a future field added to that surface
    /// starts from a known-good baseline rather than an unexamined one.
    /// </summary>
    [Fact]
    public void List_MixesADraftAndACommittedProposal_TextAndJsonArePinned()
    {
        using var database = OpenDatabase("list-mapping");
        var repository = new ProposalRepository(database);

        var committedId = CanonicalId.Parse("proposal/AAECAwQFBgcICQoLDA0ODw");
        repository.SaveRevision(new ProposalRevisionRecord(
            committedId, "sha256:committed", "{\"proposalId\":\"" + committedId.Value + "\"}",
            "proposed", "Committed label", null, null));

        var draftId = CanonicalId.Parse("proposal/AQIDBAUGBwgJCgsMDQ4PEA");
        repository.CreateDraft("working", draftId, "{\"proposalId\":\"" + draftId.Value + "\"}");

        var manifests = repository.List(new ProposalListFilter())
            .Select(ProposalRecordMapping.ToManifest)
            .ToList();
        var projection = ProposalListProjectionBuilder.Build(manifests);

        var text = CommandTextRenderer.Render(projection);
        var json = ProjectionJson.Serialize(projection);

        var expectedText = string.Join(Environment.NewLine, new[]
        {
            "proposal/AAECAwQFBgcICQoLDA0ODw  proposed  Committed label",
            "proposal/AQIDBAUGBwgJCgsMDQ4PEA  draft     ",
        }) + Environment.NewLine;

        var expectedJson = string.Join(Environment.NewLine, new[]
        {
            "{",
            "  \"proposals\": [",
            "    {",
            "      \"proposalId\": \"proposal/AAECAwQFBgcICQoLDA0ODw\",",
            "      \"status\": \"proposed\",",
            "      \"label\": \"Committed label\"",
            "    },",
            "    {",
            "      \"proposalId\": \"proposal/AQIDBAUGBwgJCgsMDQ4PEA\",",
            "      \"status\": \"draft\"",
            "    }",
            "  ]",
            "}",
        });

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedJson, json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
