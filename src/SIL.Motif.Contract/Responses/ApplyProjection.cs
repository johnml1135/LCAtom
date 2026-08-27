using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>The applied-change-log entry an Apply just wrote (or found already present), for display.</summary>
public sealed record AppliedLogEntrySummary(string ProposalId, string TimestampUtc, string User, string IntentDigest);

/// <summary>
/// The <c>apply</c> report: whether the Proposal actually mutated the project (idempotent no-op
/// otherwise), the resulting effects, and the applied-change-log entry it is recorded under.
/// </summary>
public sealed record ApplyProjection(
    string ProposalId,
    bool AlreadyApplied,
    string ResultNote,
    IReadOnlyList<EffectView> Effects,
    string EffectDigest,
    AppliedLogEntrySummary AppliedLogEntry);
