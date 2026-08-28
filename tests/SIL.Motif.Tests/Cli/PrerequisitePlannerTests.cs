using System.Text.Json;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.Store;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

public sealed class PrerequisitePlannerTests : IDisposable
{
    private readonly string _fwDataPath = PlaceholderProject.Create("SIL.Motif.Tests.PrerequisitePlanner");

    public void Dispose()
    {
        var projectDir = Path.GetDirectoryName(_fwDataPath)!;
        if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
    }

    [Fact]
    public void Plan_MissingTransitiveProposal_NamesTheMissingId()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var missing = CanonicalId.Mint();
        var prerequisite = Proposal(requires: new[] { missing });
        var requested = Proposal(requires: new[] { prerequisite.ProposalId });
        Store(repository, prerequisite);
        Store(repository, requested);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(repository, requested));

        Assert.Contains(missing.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_DirectCycle_ReportsTheCompleteCyclePath()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var firstId = CanonicalId.Mint();
        var secondId = CanonicalId.Mint();
        var first = Proposal(firstId, secondId);
        var second = Proposal(secondId, firstId);
        Store(repository, first);
        Store(repository, second);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(repository, first));

        Assert.Contains(
            $"{firstId.Value} -> {secondId.Value} -> {firstId.Value}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_TransitiveCycle_ReportsOnlyTheReachableCycleAsACompletePath()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var cycleFirstId = CanonicalId.Mint();
        var cycleSecondId = CanonicalId.Mint();
        var cycleFirst = Proposal(cycleFirstId, cycleSecondId);
        var cycleSecond = Proposal(cycleSecondId, cycleFirstId);
        var requested = Proposal(requires: new[] { cycleFirstId });
        Store(repository, cycleFirst);
        Store(repository, cycleSecond);
        Store(repository, requested);

        var ex = Assert.ThrowsAny<Exception>(() => Plan(repository, requested));

        Assert.Contains(
            $"{cycleFirstId.Value} -> {cycleSecondId.Value} -> {cycleFirstId.Value}",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_IndependentPrerequisites_UsesByteOrdinalProposalIdOrder()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var first = Proposal();
        var second = Proposal();
        var requested = Proposal(requires: new[] { second.ProposalId, first.ProposalId });
        Store(repository, first);
        Store(repository, second);
        Store(repository, requested);

        var plan = Plan(repository, requested);

        Assert.Equal(
            new[] { first.ProposalId.Value, second.ProposalId.Value }.OrderBy(id => id, StringComparer.Ordinal),
            plan.Select(proposal => proposal.ProposalId.Value));
    }

    [Fact]
    public void Plan_Diamond_IncludesTheSharedAncestorExactlyOnce()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var ancestor = Proposal();
        var left = Proposal(requires: new[] { ancestor.ProposalId });
        var right = Proposal(requires: new[] { ancestor.ProposalId });
        var requested = Proposal(requires: new[] { left.ProposalId, right.ProposalId });
        Store(repository, ancestor);
        Store(repository, left);
        Store(repository, right);
        Store(repository, requested);

        var plan = Plan(repository, requested);

        Assert.Equal(3, plan.Count);
        Assert.Equal(ancestor.ProposalId, plan[0].ProposalId);
        Assert.Equal(1, plan.Count(proposal => proposal.ProposalId == ancestor.ProposalId));
    }

    [Fact]
    public void Plan_AppliedPrerequisite_SatisfiesItsAncestryForScratchPreparation()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var ancestor = Proposal();
        var prerequisite = Proposal(requires: new[] { ancestor.ProposalId });
        var requested = Proposal(requires: new[] { prerequisite.ProposalId });
        Store(repository, ancestor);
        Store(repository, prerequisite);
        Store(repository, requested);

        var plan = Plan(repository, requested, prerequisite.ProposalId.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_DoesNotRequireAStoredManifest()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var applied = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied });

        var plan = Plan(repository, requested, applied.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_DoesNotReadItsCorruptManifest()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var applied = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied });
        StoreCorruptCommittedRow(database, applied);

        var plan = Plan(repository, requested, applied.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedPrerequisite_CutsOffItsCorruptAncestor()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var ancestor = CanonicalId.Mint();
        var applied = Proposal(requires: new[] { ancestor });
        var requested = Proposal(requires: new[] { applied.ProposalId });
        Store(repository, applied);
        StoreCorruptCommittedRow(database, ancestor);

        var plan = Plan(repository, requested, applied.ProposalId.ToGuid());

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_AppliedCutoff_DoesNotHideAMissingUnappliedBranch()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var applied = CanonicalId.Mint();
        var missing = CanonicalId.Mint();
        var requested = Proposal(requires: new[] { applied, missing });

        var ex = Assert.ThrowsAny<Exception>(() => Plan(repository, requested, applied.ToGuid()));

        Assert.Contains(missing.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_AppliedCutoff_StillExecutesASeparatelyDeclaredUnappliedBranch()
    {
        using var database = ProjectMotifDatabase.Open(_fwDataPath);
        var repository = new ProposalRepository(database);
        var applied = CanonicalId.Mint();
        var unapplied = Proposal();
        var requested = Proposal(requires: new[] { applied, unapplied.ProposalId });
        Store(repository, unapplied);

        var plan = Plan(repository, requested, applied.ToGuid());

        Assert.Equal(unapplied.ProposalId, Assert.Single(plan).ProposalId);
    }

    private static IReadOnlyList<Proposal> Plan(
        ProposalRepository repository, Proposal requested, params Guid[] appliedProposalIds) =>
        repository.PlanPrerequisites(requested, appliedProposalIds).Prerequisites;

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

    private static void Store(ProposalRepository repository, Proposal proposal)
    {
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
        repository.SaveRevision(new ProposalRevisionRecord(
            proposal.ProposalId, digest, json, "proposed", null, null, null));
    }

    /// <summary>A committed row whose revision cannot be read, proving an applied/cut-off id is unread.</summary>
    private static void StoreCorruptCommittedRow(MotifDatabase database, CanonicalId proposalId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Proposals (ProposalId, CurrentIntentDigest, Status) VALUES ($id, 'sha256:corrupt', 'proposed');";
        command.Parameters.AddWithValue("$id", proposalId.Value);
        command.ExecuteNonQuery();
    }
}
