using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;

namespace SIL.Motif.Model.Effects;

/// <summary>
/// The single rule deciding which of a Proposal's targets belong in its footprint: every target
/// except one the Proposal itself mints. Both sides of the drift check consult this and nothing else,
/// so the digest a Dry Run binds and the digest Apply re-reads cannot disagree about what was measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>An object the Proposal creates has no prior state to have drifted from.</b> Its "before" is that
/// it did not exist, which no outside change can alter — nothing can touch an object this Proposal is
/// the one bringing into existence. Excluding it is therefore sound rather than convenient.
/// </para>
/// <para>
/// Leaving it in was not merely wrong, it was wrong differently on each side. A Dry Run runs
/// sequentially inside one open unit of work, so by the time it reaches an operation targeting a
/// just-minted entity that entity exists, and it recorded the freshly created value as a baseline.
/// Apply's pre-flight reads every target before any operation runs, finds nothing there, and threw.
/// The result was a Proposal that dry-ran cleanly and then failed at apply — pinned by
/// `OneOperationTargetsAMintedEntity_TheDigestsAgree`.
/// </para>
/// <para>
/// The rule is deliberately order-independent: an id minted anywhere in the Proposal is excluded,
/// not merely one minted by an earlier operation. Both sides must reach the same answer from the same
/// Proposal, and a rule that depends on position is one more thing that can be read two ways.
/// </para>
/// </remarks>
public static class FootprintPlan
{
    /// <summary>
    /// The entity ids <paramref name="proposal"/> mints, which are exactly the targets excluded from
    /// its footprint digest.
    /// </summary>
    public static HashSet<CanonicalId> TargetsMintedWithinProposal(Proposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        var minted = new HashSet<CanonicalId>();
        foreach (var operation in proposal.Operations)
        {
            if (operation.EntityId is { } entityId)
                minted.Add(entityId);
        }

        return minted;
    }

    /// <summary>
    /// Whether an operation's own current state should be read for the footprint. False exactly when
    /// its target is one this Proposal mints.
    /// </summary>
    public static bool ParticipatesInFootprint(OperationEnvelope operation, HashSet<CanonicalId> mintedTargets)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (mintedTargets is null) throw new ArgumentNullException(nameof(mintedTargets));

        return operation.Target is not { } target || !mintedTargets.Contains(target);
    }
}
