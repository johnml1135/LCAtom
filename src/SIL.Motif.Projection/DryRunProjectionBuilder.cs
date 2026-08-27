using DryRunResult = SIL.Motif.Model.DryRun.DryRun;
using SIL.Motif.Contract.Responses;
using System.Collections.Generic;

namespace SIL.Motif.Projection;

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
