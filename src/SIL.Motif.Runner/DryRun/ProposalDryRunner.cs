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
/// binding an anchor a later Apply must match. See ADR 0006 decision 1 (read-back inside the open
/// task sees true, synchronously-applied engine state).
/// </summary>
/// <remarks>
/// <para>
/// Dispatch is by <see cref="OperationHandlerRegistry"/> lookup, one <see cref="IOperationHandler"/>
/// per registered kind — a registry rather than a switch, since the generated catalog registers many
/// kinds over time. It shares the
/// whole resolve/snapshot/lower/snapshot sequence with <see cref="SIL.Motif.Runner.Apply.ProposalApplier"/>
/// (Stage D) via those same handlers.
/// </para>
/// <para>
/// <b>The one difference from Stage D, and it is the point:</b> Apply mutates the live project inside an
/// undoable unit of work it commits; Run mutates a copy inside a non-undoable one and <b>never reverts
/// it</b>. The copy is discarded instead (<see cref="DryRunScratch.Dispose"/>). That is why this method
/// no longer classifies operations by whether they might leave a derived cache stale: staleness came
/// from <c>UndoStack.Rollback</c> skipping forward-only setter hooks, and there is no rollback here to
/// skip them (ADR 0016).
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
        return Run(scratch, Array.Empty<Proposal>(), proposal);
    }

    /// <summary>
    /// Applies <paramref name="prerequisites"/> to the scratch in declared dependency order, then
    /// evaluates <paramref name="proposal"/> against the prepared state. Prerequisite effects prepare
    /// the baseline only; the returned Dry Run describes the requested Proposal alone.
    /// </summary>
    public static DryRunModel Run(
        DryRunScratch scratch, IReadOnlyCollection<Proposal> prerequisites, Proposal proposal)
    {
        if (scratch is null) throw new ArgumentNullException(nameof(scratch));
        if (prerequisites is null) throw new ArgumentNullException(nameof(prerequisites));
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        var cache = scratch.ConsumeForOneRun();

        var intentDigest = ContractIntentDigest.Compute(proposal);
        var effects = new List<ExpectedEffect>();
        var touchedTargets = new List<CanonicalId>();
        var mintedTargets = FootprintPlan.TargetsMintedWithinProposal(proposal);

        var actionHandler = cache.ServiceLocator.GetInstance<IActionHandler>();

        // Non-undoable; RollBack cleared before the first mutation, not after: rollback would leave the cache stale.
        using (var unitOfWork = new NonUndoableUnitOfWorkHelper(actionHandler))
        {
            unitOfWork.RollBack = false;

            foreach (var prerequisite in OrderPrerequisites(prerequisites))
            {
                var prerequisiteTargets = new List<CanonicalId>();
                foreach (var operation in prerequisite.Operations)
                {
                    var handler = OperationHandlerRegistry.Resolve(operation.Kind, "prerequisite Dry Run preparation");
                    handler.ApplyAndCaptureEffect(cache, operation, prerequisiteTargets);
                }
            }

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
            IntentDigest: intentDigest,
            FootprintDigest: FootprintDigest.Compute(
                effects.Where(e => !mintedTargets.Contains(e.CanonicalId))),
            EffectDigest: effectDigest,
            RunnerVersion: typeof(ProposalDryRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            LibLcmVersion: typeof(LcmCache).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProjectionVersion: SnapshotFields.ProjectionVersion,
            DryRunAtUtc: AppliedLogFormat.FormatTimestamp(DateTime.UtcNow));

        return new DryRunModel(intentDigest, baselineNote, effects, effectDigest, anchor);
    }

    private static IReadOnlyList<Proposal> OrderPrerequisites(IReadOnlyCollection<Proposal> prerequisites)
    {
        var proposals = prerequisites.ToDictionary(proposal => proposal.ProposalId.Value, StringComparer.Ordinal);
        var outgoing = proposals.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegrees = proposals.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var proposal in proposals.Values)
        {
            foreach (var requiredId in proposal.Requires.Select(id => id.Value).Distinct(StringComparer.Ordinal))
            {
                if (!proposals.ContainsKey(requiredId)) continue;

                outgoing[requiredId].Add(proposal.ProposalId.Value);
                indegrees[proposal.ProposalId.Value]++;
            }
        }

        var ready = new SortedSet<string>(
            indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<Proposal>(proposals.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(proposals[id]);
            foreach (var dependentId in outgoing[id].OrderBy(value => value, StringComparer.Ordinal))
            {
                indegrees[dependentId]--;
                if (indegrees[dependentId] == 0) ready.Add(dependentId);
            }
        }

        if (ordered.Count != proposals.Count)
            throw new InvalidOperationException("The supplied prerequisite Proposals contain a dependency cycle.");

        return ordered;
    }
}
