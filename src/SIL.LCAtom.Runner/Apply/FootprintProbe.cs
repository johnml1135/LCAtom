using System;
using System.Collections.Generic;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Model.Effects;
using SIL.LCAtom.Runner.Operations;
using SIL.LCModel;

namespace SIL.LCAtom.Runner.Apply;

/// <summary>
/// Reads a Change Set's CURRENT footprint — the live, pre-mutation state of every target its
/// operations touch — without mutating anything or opening any unit of work. Used by
/// <see cref="ChangeSetApplier.Apply"/>'s pre-flight drift check (docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
/// decision 3): a bare read is legal at any transaction state
/// (docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 1), so this probe can run
/// before Apply opens its own committing unit of work.
/// </summary>
/// <remarks>
/// Mirrors the dispatch in <see cref="SIL.LCAtom.Runner.Assessment.ChangeSetAssessor"/> and
/// <see cref="ChangeSetApplier"/> (one case per in-scope operation kind), but calls each handler's
/// non-mutating <c>ReadCurrentFootprint</c> rather than its resolve/snapshot/lower/snapshot sequence.
/// </remarks>
public static class FootprintProbe
{
    public static string ComputeCurrentFootprintDigest(LcmCache cache, ChangeSetEnvelope changeSet)
    {
        var entries = new List<ExpectedEffect>();

        foreach (var operation in changeSet.Operations)
        {
            switch (operation.Kind)
            {
                case LexicalSenseOperationKinds.SetGloss:
                    entries.Add(SetGlossOperationHandler.ReadCurrentFootprint(cache, operation));
                    break;

                default:
                    throw new NotSupportedException(
                        $"Apply's footprint pre-flight does not support operation kind '{operation.Kind}'.");
            }
        }

        return FootprintDigest.Compute(entries);
    }
}
