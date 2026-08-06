using System;
using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.AppliedLog;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Model.Receipts;
using SIL.Motif.Runner.AppliedLog;
using SIL.Motif.Runner.Operations;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Infrastructure;
using ContractIntentDigest = SIL.Motif.Contract.Canonicalization.IntentDigest;

namespace SIL.Motif.Runner.Apply;

/// <summary>
/// Stage D's committing Apply: idempotent, applied-log-writing counterpart to
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/>. See docs/change-set-contract.md,
/// "Application Receipt", and docs/applied-log.md.
/// </summary>
/// <remarks>
/// Dispatch is by <see cref="OperationHandlerRegistry"/> lookup (MOT-4), matching
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/>. Apply never calls
/// <c>LcmCache.ActionHandlerAccessor.Commit()</c>/saves the project itself — the core does not save
/// projects (docs/change-set-contract.md, "Application Receipt"); that is
/// <see cref="SIL.Motif.Host.LcmUtils.FwDataProjectLoader.Save"/>'s job, run by the host after this
/// method returns.
/// </remarks>
public static class ProposalApplier
{
    /// <param name="cache">The already-loaded, already-exclusively-writable project (docs/adr/0006, decision 4).</param>
    /// <param name="proposal">The Proposal to apply.</param>
    /// <param name="anchor">
    /// The <see cref="BoundDryRunAnchor"/> a prior <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner.Run"/>
    /// call produced against this same baseline (docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
    /// decision 3). Required: a bare apply with no bound DryRun is a hard error
    /// (<see cref="ApplyPreconditionException"/>). Apply re-reads the current footprint and hard-stops
    /// with a drift diagnostic if it no longer matches <see cref="BoundDryRunAnchor.FootprintDigest"/>.
    /// </param>
    /// <param name="applierIdentity">
    /// Opaque, host-supplied applier identity (docs/applied-log.md, "Applier identity"). Empty is
    /// permitted; the runner never infers identity. Must not contain <c>|</c> or a control
    /// character, and must be at most <see cref="AppliedLogFormat.MaxUserLength"/> characters.
    /// </param>
    /// <param name="description">
    /// Free single-line human-facing text for the applied-log entry (docs/applied-log.md, "Record
    /// format"). May contain <c>|</c>; must not contain a control character or exceed
    /// <see cref="AppliedLogFormat.MaxDescriptionLength"/> characters. Defaults to empty.
    /// </param>
    public static Receipt Apply(
        LcmCache cache,
        Proposal proposal,
        BoundDryRunAnchor anchor,
        string applierIdentity,
        string description = "")
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        if (applierIdentity is null) throw new ArgumentNullException(nameof(applierIdentity));
        if (description is null) throw new ArgumentNullException(nameof(description));

        if (anchor is null)
        {
            throw new ApplyPreconditionException(
                "Apply requires a prior bound DryRun (docs/adr/0004, decision 3): call " +
                "ProposalDryRunner.Run first and pass its BoundDryRunAnchor here. A bare " +
                "apply with no bound DryRun is a hard error.");
        }

        var proposalGuid = proposal.ProposalId.ToGuid();
        var fullIntentDigest = ContractIntentDigest.Compute(proposal);
        var intentDigestHex = StripSha256Prefix(fullIntentDigest);

        // Idempotence first: reading the applied-log is a plain property read, legal at any
        // transaction state (docs/adr/0006, decision 1), so this check runs before any unit of
        // work opens and, when it hits, this call does nothing else at all — no duplicate entry,
        // no re-mutation. See docs/applied-log.md, "What presence and absence mean".
        if (ProjectAppliedLog.TryFindByProposalId(cache, proposalGuid, out var existingEntry))
        {
            return BuildAlreadyAppliedReceipt(proposal, fullIntentDigest, intentDigestHex, existingEntry!);
        }

        // Drift check (docs/adr/0004, decision 3): re-read the CURRENT footprint — a pure read,
        // legal at any transaction state (docs/adr/0006, decision 1) — and hard-stop rather than
        // proceed if it no longer matches the anchor's baseline. This is the TOCTOU race Terraform's
        // "apply is bound to a saved plan" default-safe workflow closes.
        var currentFootprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(cache, proposal);
        if (!string.Equals(currentFootprintDigest, anchor.FootprintDigest, StringComparison.Ordinal))
        {
            // Name both sides and where each came from. An earlier version reported only the two
            // digests, and when the real fault was a save that had not yet reached disk — so the Dry
            // Run's scratch copy was a stale file — this message sent the investigation after the drift
            // check instead of the save (ADR 0016, "'save' was not a save").
            throw new ApplyPreconditionException(
                "Footprint drift detected: the pre-mutation state of the objects this Proposal touches " +
                "is not what the bound DryRun measured." + Environment.NewLine +
                $"  bound DryRun baseline (measured {anchor.DryRunAtUtc} against a scratch copy of the " +
                $"project file): {anchor.FootprintDigest}" + Environment.NewLine +
                $"  live project now:  {currentFootprintDigest}" + Environment.NewLine +
                "Either something changed the project after the dry run — re-run it and review the new " +
                "effects — or the project was never saved before the dry run copied it, in which case " +
                "the copy was stale and nothing actually drifted.");
        }

