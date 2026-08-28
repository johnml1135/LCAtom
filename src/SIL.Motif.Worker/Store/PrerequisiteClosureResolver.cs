using SIL.Motif.Contract.Model;

namespace SIL.Motif.Worker.Store;

internal static class PrerequisiteClosureResolver
{
    public static IReadOnlyCollection<Proposal> Resolve(
        Proposal requested,
        Func<string, Proposal> resolve,
        IReadOnlyCollection<Guid> appliedProposalIds)
    {
        var proposals = new Dictionary<string, Proposal>(StringComparer.Ordinal);
        var applied = new HashSet<Guid>(appliedProposalIds);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(requested, requested, resolve, applied, proposals, visited);
        return new List<Proposal>(proposals.Values);
    }

    private static void Visit(
        Proposal requested,
        Proposal proposal,
        Func<string, Proposal> resolve,
        ISet<Guid> appliedProposalIds,
        IDictionary<string, Proposal> proposals,
        ISet<string> visited)
    {
        if (!visited.Add(proposal.ProposalId.Value)) return;

        foreach (var required in proposal.Requires)
        {
            if (appliedProposalIds.Contains(required.ToGuid())) continue;

            var requiredId = required.Value;
            if (!proposals.TryGetValue(requiredId, out var prerequisite))
            {
                if (requiredId == requested.ProposalId.Value)
                {
                    prerequisite = requested;
                }
                else
                {
                    prerequisite = resolve(requiredId);
                    proposals.Add(requiredId, prerequisite);
                }
            }

            Visit(requested, prerequisite, resolve, appliedProposalIds, proposals, visited);
        }
    }
}
