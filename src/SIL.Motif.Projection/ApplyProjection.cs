using System.Collections.Generic;
using SIL.Motif.Model.Receipts;

namespace SIL.Motif.Projection;

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

/// <summary>Shapes a Runner <see cref="Receipt"/> into an <see cref="ApplyProjection"/>.</summary>
public static class ApplyProjectionBuilder
{
    public static ApplyProjection Build(string proposalId, Receipt receipt) => new(
        proposalId,
        receipt.AlreadyApplied,
        receipt.ResultNote,
        EffectProjectionBuilder.Build(receipt.ActualEffects),
        receipt.EffectDigest,
        new AppliedLogEntrySummary(
            receipt.AppliedLogEntry.ProposalId.ToString("D"),
            receipt.AppliedLogEntry.TimestampUtc,
            receipt.AppliedLogEntry.User,
            receipt.AppliedLogEntry.IntentDigest));
}
