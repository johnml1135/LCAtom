using System;
using System.Collections.Generic;
using System.Linq;
using SIL.LCAtom.Contract.Ids;
using SIL.LCAtom.Contract.Model;
using SIL.LCAtom.Model.AppliedLog;
using SIL.LCAtom.Model.Assessment;
using SIL.LCAtom.Model.Effects;
using SIL.LCAtom.Model.Snapshot;
using SIL.LCAtom.Runner.Caching;
using SIL.LCAtom.Runner.Operations;
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
/// to justify one. This type never commits a unit of work — it is the non-mutating counterpart to
/// <see cref="SIL.LCAtom.Runner.Apply.ChangeSetApplier"/> (Stage D), which shares the same
/// resolve/snapshot/lower/snapshot sequence via <see cref="SIL.LCAtom.Runner.Operations.SetGlossOperationHandler"/>
/// but commits instead of rolling back, and writes the applied-change log.
/// </remarks>
public static class ChangeSetAssessor
{
    public static AssessmentModel Assess(LcmCache cache, ChangeSetEnvelope changeSet)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (changeSet is null) throw new ArgumentNullException(nameof(changeSet));

        // Refuse outright rather than silently return a possibly-different digest against a cache
        // already known to carry stale derived caches (docs/adr/0006, decision 3). See
        // SIL.LCAtom.Runner.Caching.CacheReusability.
        CacheReusability.EnsureReusable(cache);

        var intentDigest = ContractIntentDigest.Compute(changeSet);
        var effects = new List<ExpectedEffect>();
        var touchedTargets = new List<CanonicalId>();

        // Defect-4 guard: mark the cache poisoned BEFORE running the mutate-then-rollback sequence
        // below when any operation's kind is flagged as possibly touching a forward-only derived
        // cache — it is the rollback itself (not the mutation) that leaves such a cache stale (see
        // DerivedCachePoisoningOperationKinds and docs/adr/0006, decision 3). Dormant today: no
        // operation kind Assess actually dispatches (only setGloss) is flagged.
        foreach (var operation in changeSet.Operations)
        {
            if (DerivedCachePoisoningOperationKinds.MayPoisonDerivedCache(operation.Kind))
            {
                CacheReusability.MarkPoisoned(
                    cache,
                    $"Assess ran a mutate-then-rollback pass over a '{operation.Kind}' operation, " +
                    "flagged as possibly touching a LexEntry headword/homograph or " +
                    "MoStemAllomorph monomorphemic derived cache; UndoStack.Rollback does not " +
                    "refresh those caches (docs/adr/0006, decision 3).");
                break;
            }
        }

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
                        effects.Add(SetGlossOperationHandler.ApplyAndCaptureEffect(cache, operation, touchedTargets));
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

        // Binds a subsequent Apply to exactly this evaluated baseline (docs/adr/0004, decision 3).
        var anchor = new BoundAssessmentAnchor(
            FootprintDigest: FootprintDigest.Compute(effects),
            EffectDigest: effectDigest,
            RunnerVersion: typeof(ChangeSetAssessor).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            LibLcmVersion: typeof(LcmCache).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProjectionVersion: SnapshotFields.ProjectionVersion,
            AssessedAtUtc: AppliedLogFormat.FormatTimestamp(DateTime.UtcNow));

        return new AssessmentModel(intentDigest, baselineNote, effects, effectDigest, anchor);
    }
}
