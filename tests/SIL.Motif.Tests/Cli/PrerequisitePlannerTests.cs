using System.Text.Json;
using SIL.Motif.Cli.Store;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Projection.Store;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Cli;

public sealed class PrerequisitePlannerTests : IDisposable
{
    private readonly string _fwDataPath = PlaceholderProject.Create("SIL.Motif.Tests.PrerequisitePlanner");
    private readonly string _storeDir;

    public PrerequisitePlannerTests()
    {
        _storeDir = ProposalStore.ForProject(_fwDataPath).RootDirectory;
    }

    [Fact]
    public void Plan_MissingTransitiveProposal_NamesTheMissingId()
    {
        var store = new ProposalStore(_storeDir);
        var missing = CanonicalId.Mint();
        var prerequisite = Proposal(requires: new[] { missing });
        var requested = Proposal(requires: new[] { prerequisite.ProposalId });
        Store(store, prerequisite);
        Store(store, requested);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(store, requested));

        Assert.Contains(missing.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_DirectCycle_ReportsTheCompleteCyclePath()
    {
        var store = new ProposalStore(_storeDir);
        var firstId = CanonicalId.Mint();
        var secondId = CanonicalId.Mint();
        var first = Proposal(firstId, secondId);
        var second = Proposal(secondId, firstId);
        Store(store, first);
        Store(store, second);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(store, first));

        Assert.Contains(
            $"{firstId.Value} -> {secondId.Value} -> {firstId.Value}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TransitiveCycle_ReportsOnlyTheReachableCycleAsACompletePath()
    {
        var store = new ProposalStore(_storeDir);
        var cycleFirstId = CanonicalId.Mint();
        var cycleSecondId = CanonicalId.Mint();
        var cycleFirst = Proposal(cycleFirstId, cycleSecondId);
        var cycleSecond = Proposal(cycleSecondId, cycleFirstId);
        var requested = Proposal(requires: new[] { cycleFirstId });
        Store(store, cycleFirst);
        Store(store, cycleSecond);
        Store(store, requested);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(store, requested));

        Assert.Contains(
            $"{cycleFirstId.Value} -> {cycleSecondId.Value} -> {cycleFirstId.Value}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_IndependentPrerequisites_UsesByteOrdinalProposalIdOrder()
    {
        var store = new ProposalStore(_storeDir);
        var first = Proposal();
        var second = Proposal();
        var requested = Proposal(requires: new[] { second.ProposalId, first.ProposalId });
        Store(store, first);
        Store(store, second);
        Store(store, requested);

        var plan = Plan(store, requested);

        Assert.Equal(
            new[] { first.ProposalId.Value, second.ProposalId.Value }.OrderBy(id => id, StringComparer.Ordinal),
            plan.Select(proposal => proposal.ProposalId.Value));
    }

    [Fact]
    public void Plan_Diamond_IncludesTheSharedAncestorExactlyOnce()
    {
        var store = new ProposalStore(_storeDir);
        var ancestor = Proposal();
        var left = Proposal(requires: new[] { ancestor.ProposalId });
        var right = Proposal(requires: new[] { ancestor.ProposalId });
        var requested = Proposal(requires: new[] { left.ProposalId, right.ProposalId });
        Store(store, ancestor);
        Store(store, left);
        Store(store, right);
        Store(store, requested);

        var plan = Plan(store, requested);

        Assert.Equal(3, plan.Count);
        Assert.Equal(ancestor.ProposalId, plan[0].ProposalId);
        Assert.Equal(1, plan.Count(proposal => proposal.ProposalId == ancestor.ProposalId));
    }

    [Fact]
    public void Plan_AppliedPrerequisite_SatisfiesItsAncestryForScratchPreparation()
    {
        var store = new ProposalStore(_storeDir);
        var ancestor = Proposal();
        var prerequisite = Proposal(requires: new[] { ancestor.ProposalId });
        var requested = Proposal(requires: new[] { prerequisite.ProposalId });
        Store(store, ancestor);
        Store(store, prerequisite);
        Store(store, requested);

        var plan = Plan(store, requested, prerequisite.ProposalId.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_DoesNotRequireAStoredManifest()
    {
        var store = new ProposalStore(_storeDir);
        var applied = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied });

        var plan = Plan(store, requested, applied.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_DoesNotReadItsCorruptManifest()
    {
        var store = new ProposalStore(_storeDir);
        var applied = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied });
        store.EnsureDirectoriesExist();
        File.WriteAllText(store.ManifestPath(applied.Value), "not json");

        var plan = Plan(store, requested, applied.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_CutsOffItsCorruptAncestor()
    {
        var store = new ProposalStore(_storeDir);
        var ancestor = CanonicalId.Mint();
        var applied = Proposal(requires: new[] { ancestor });
        var requested = Proposal(requires: new[] { applied.ProposalId });
        Store(store, applied);
        File.WriteAllText(store.ManifestPath(ancestor.Value), "not json");

        var plan = Plan(store, requested, applied.ProposalId.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedCutoff_DoesNotHideAMissingUnappliedBranch()
    {
        var store = new ProposalStore(_storeDir);
        var applied = CanonicalId.Mint();
        var missing = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied, missing });

        var ex = Assert.ThrowsAny<Exception>(() => Plan(store, requested, applied.ToGuid()));

        Assert.Contains(missing.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_AppliedCutoff_StillExecutesASeparatelyDeclaredUnappliedBranch()
    {
        var store = new ProposalStore(_storeDir);
        var applied = CanonicalId.Mint();
        var unapplied = Proposal();
        var requested = Proposal(requires: new[] { applied, unapplied.ProposalId });
        Store(store, unapplied);

        var plan = Plan(store, requested, applied.ToGuid());

        Assert.Equal(unapplied.ProposalId, Assert.Single(plan).ProposalId);
    }

    public void Dispose()
    {
        var projectDir = Path.GetDirectoryName(_fwDataPath)!;
        if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
    }

    private static IReadOnlyList<Proposal> Plan(
        ProposalStore store, Proposal requested, params Guid[] appliedProposalIds) =>
        store.PlanPrerequisites(requested, appliedProposalIds).Prerequisites;

    private static Proposal Proposal(CanonicalId? proposalId = null, params CanonicalId[] requires)
    {
        using var after = JsonDocument.Parse("{\"ws\":\"en\",\"text\":\"planner fixture\"}");
        var operation = new OperationEnvelope(
            CanonicalId.Mint(),
            LexicalSenseOperationKinds.SetGloss,
            target: CanonicalId.Mint(),
            after: after.RootElement.Clone());
        return new Proposal(
            new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId ?? CanonicalId.Mint(),
            requires,
            new[] { operation });
    }

    private static void Store(ProposalStore store, Proposal proposal)
    {
        store.EnsureDirectoriesExist();
        var json = JsonSerializer.Serialize(
            new
            {
                contractVersions = proposal.ContractVersions,
                proposalId = proposal.ProposalId.Value,
                requires = proposal.Requires.Select(id => id.Value),
                operations = proposal.Operations.Select(operation => new
                {
                    operationId = operation.OperationId.Value,
                    kind = operation.Kind,
                    target = operation.Target!.Value.Value,
                    after = operation.After!.Value,
                }),
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var digest = IntentDigest.Compute(proposal);
        File.WriteAllText(store.ObjectPath(digest), json);
        File.WriteAllText(
            store.ManifestPath(proposal.ProposalId.Value),
            JsonSerializer.Serialize(new ManifestDocument
            {
                ProposalId = proposal.ProposalId.Value,
                CurrentIntentDigest = digest,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
