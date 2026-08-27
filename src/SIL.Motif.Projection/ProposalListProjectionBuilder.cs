using SIL.Motif.Contract.Responses;
using SIL.Motif.Projection.Store;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Projection;

/// <summary>Shapes a store's manifests into a <see cref="ProposalListProjection"/>.</summary>
public static class ProposalListProjectionBuilder
{
    public static ProposalListProjection Build(IReadOnlyList<ManifestDocument> manifests) =>
        new(manifests.Select(m => new ProposalListItem(m.ProposalId, m.Status, m.Label)).ToList());
}
