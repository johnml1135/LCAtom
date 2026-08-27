using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Contract.Responses;

/// <summary>One row of the <c>list</c> report.</summary>
public sealed record ProposalListItem(string ProposalId, string Status, string? Label);

/// <summary>The <c>list</c> report: every Proposal currently in a store.</summary>
public sealed record ProposalListProjection(IReadOnlyList<ProposalListItem> Proposals);
