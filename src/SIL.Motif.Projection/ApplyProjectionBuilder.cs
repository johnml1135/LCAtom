using SIL.Motif.Contract.Responses;
using SIL.Motif.Model.Receipts;
using System.Collections.Generic;

namespace SIL.Motif.Projection;

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
