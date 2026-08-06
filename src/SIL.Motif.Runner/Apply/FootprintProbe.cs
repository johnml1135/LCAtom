using System.Collections.Generic;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.Effects;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Reads a Proposal's CURRENT footprint — the live, pre-mutation state of every target its
/// operations touch — without mutating anything or opening any unit of work. Used by
/// <see cref="ProposalApplier.Apply"/>'s pre-flight drift check (docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
/// decision 3): a bare read is legal at any transaction state
/// (docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 1), so this probe can run
/// before Apply opens its own committing unit of work.
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

        foreach (var operation in proposal.Operations)
        {
            var handler = OperationHandlerRegistry.Resolve(operation.Kind, "Apply's footprint pre-flight");
            entries.Add(handler.ReadCurrentFootprint(cache, operation));
        }

        return FootprintDigest.Compute(entries);
    }
}
