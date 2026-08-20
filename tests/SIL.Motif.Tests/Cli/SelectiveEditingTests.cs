using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SIL.Motif.Cli;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Projection.Store;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

/// <summary>
/// Selective Proposal editing (duplicate, remove, split) — the removal rule decided by
/// ADR 0021, decision 6:
/// <list type="number">
/// <item>a removal with no dependents just happens;</item>
/// <item>a removal that orphans a dependent warns and names every consequence, then requires
/// <c>--force</c>;</item>
/// <item>force never means "guess" — if the consequences cannot be enumerated, the removal is
/// refused outright, not forced;</item>
/// <item><c>proposalId</c> stays frozen and the intent digest moves; removal produces a new
/// revision, never a mutation of an approved one.</item>
/// </list>
/// None of this touches a live LibLCM project — the dependency graph is entirely declared in the
/// Proposal document (<c>dependsOn</c>, <c>entityId</c>/<c>target</c>), so these tests use synthetic
/// canonical ids rather than a live project via
/// <see cref="SIL.Motif.Tests.TestFixtures.PristineProjectFixture"/>, unlike <see cref="ReopenAmendTests"/>.
/// </summary>
public sealed class SelectiveEditingTests : IDisposable
{
    private readonly string _storeDir;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SelectiveEditingTests()
    {
        _storeDir = Path.Combine(
            Path.GetTempPath(), "SIL.Motif.Tests.SelectiveEditing", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_storeDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static string NewTarget() => CanonicalId.Mint().Value;

    private string CommitProposal(string draftName, params string[] setGlossTargets)
    {
        Assert.Equal(0, Commands.New(_storeDir, draftName, "label for " + draftName).ExitCode);
        foreach (var target in setGlossTargets)
        {
            Assert.Equal(
                0,
                Commands.AddSetGloss(_storeDir, draftName, target, "en", "gloss for " + target).ExitCode);
        }
        DraftRationale.Author(
            _storeDir, draftName, "Edit selected lexical entries", "Apply the authored gloss changes to the selected targets.");
        var finalize = Commands.Finalize(_storeDir, draftName);
        Assert.Equal(0, finalize.ExitCode);
        return ExtractProposalId(finalize.Output);
    }

    /// <summary>Stands in for a real dry-run anchor, without a FieldWorks project load per test.</summary>
    private void BindSyntheticAnchor(string proposalId)
    {
        var store = new ProposalStore(_storeDir);
        var manifestPath = store.ManifestPath(proposalId);
        var manifest = JsonSerializer.Deserialize<ManifestDocument>(File.ReadAllText(manifestPath), ManifestJsonOptions)!;
        manifest.Anchor = new BoundDryRunAnchor(
            IntentDigest: "sha256:" + new string('c', 64),
            FootprintDigest: "sha256:" + new string('a', 64),
            EffectDigest: "sha256:" + new string('b', 64),
            RunnerVersion: "1.0.0.0",
            LibLcmVersion: "1.0.0.0",
            ProjectionVersion: "1",
            DryRunAtUtc: "20260101T000000Z");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private BoundDryRunAnchor? ReadAnchor(string proposalId)
    {
        var store = new ProposalStore(_storeDir);
        var manifest = JsonSerializer.Deserialize<ManifestDocument>(
            File.ReadAllText(store.ManifestPath(proposalId)), ManifestJsonOptions)!;
        return manifest.Anchor;
    }

    private DraftDocument ReadDraft(string draftName)
    {
        var store = new ProposalStore(_storeDir);
        return JsonSerializer.Deserialize<DraftDocument>(
            File.ReadAllText(store.DraftPath(draftName)), ManifestJsonOptions)!;
    }

    private static string ExtractProposalId(string output)
    {
        const string marker = "-> Proposal ";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        Assert.True(end > start, $"Could not parse proposalId from output: {output}");
        return output.Substring(start, end - start);
    }

    // ---- remove: no dependents -> just happens, and clears the anchor on amend -----------------

    [Fact]
    public void RemoveOperations_NoDependents_JustHappens_AndClearsAnchorOnAmend()
    {
        var t1 = NewTarget();
        var t2 = NewTarget();
        Assert.Equal(0, Commands.New(_storeDir, "v1", "two independent ops").ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "v1", t1, "en", "gloss1").ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "v1", t2, "en", "gloss2").ExitCode);
        DraftRationale.Author(
            _storeDir, "v1", "Update two independent glosses", "Correct both lexical analyses in one proposal.");
        var finalize = Commands.Finalize(_storeDir, "v1");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);

        // Reopen to get at the real operation ids as recorded in the committed object.
        Assert.Equal(0, Commands.Reopen(_storeDir, "v2", proposalId).ExitCode);
        var reopened = ReadDraft("v2");
        Assert.Equal(2, reopened.Operations.Count);
        var toRemove = reopened.Operations.First(o => o.Target == t2).OperationId;

        BindSyntheticAnchor(proposalId);
        Assert.NotNull(ReadAnchor(proposalId));

        var removeResult = Commands.RemoveOperations(_storeDir, "v2", new[] { toRemove }, force: false);
        Assert.Equal(0, removeResult.ExitCode);
        Assert.DoesNotContain("orphan", removeResult.Output, StringComparison.OrdinalIgnoreCase);

        var afterRemove = ReadDraft("v2");
        Assert.Single(afterRemove.Operations);
        Assert.Equal(t1, afterRemove.Operations[0].Target);

        var amend = Commands.Finalize(_storeDir, "v2");
        Assert.Equal(0, amend.ExitCode);
        Assert.Contains("Amended draft", amend.Output);
        Assert.Equal(proposalId, ExtractProposalId(amend.Output)); // id frozen

        Assert.Null(ReadAnchor(proposalId)); // stale anchor cleared by the edit
    }

