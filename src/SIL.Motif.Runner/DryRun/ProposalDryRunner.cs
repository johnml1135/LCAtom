using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.AppliedLog;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;
using ContractIntentDigest = SIL.Motif.Contract.Canonicalization.IntentDigest;

namespace SIL.Motif.Runner.DryRun;

/// <summary>
/// Stage C's Run: applies each operation to a <see cref="DryRunScratch"/> — a throwaway copy of the
/// project — snapshotting the target before and after, diffing the two into expected effects, and
/// binding an anchor a later Apply must match. See docs/change-set-contract.md, "DryRun", and
/// docs/adr/0006-engine-reality-apply-readback-preflight.md decision 1 (read-back inside the open task
/// sees true, synchronously-applied engine state).
/// </summary>
/// <remarks>
/// <para>
/// Dispatch is by <see cref="OperationHandlerRegistry"/> lookup, one <see cref="IOperationHandler"/>
/// per registered kind (MOT-4) — before a second kind existed this was "a single case deliberately,
/// not a plugin registry"; the generated catalog is that second kind, many times over. It shares the
/// whole resolve/snapshot/lower/snapshot sequence with <see cref="SIL.Motif.Runner.Apply.ProposalApplier"/>
/// (Stage D) via those same handlers.
/// </para>
/// <para>
/// <b>The one difference from Stage D, and it is the point:</b> Apply mutates the live project inside an
/// undoable unit of work it commits; Run mutates a copy inside a non-undoable one and <b>never reverts
/// it</b>. The copy is discarded instead (<see cref="DryRunScratch.Dispose"/>). That is why this method
/// no longer classifies operations by whether they might leave a derived cache stale: staleness came
/// from <c>UndoStack.Rollback</c> skipping forward-only setter hooks, and there is no rollback here to
/// skip them (docs/adr/0016-scratch-cache-copy-not-undo.md, amended 2026-08-06).
/// </para>
/// <para>
/// <b>The scratch reads the project as it was last saved</b>, so the host must save before calling this
/// or the anchor will describe a baseline the live cache has already moved past — reported at Apply as
/// drift that did not really happen. See ADR 0016, "Save first, and recovery is reload".
/// </para>
/// </remarks>
public static class ProposalDryRunner
{
    public static DryRunModel Run(DryRunScratch scratch, Proposal proposal)
    {
        if (scratch is null) throw new ArgumentNullException(nameof(scratch));
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        var cache = scratch.ConsumeForOneRun();

        var intentDigest = ContractIntentDigest.Compute(proposal);
        var effects = new List<ExpectedEffect>();
        var touchedTargets = new List<CanonicalId>();

        var actionHandler = cache.ServiceLocator.GetInstance<IActionHandler>();

        // Non-undoable, and RollBack cleared before the first mutation rather than after the last one:
        // there is nothing to protect here — the cache is a copy that is about to be thrown away — and
        // a rollback is the one thing that would leave a derived cache stale. So even a *failed* Run
        // ends its task instead of reverting it, and the damage goes out with the scratch.
        using (var unitOfWork = new NonUndoableUnitOfWorkHelper(actionHandler))
        {
            unitOfWork.RollBack = false;

            foreach (var operation in proposal.Operations)
            {
                var handler = OperationHandlerRegistry.Resolve(operation.Kind, "Stage C dryRun");
                effects.Add(handler.ApplyAndCaptureEffect(cache, operation, touchedTargets));
            }
        }

        var effectDigest = ExpectedEffectSetDigest.Compute(effects);
        var baselineNote = touchedTargets.Count == 0
            ? $"Empty footprint: no operations resolved a target. Scratch: {scratch.Provenance}."
            : "Footprint-scoped baseline read back from LibLCM on a single-use scratch copy " +
              $"({scratch.Provenance}; {touchedTargets.Count} target object(s): " +
              string.Join(", ", touchedTargets.Select(t => t.Value)) + ").";

        // Binds a subsequent Apply to exactly this evaluated baseline (docs/adr/0004, decision 3).
        var anchor = new BoundDryRunAnchor(
            FootprintDigest: FootprintDigest.Compute(effects),
            EffectDigest: effectDigest,
            RunnerVersion: typeof(ProposalDryRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            LibLcmVersion: typeof(LcmCache).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProjectionVersion: SnapshotFields.ProjectionVersion,
            DryRunAtUtc: AppliedLogFormat.FormatTimestamp(DateTime.UtcNow));

        return new DryRunModel(intentDigest, baselineNote, effects, effectDigest, anchor);
    }
}
