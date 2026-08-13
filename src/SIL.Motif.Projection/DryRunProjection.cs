using System.Collections.Generic;
using DryRunResult = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Projection;

/// <summary>
/// The <c>dry-run</c> report: the effects a Proposal would have, read back from a live baseline
/// without mutating it.
/// </summary>
public sealed record DryRunProjection(
    string ProposalId,
    string IntentDigest,
    string BaselineNote,
    IReadOnlyList<EffectView> Effects,
    string EffectDigest,
    string FootprintDigest);

/// <summary>Shapes a Runner <see cref="DryRunResult"/> into a <see cref="DryRunProjection"/>.</summary>
public static class DryRunProjectionBuilder
{
    public static DryRunProjection Build(string proposalId, DryRunResult dryRun) => new(
        proposalId,
        dryRun.IntentDigest,
        dryRun.BaselineNote,
        EffectProjectionBuilder.Build(dryRun.ExpectedEffects),
        dryRun.EffectDigest,
        dryRun.Anchor.FootprintDigest);
}
