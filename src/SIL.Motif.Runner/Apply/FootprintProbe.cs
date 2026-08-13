using System.Collections.Generic;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Reads a Proposal's CURRENT footprint — the live, pre-mutation state of every target its
/// operations touch — without mutating anything or opening any unit of work. Used by
/// <see cref="ProposalApplier.Apply"/>'s pre-flight drift check (ADR 0004 decision 3): a bare read is
/// legal at any transaction state (ADR 0006 decision 1), so this probe can run before Apply opens its
/// own committing unit of work.
/// </summary>
/// <remarks>
/// Mirrors the dispatch in <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/> and
/// <see cref="ProposalApplier"/> (an <see cref="OperationHandlerRegistry"/> lookup per operation), but
/// calls each handler's non-mutating <c>ReadCurrentFootprint</c> rather than its
/// resolve/snapshot/lower/snapshot sequence.
/// </remarks>
public static class FootprintProbe
{
    public static string ComputeCurrentFootprintDigest(LcmCache cache, Proposal proposal)
    {
        var entries = new List<ExpectedEffect>();
        var mintedTargets = FootprintPlan.TargetsMintedWithinProposal(proposal);

        foreach (var operation in proposal.Operations)
        {
            // A target this Proposal mints does not exist yet, and has no prior state to have drifted from.
            if (!FootprintPlan.ParticipatesInFootprint(operation, mintedTargets)) continue;

            var handler = OperationHandlerRegistry.Resolve(operation.Kind, "Apply's footprint pre-flight");
            entries.Add(handler.ReadCurrentFootprint(cache, operation));
        }

        return FootprintDigest.Compute(entries);
    }
}