    // ---- remove: orphans a dependent -> warns, enumerates, blocked without --force -------------

    [Fact]
    public void RemoveOperations_WithDependent_WarnsAndRefusesWithoutForce()
    {
        var t1 = NewTarget();
        var t2 = NewTarget();
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "v1", t1, "en", "base");
        Assert.Equal(0, add1.ExitCode);
        var op1Id = ExtractOperationId(add1.Output);
        var add2 = Commands.AddSetGloss(_storeDir, "v1", t2, "en", "dependent", new[] { op1Id });
        Assert.Equal(0, add2.ExitCode);
        var op2Id = ExtractOperationId(add2.Output);

        var result = Commands.RemoveOperations(_storeDir, "v1", new[] { op1Id }, force: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("orphan", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(op1Id, result.Output);
        Assert.Contains(op2Id, result.Output);
        Assert.Contains("--force", result.Output);

        // Nothing was mutated: the draft still has both operations.
        var draft = ReadDraft("v1");
        Assert.Equal(2, draft.Operations.Count);
    }

    // ---- remove: --force accepts the enumerated set and cascades to the dependent --------------

    [Fact]
    public void RemoveOperations_WithDependent_ForceCascadesAndClearsAnchor()
    {
        var t1 = NewTarget();
        var t2 = NewTarget();
        var t3 = NewTarget();
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "v1", t1, "en", "base");
        var op1Id = ExtractOperationId(add1.Output);
        var add2 = Commands.AddSetGloss(_storeDir, "v1", t2, "en", "dependent", new[] { op1Id });
        var op2Id = ExtractOperationId(add2.Output);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "v1", t3, "en", "independent").ExitCode);
        DraftRationale.Author(
            _storeDir, "v1", "Update related glosses", "Keep dependent edits together while preserving the independent edit.");

        var finalize = Commands.Finalize(_storeDir, "v1");
        Assert.Equal(0, finalize.ExitCode);
        var proposalId = ExtractProposalId(finalize.Output);
        BindSyntheticAnchor(proposalId);

        Assert.Equal(0, Commands.Reopen(_storeDir, "v2", proposalId).ExitCode);

        var refused = Commands.RemoveOperations(_storeDir, "v2", new[] { op1Id }, force: false);
        Assert.NotEqual(0, refused.ExitCode);

        var forced = Commands.RemoveOperations(_storeDir, "v2", new[] { op1Id }, force: true);
        Assert.Equal(0, forced.ExitCode);
        Assert.Contains(op2Id, forced.Output);

        var afterRemove = ReadDraft("v2");
        Assert.Single(afterRemove.Operations);
        Assert.Equal(t3, afterRemove.Operations[0].Target);

        var amend = Commands.Finalize(_storeDir, "v2");
        Assert.Equal(0, amend.ExitCode);
        Assert.Null(ReadAnchor(proposalId));
    }

    // ---- remove: transitive dependents are enumerated, not just the direct one -----------------

    [Fact]
    public void RemoveOperations_TransitiveDependents_AllEnumerated()
    {
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "a");
        var op1Id = ExtractOperationId(add1.Output);
        var add2 = Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "b", new[] { op1Id });
        var op2Id = ExtractOperationId(add2.Output);
        var add3 = Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "c", new[] { op2Id });
        var op3Id = ExtractOperationId(add3.Output);

        var result = Commands.RemoveOperations(_storeDir, "v1", new[] { op1Id }, force: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(op2Id, result.Output);
        Assert.Contains(op3Id, result.Output); // transitive: op3 depends on op2 depends on op1

        var forced = Commands.RemoveOperations(_storeDir, "v1", new[] { op1Id }, force: true);
        Assert.Equal(0, forced.ExitCode);
        Assert.Empty(ReadDraft("v1").Operations);
    }

    // ---- remove: cascading delete cannot be enumerated -> refused even with --force ------------

    [Fact]
    public void RemoveOperations_CascadingDelete_RefusedEvenWithForce()
    {
        var entryTarget = NewTarget();
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        var addDelete = Commands.AddDeleteLexemeForm(_storeDir, "v1", entryTarget);
        Assert.Equal(0, addDelete.ExitCode);
        var deleteOpId = ExtractOperationId(addDelete.Output);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "unrelated").ExitCode);

        var withoutForce = Commands.RemoveOperations(_storeDir, "v1", new[] { deleteOpId }, force: false);
        Assert.NotEqual(0, withoutForce.ExitCode);
        Assert.Contains("cascading delete", withoutForce.Output, StringComparison.OrdinalIgnoreCase);

        var withForce = Commands.RemoveOperations(_storeDir, "v1", new[] { deleteOpId }, force: true);
        Assert.NotEqual(0, withForce.ExitCode);
        Assert.Contains("cascading delete", withForce.Output, StringComparison.OrdinalIgnoreCase);

        // Refused either way: nothing was mutated.
        Assert.Equal(2, ReadDraft("v1").Operations.Count);
    }

    [Fact]
    public void RemoveOperations_UnknownOperationId_Fails()
    {
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "a").ExitCode);

        var bogus = CanonicalId.Mint().Value;
        var result = Commands.RemoveOperations(_storeDir, "v1", new[] { bogus }, force: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(bogus, result.Output);
    }

    [Fact]
    public void AddSetGloss_DependsOnUnknownOperation_Fails()
    {
        Assert.Equal(0, Commands.New(_storeDir, "v1", null).ExitCode);
        var bogus = CanonicalId.Mint().Value;
        var result = Commands.AddSetGloss(_storeDir, "v1", NewTarget(), "en", "a", new[] { bogus });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(bogus, result.Output);
    }

    // ---- duplicate: fresh identity, copies content, leaves the source untouched ----------------

    [Fact]
    public void Duplicate_CreatesNewProposalId_CopiesOperations_SourceUnchanged()
    {
        var t1 = NewTarget();
        var t2 = NewTarget();
        var sourceId = CommitProposal("source", t1, t2);
        BindSyntheticAnchor(sourceId);

        var duplicate = Commands.Duplicate(_storeDir, sourceId, "dup");
        Assert.Equal(0, duplicate.ExitCode);
        Assert.DoesNotContain(sourceId, duplicate.Output.Split('\n').First(l => l.Contains("proposalId")));

        var dupDraft = ReadDraft("dup");
        Assert.Equal(2, dupDraft.Operations.Count);
        Assert.NotEqual(sourceId, dupDraft.ProposalId);
        Assert.Contains(dupDraft.Operations, o => o.Target == t1);
        Assert.Contains(dupDraft.Operations, o => o.Target == t2);

        // The source Proposal's manifest (including its anchor) is untouched by duplicating it.
        Assert.NotNull(ReadAnchor(sourceId));

        var dupFinalize = Commands.Finalize(_storeDir, "dup");
        Assert.Equal(0, dupFinalize.ExitCode);
        Assert.Contains("Finalized draft", dupFinalize.Output); // a first commit, not an amend
        var dupProposalId = ExtractProposalId(dupFinalize.Output);
        Assert.NotEqual(sourceId, dupProposalId);
        Assert.Null(ReadAnchor(dupProposalId)); // brand-new Proposal, never dry-run yet
        Assert.NotNull(ReadAnchor(sourceId)); // still untouched
    }

    [Fact]
    public void Duplicate_UnknownProposalId_Fails()
    {
        var bogus = CanonicalId.Mint().Value;
        var result = Commands.Duplicate(_storeDir, bogus, "dup");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- split: partitions operations into new Proposals, source untouched ---------------------

    /// <summary>
    /// Models "keep only rules 1 and 4, not 5" as a split into two successor Proposals rather than a
    /// discard, since split's job is partitioning, not dropping — that composes with <c>remove</c>: split,
    /// then reopen+remove on whichever fragment should drop rule 5.
    /// </summary>
    [Fact]
    public void Split_PartitionsOperationsIntoNewProposals_SourceUnchanged()
    {
        Assert.Equal(0, Commands.New(_storeDir, "source", "three rules").ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "rule1");
        var op1Id = ExtractOperationId(add1.Output);
        var add2 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "rule4");
        var op2Id = ExtractOperationId(add2.Output);
        var add3 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "rule5");
        var op3Id = ExtractOperationId(add3.Output);
        DraftRationale.Author(
            _storeDir, "source", "Partition authored gloss rules", "Split the rules into independently reviewable proposals.");
        var finalize = Commands.Finalize(_storeDir, "source");
        Assert.Equal(0, finalize.ExitCode);
        var sourceId = ExtractProposalId(finalize.Output);
        BindSyntheticAnchor(sourceId);

        var groups = new[]
        {
            new Commands.SplitGroup("keep", new[] { op1Id, op2Id }),
            new Commands.SplitGroup("rest", new[] { op3Id }),
        };
        var split = Commands.Split(_storeDir, sourceId, groups, force: false);
        Assert.Equal(0, split.ExitCode);

        var keepDraft = ReadDraft("keep");
        var restDraft = ReadDraft("rest");
        Assert.Equal(2, keepDraft.Operations.Count);
        Assert.Single(restDraft.Operations);
        Assert.NotEqual(keepDraft.ProposalId, restDraft.ProposalId);
        Assert.NotEqual(sourceId, keepDraft.ProposalId);
        Assert.NotEqual(sourceId, restDraft.ProposalId);

        // Source is untouched.
        Assert.NotNull(ReadAnchor(sourceId));
        var store = new ProposalStore(_storeDir);
        Assert.True(File.Exists(store.ManifestPath(sourceId)));

        Assert.Equal(0, Commands.Finalize(_storeDir, "keep").ExitCode);
        Assert.Equal(0, Commands.Finalize(_storeDir, "rest").ExitCode);
    }

    [Fact]
    public void Split_SeveredDependency_WarnsAndRefusesWithoutForce_ThenForceProceeds()
    {
        Assert.Equal(0, Commands.New(_storeDir, "source", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "a");
        var op1Id = ExtractOperationId(add1.Output);
        var add2 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "b", new[] { op1Id });
        var op2Id = ExtractOperationId(add2.Output);
        DraftRationale.Author(
            _storeDir, "source", "Separate dependent gloss rules", "Preserve declared dependencies while partitioning the proposal.");
        var finalize = Commands.Finalize(_storeDir, "source");
        Assert.Equal(0, finalize.ExitCode);
        var sourceId = ExtractProposalId(finalize.Output);

        var groups = new[]
        {
            new Commands.SplitGroup("groupA", new[] { op1Id }),
            new Commands.SplitGroup("groupB", new[] { op2Id }),
        };

        var refused = Commands.Split(_storeDir, sourceId, groups, force: false);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("sever", refused.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(op1Id, refused.Output);
        Assert.Contains(op2Id, refused.Output);

        var store = new ProposalStore(_storeDir);
        Assert.False(File.Exists(store.DraftPath("groupA")));
        Assert.False(File.Exists(store.DraftPath("groupB")));

        var forced = Commands.Split(_storeDir, sourceId, groups, force: true);
        Assert.Equal(0, forced.ExitCode);

        var groupBDraft = ReadDraft("groupB");
        // The severed dependency is kept exactly as authored: it names an id outside groupB's own operations.
        Assert.Contains(op1Id, groupBDraft.Operations.Single().DependsOn);
    }

    [Fact]
    public void Split_UnassignedOperation_Fails()
    {
        Assert.Equal(0, Commands.New(_storeDir, "source", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "a");
        var op1Id = ExtractOperationId(add1.Output);
        Assert.Equal(0, Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "b").ExitCode);
        DraftRationale.Author(
            _storeDir, "source", "Partition independent gloss rules", "Keep every authored edit assigned to a resulting proposal.");
        var finalize = Commands.Finalize(_storeDir, "source");
        var sourceId = ExtractProposalId(finalize.Output);

        var groups = new[] { new Commands.SplitGroup("only", new[] { op1Id }) };
        var result = Commands.Split(_storeDir, sourceId, groups, force: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not assigned", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Split_DuplicateAssignedOperation_Fails()
    {
        Assert.Equal(0, Commands.New(_storeDir, "source", null).ExitCode);
        var add1 = Commands.AddSetGloss(_storeDir, "source", NewTarget(), "en", "a");
        var op1Id = ExtractOperationId(add1.Output);
        DraftRationale.Author(
            _storeDir, "source", "Partition one gloss rule", "Ensure each rule is assigned to at most one resulting proposal.");
        var finalize = Commands.Finalize(_storeDir, "source");
        var sourceId = ExtractProposalId(finalize.Output);

        var groups = new[]
        {
            new Commands.SplitGroup("groupA", new[] { op1Id }),
            new Commands.SplitGroup("groupB", new[] { op1Id }),
        };
        var result = Commands.Split(_storeDir, sourceId, groups, force: false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("more than one", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractOperationId(string output)
    {
        const string marker = "Added operation '";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in output: {output}");
        start += marker.Length;
        var end = output.IndexOf('\'', start);
        Assert.True(end > start, $"Could not parse operationId from output: {output}");
        return output.Substring(start, end - start);
    }
}
