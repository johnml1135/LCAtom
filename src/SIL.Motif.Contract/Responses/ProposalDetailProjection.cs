using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Contract.Responses;

/// <summary>One operation inside the <c>show</c> report, with every id rendered as its text form.</summary>
/// <param name="AfterJson"><c>null</c> when the operation carries no after-state at all.</param>
public sealed record ProposalOperationView(
    string OperationId,
    string Kind,
    string? Target,
    string? EntityId,
    IReadOnlyList<string> DependsOn,
    string? AfterJson);

/// <summary>The most recent human or AI verdict on a Proposal, shaped for the <c>show</c> report.</summary>
public sealed record DecisionView(
    string Outcome, string ActorType, string ActorId, string? Comment, string TimestampUtc);

/// <summary>The <c>show</c> report: a Proposal's review state and its full operation list.</summary>
/// <param name="CurrentIntentDigest">
/// <c>null</c> for a Draft: it has no committed revision yet, so it has no digest.
/// </param>
public sealed record ProposalDetailProjection(
    string ProposalId,
    string Status,
    string? Label,
    string? Comment,
    string? CurrentIntentDigest,
    IReadOnlyList<ProposalOperationView> Operations,
    DecisionView? Decision = null,
    string? SupersededBy = null,
    string? ExtensionsJson = null);
