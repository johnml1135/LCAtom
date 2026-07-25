using System;
using System.Collections.Generic;
using System.Linq;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Model.Effects;
using SIL.LCAtom.Model.Snapshot;
using SIL.LCAtom.Runner.Operations;
using SIL.LCAtom.Runner.Resolution;
using SIL.LCAtom.Runner.Snapshotting;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using AssessmentModel = SIL.LCAtom.Model.Assessment.Assessment;
using ContractIntentDigest = SIL.LCAtom.Contract.Canonicalization.IntentDigest;

namespace SIL.LCAtom.Runner.Assessment;

/// <summary>
/// Stage C's non-mutating Assess: for each operation, resolves the target, snapshots it before and
/// after applying the lowering inside an open (never-committed) unit of work, diffs the two
/// snapshots into expected effects, then rolls back so the project is left unchanged. See
/// docs/change-set-contract.md, "Assessment", and docs/adr/0006-engine-reality-apply-readback-preflight.md
/// decision 1 (read-back inside the open task sees true, synchronously-applied engine state).
/// </summary>
/// <remarks>
/// Scope is exactly one operation kind (<see cref="LexicalSenseOperationKinds.SetGloss"/>); the
/// dispatch below is a single case deliberately, not a plugin registry, until a second kind exists
/// to justify one. Apply, receipts, and the applied-change log are explicitly out of scope here
/// (Stage D) — this type never commits a unit of work.
/// </remarks>
public static class ChangeSetAssessor
{
    public static AssessmentModel Assess(LcmCache cache, ChangeSetEnvelope changeSet)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (changeSet is null) throw new ArgumentNullException(nameof(changeSet));

        var intentDigest = ContractIntentDigest.Compute(changeSet);
        var effects = new List<ExpectedEffect>();
        var touchedTargets = new List<CanonicalId>();

        var actionHandler = cache.ServiceLocator.GetInstance<IActionHandler>();

        // Deliberately construct the helper directly (not the static UndoableUnitOfWorkHelper.Do,
        // which sets RollBack = false on success) so Dispose always rolls back: Assess must never
        // leave a mutation committed. See docs/adr/0006, decision 3 ("Rollback is not Undo") — the
        // object graph and identity map revert correctly for this scope (a MultiUnicode field has
        // no headword/homograph/monomorphemic derived cache dependent on it).
        using (var undoHelper = new UndoableUnitOfWorkHelper(
            actionHandler, "LCAtom assess (non-committing)", "LCAtom assess (non-committing)"))
        {
            foreach (var operation in changeSet.Operations)
            {
                switch (operation.Kind)
                {
                    case LexicalSenseOperationKinds.SetGloss:
                        effects.Add(AssessSetGloss(cache, operation, touchedTargets));
                        break;

                    default:
                        throw new NotSupportedException(
                            $"Stage C assessment does not support operation kind '{operation.Kind}'.");
                }
            }

            // No undoHelper.RollBack = false: Dispose() rolls the unit of work back unconditionally,
            // so the project is exactly as it was before this call, regardless of success.
        }

        var effectDigest = ExpectedEffectSetDigest.Compute(effects);
        var baselineNote = touchedTargets.Count == 0
            ? "Empty footprint: no operations resolved a target."
            : "Footprint-scoped baseline read back from LibLCM immediately before rollback " +
              $"({touchedTargets.Count} target object(s): " +
              string.Join(", ", touchedTargets.Select(t => t.Value)) + ").";

        return new AssessmentModel(intentDigest, baselineNote, effects, effectDigest);
    }

    private static ExpectedEffect AssessSetGloss(
        LcmCache cache, OperationEnvelope operation, List<CanonicalId> touchedTargets)
    {
        if (operation.Target is not { } targetId)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{LexicalSenseOperationKinds.SetGloss}' " +
                "requires 'target'.");
        }

        if (operation.After is not { } after)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{LexicalSenseOperationKinds.SetGloss}' " +
                "requires 'after'.");
        }

        var (writingSystemTag, text) = SetGlossPayload.Parse(after);

        var target = CanonicalIdResolver.Resolve(cache, targetId);
        if (target is not ILexSense sense)
        {
            throw new InvalidOperationException(
                $"Target '{targetId.Value}' is not a LexSense (it is a {target.GetType().Name}).");
        }

        touchedTargets.Add(targetId);

        var before = GlossAlternatives(LexSenseSnapshotter.Snapshot(cache, sense));

        SetGlossLowering.Apply(cache, sense, writingSystemTag, text);

        var after_ = GlossAlternatives(LexSenseSnapshotter.Snapshot(cache, sense));

        return new ExpectedEffect(targetId, SnapshotFields.LexSenseGloss, before, after_);
    }

    private static IReadOnlyDictionary<string, string> GlossAlternatives(ObjectSnapshot snapshot) =>
        snapshot.MultiUnicodeFields.TryGetValue(SnapshotFields.LexSenseGloss, out var alternatives)
            ? alternatives
            : new Dictionary<string, string>();
}
