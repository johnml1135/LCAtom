using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Cli.Store;

internal static class PrerequisiteClosurePlanner
{
    public static IReadOnlyList<Proposal> Plan(
        Proposal requested,
        Func<string, Proposal> resolve,
        IReadOnlyCollection<Guid> appliedProposalIds)
    {
        var proposals = new Dictionary<string, Proposal>(StringComparer.Ordinal)
        {
            [requested.ProposalId.Value] = requested,
        };
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();
        Visit(requested, resolve, proposals, states, path);

        var outgoing = proposals.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegrees = proposals.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var proposal in proposals.Values)
        {
            foreach (var requiredId in proposal.Requires.Select(id => id.Value).Distinct(StringComparer.Ordinal))
            {
                outgoing[requiredId].Add(proposal.ProposalId.Value);
                indegrees[proposal.ProposalId.Value]++;
            }
        }

        var ready = new SortedSet<string>(
            indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<Proposal>(proposals.Count - 1);
        var applied = new HashSet<Guid>(appliedProposalIds);
        var executionIds = FindExecutionIds(requested, proposals, applied);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            var proposal = proposals[id];
            if (executionIds.Contains(id))
                ordered.Add(proposal);

            foreach (var dependentId in outgoing[id].OrderBy(value => value, StringComparer.Ordinal))
            {
                indegrees[dependentId]--;
                if (indegrees[dependentId] == 0) ready.Add(dependentId);
            }
        }

        return ordered;
    }

    private static ISet<string> FindExecutionIds(
        Proposal requested,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds)
    {
        var executionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requiredId in requested.Requires.Select(id => id.Value).Distinct(StringComparer.Ordinal))
            AddForExecution(requiredId, proposals, appliedProposalIds, executionIds);

        return executionIds;
    }

    private static void AddForExecution(
        string id,
        IReadOnlyDictionary<string, Proposal> proposals,
        ISet<Guid> appliedProposalIds,
        ISet<string> executionIds)
    {
        var proposal = proposals[id];
        if (appliedProposalIds.Contains(proposal.ProposalId.ToGuid()) || !executionIds.Add(id)) return;

        foreach (var requiredId in proposal.Requires.Select(required => required.Value))
            AddForExecution(requiredId, proposals, appliedProposalIds, executionIds);
    }

    private static void Visit(
        Proposal proposal,
        Func<string, Proposal> resolve,
        IDictionary<string, Proposal> proposals,
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
        foreach (var requiredId in proposal.Requires
                     .Select(required => required.Value)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(required => required, StringComparer.Ordinal))
        {
            if (!proposals.TryGetValue(requiredId, out var prerequisite))
            {
                prerequisite = resolve(requiredId);
                proposals.Add(requiredId, prerequisite);
            }

            Visit(prerequisite, resolve, proposals, states, path);
        }

        path.RemoveAt(path.Count - 1);
        states[id] = VisitState.Visited;
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
