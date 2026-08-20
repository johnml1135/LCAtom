using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Runner.DryRun;

/// <summary>
/// A validated prerequisite closure for one requested Proposal. Construction verifies declared
/// dependencies and derives the deterministic execution order, so callers cannot supply positional
/// authority to <see cref="ProposalDryRunner"/>.
/// </summary>
public sealed class PrerequisiteExecutionPlan
{
    private static readonly IComparer<string> ProposalIdComparer = Comparer<string>.Create(
        (left, right) => CanonicalId.Parse(left).CompareTo(CanonicalId.Parse(right)));

    private PrerequisiteExecutionPlan(Proposal requested, IList<Proposal> prerequisites)
    {
        Requested = requested;
        Prerequisites = new ReadOnlyCollection<Proposal>(prerequisites);
    }

    /// <summary>The requested Proposal evaluated after scratch preparation.</summary>
    public Proposal Requested { get; }

    /// <summary>The un-applied prerequisite Proposals in deterministic topological order.</summary>
    public IReadOnlyList<Proposal> Prerequisites { get; }

    /// <summary>
    /// Validates <paramref name="candidates"/> as the complete un-applied closure of
    /// <paramref name="requested"/> and derives byte-ordinally stable topological order. IDs in
    /// <paramref name="appliedProposalIds"/> satisfy and cut off their dependency branches.
    /// </summary>
    public static PrerequisiteExecutionPlan Create(
        Proposal requested,
        IReadOnlyCollection<Proposal> candidates,
        IReadOnlyCollection<Guid> appliedProposalIds)
    {
        if (requested is null) throw new ArgumentNullException(nameof(requested));
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (appliedProposalIds is null) throw new ArgumentNullException(nameof(appliedProposalIds));

        var applied = new HashSet<Guid>(appliedProposalIds);
        var proposals = new Dictionary<string, Proposal>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var id = candidate.ProposalId.Value;
            if (id == requested.ProposalId.Value)
                throw new InvalidOperationException($"Prerequisite closure repeats requested Proposal {id}.");
            if (applied.Contains(candidate.ProposalId.ToGuid()))
                throw new InvalidOperationException($"Applied Proposal {id} must not be executable scratch preparation.");
            if (proposals.ContainsKey(id))
                throw new InvalidOperationException($"Prerequisite closure contains Proposal {id} more than once.");

            proposals.Add(id, candidate);
        }

        proposals.Add(requested.ProposalId.Value, requested);
        ValidateClosure(requested, proposals, applied, candidates.Count);
        ValidateAcyclic(requested, proposals, applied);
        return new PrerequisiteExecutionPlan(requested, TopologicalOrder(requested, proposals, applied));
    }

    private static void ValidateClosure(
        Proposal requested,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds,
        int candidateCount)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(ProposalIdComparer);
        VisitReachable(requested, proposals, appliedProposalIds, reachable, missing);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Prerequisite execution closure is missing Proposal(s): " + string.Join(", ", missing) + ".");
        }

        if (reachable.Count - 1 != candidateCount)
        {
            var extraneous = proposals.Keys
                .Where(id => id != requested.ProposalId.Value && !reachable.Contains(id))
                .OrderBy(id => id, ProposalIdComparer);
            throw new InvalidOperationException(
                "Prerequisite execution closure contains unreachable Proposal(s): " +
                string.Join(", ", extraneous) + ".");
        }
    }

    private static void VisitReachable(
        Proposal proposal,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds,
        ISet<string> reachable,
        ISet<string> missing)
    {
        if (!reachable.Add(proposal.ProposalId.Value)) return;

        foreach (var required in proposal.Requires.Distinct())
        {
            if (appliedProposalIds.Contains(required.ToGuid())) continue;
            if (!proposals.TryGetValue(required.Value, out var prerequisite))
            {
                missing.Add(required.Value);
                continue;
            }

            VisitReachable(prerequisite, proposals, appliedProposalIds, reachable, missing);
        }
    }

    private static void ValidateAcyclic(
        Proposal requested,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();
        VisitForCycle(requested, proposals, appliedProposalIds, states, path);
    }

    private static void VisitForCycle(
        Proposal proposal,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds,
        IDictionary<string, VisitState> states,
        IList<string> path)
    {
        var id = proposal.ProposalId.Value;
        if (states.TryGetValue(id, out var state))
        {
            if (state == VisitState.Visited) return;

            var cycleStart = path.IndexOf(id);
            var cycle = path.Skip(cycleStart).Concat(new[] { id });
            throw new InvalidOperationException(
                "The reachable prerequisite graph contains a cycle: " + string.Join(" -> ", cycle) + ".");
        }

        states[id] = VisitState.Visiting;
        path.Add(id);
        foreach (var required in proposal.Requires
                     .Distinct()
                     .OrderBy(value => value.Value, ProposalIdComparer))
        {
            if (appliedProposalIds.Contains(required.ToGuid())) continue;
            VisitForCycle(proposals[required.Value], proposals, appliedProposalIds, states, path);
        }

        path.RemoveAt(path.Count - 1);
        states[id] = VisitState.Visited;
    }

    private static IList<Proposal> TopologicalOrder(
        Proposal requested,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds)
    {
        var outgoing = proposals.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegrees = proposals.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var proposal in proposals.Values)
        {
            foreach (var required in proposal.Requires.Distinct())
            {
                if (appliedProposalIds.Contains(required.ToGuid())) continue;
                outgoing[required.Value].Add(proposal.ProposalId.Value);
                indegrees[proposal.ProposalId.Value]++;
            }
        }

        var ready = new SortedSet<string>(
            indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            ProposalIdComparer);
        var ordered = new List<Proposal>(proposals.Count - 1);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            if (id != requested.ProposalId.Value) ordered.Add(proposals[id]);
            foreach (var dependentId in outgoing[id].OrderBy(value => value, ProposalIdComparer))
            {
                indegrees[dependentId]--;
                if (indegrees[dependentId] == 0) ready.Add(dependentId);
            }
        }

        return ordered;
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
