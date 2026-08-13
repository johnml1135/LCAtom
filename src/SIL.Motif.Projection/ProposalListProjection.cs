using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Projection.Store;

namespace SIL.Motif.Projection;

/// <summary>One row of the <c>list</c> report.</summary>
public sealed record ProposalListItem(string ProposalId, string Status, string? Label);

/// <summary>The <c>list</c> report: every Proposal currently in a store.</summary>
public sealed record ProposalListProjection(IReadOnlyList<ProposalListItem> Proposals);

/// <summary>Shapes a store's manifests into a <see cref="ProposalListProjection"/>.</summary>
public static class ProposalListProjectionBuilder
{
    public static ProposalListProjection Build(IReadOnlyList<ManifestDocument> manifests) =>
        new(manifests.Select(m => new ProposalListItem(m.ProposalId, m.Status, m.Label)).ToList());
}