        // Build (and validate) the entry to write now, before opening any unit of work, so a bad
        // applierIdentity/description fails fast without performing — then having to roll back — a
        // real mutation.
        var logEntry = new AppliedLogEntry(
            proposalGuid,
            AppliedLogFormat.CurrentFormatVersion,
            AppliedLogFormat.FormatTimestamp(DateTime.UtcNow),
            applierIdentity,
            intentDigestHex,
            description);
        _ = AppliedLogFormat.Format(logEntry); // throws on invalid input; result discarded here

        var effects = new List<ExpectedEffect>();
        var touchedTargets = new List<CanonicalId>();
        var actionHandler = cache.ServiceLocator.GetInstance<IActionHandler>();

        // One outer UndoableUnitOfWorkHelper for the whole Proposal (docs/adr/0005 amends this
        // for the customField/schema family only, which is out of scope here). Constructed
        // directly (not the static .Do helper) so effects can be captured and the log entry written
        // from inside the task.
        //
        // This is the ONLY rollback left in Motif, and it is forced rather than chosen: AGENTS.md
        // rule 4 makes one Change Set one atomic unit of work, so a mid-proposal failure must unwind
        // — a half-applied Proposal is worse than a stale derived cache. Nothing here tries to repair
        // the caches that rollback leaves stale, because nothing can: the obligation lands on the
        // host instead, unconditionally and with no field list to consult —
        //
        //     if Apply throws, discard this LcmCache and reload the project from the file that was
        //     saved before the Dry Run.
        //
        // Reload is strictly stronger than the rollback anyway: it also discards the non-undoable
        // schema phase of docs/adr/0005, which rollback cannot reach. See
        // docs/adr/0016-scratch-cache-copy-not-undo.md, amended 2026-08-06.
        using (var undoHelper = new UndoableUnitOfWorkHelper(actionHandler, "Motif apply", "Motif apply"))
        {
            foreach (var operation in proposal.Operations)
            {
                var handler = OperationHandlerRegistry.Resolve(operation.Kind, "Stage D apply");
                effects.Add(handler.ApplyAndCaptureEffect(cache, operation, touchedTargets));
            }

            // Exactly one applied-log entry, written inside this same unit of work
            // (docs/change-set-contract.md, "Application Receipt"; docs/applied-log.md,
            // "Atomicity") — so a rolled-back apply leaves no entry at all.
            ProjectAppliedLog.WriteEntry(cache, logEntry);

            // Success: commit instead of the default rollback-on-Dispose. Never call Save here
            // — the core does not save projects; that is the host's job, after this unit of
            // work has closed. On any exception RollBack stays true and Dispose unwinds.
            undoHelper.RollBack = false;
        }

        var effectDigest = ExpectedEffectSetDigest.Compute(effects);
        var baselineNote = touchedTargets.Count == 0
            ? "Empty footprint: no operations resolved a target."
            : $"Footprint-scoped baseline read back from LibLCM immediately before commit " +
              $"({touchedTargets.Count} target object(s)).";
        var resultNote =
            $"Applied {proposal.Operations.Count} operation(s) and committed; wrote one applied-log " +
            $"entry ({logEntry.TimestampUtc}, user '{applierIdentity}').";

        return new Receipt(
            proposal.ProposalId,
            fullIntentDigest,
            AlreadyApplied: false,
            baselineNote,
            resultNote,
            effects,
            effectDigest,
            logEntry);
    }

    private static Receipt BuildAlreadyAppliedReceipt(
        Proposal proposal, string fullIntentDigest, string intentDigestHex, AppliedLogEntry existingEntry)
    {
        // Content check (docs/applied-log.md, "What presence and absence mean"): same proposalId
        // is "already applied" only when the recorded intent digest also matches; a differing
        // digest is the same identity referring to different content, surfaced rather than
        // silently reported as clean.
        var contentMatches = string.Equals(existingEntry.IntentDigest, intentDigestHex, StringComparison.Ordinal);

        var resultNote = contentMatches
            ? $"Already applied at {existingEntry.TimestampUtc} by '{existingEntry.User}'; no mutation " +
              "performed (idempotent)."
            : $"Already applied at {existingEntry.TimestampUtc} by '{existingEntry.User}', but the " +
              $"supplied Proposal's intent digest ({intentDigestHex}) differs from the one recorded " +
              $"at that apply ({existingEntry.IntentDigest}) — same proposalId, different content. " +
              "No mutation performed.";

        return new Receipt(
            proposal.ProposalId,
            fullIntentDigest,
            AlreadyApplied: true,
            BaselineNote: "No baseline read: the idempotence check short-circuited before any unit of " +
                          "work opened.",
            ResultNote: resultNote,
            ActualEffects: Array.Empty<ExpectedEffect>(),
            EffectDigest: ExpectedEffectSetDigest.Compute(Array.Empty<ExpectedEffect>()),
            AppliedLogEntry: existingEntry);
    }

    private const string Sha256Prefix = "sha256:";

    private static string StripSha256Prefix(string digest) =>
        digest.StartsWith(Sha256Prefix, StringComparison.Ordinal)
            ? digest.Substring(Sha256Prefix.Length)
            : digest;
}
