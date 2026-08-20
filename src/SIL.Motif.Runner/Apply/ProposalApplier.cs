using System;
using System.Collections.Generic;
using System.Linq;
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
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/>. Applies every operation, reads back the
/// actual effects, and returns a <see cref="Receipt"/> — emitted only after all operations,
/// read-back, and invariant validation succeed and the unit of work commits; no receipt is emitted
/// for a rolled-back application.
/// </summary>
/// <remarks>
/// Dispatch is by <see cref="OperationHandlerRegistry"/> lookup, matching
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/>. Apply never calls
/// <c>LcmCache.ActionHandlerAccessor.Commit()</c>/saves the project itself — the core does not save
/// projects; that is <see cref="SIL.Motif.Host.LcmUtils.FwDataProjectLoader.Save"/>'s job, run by the
/// host after this method returns.
/// </remarks>
public static class ProposalApplier
{
    /// <param name="cache">The already-loaded, already-exclusively-writable project (ADR 0006 decision 4).</param>
    /// <param name="proposal">The Proposal to apply.</param>
    /// <param name="anchor">
    /// The <see cref="BoundDryRunAnchor"/> a prior <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner.Run"/>
    /// call produced against this same baseline (ADR 0004 decision 3). Required: a bare apply with no
    /// bound DryRun is a hard error (<see cref="ApplyPreconditionException"/>), and so is one bound to
    /// a different Proposal's content (<see cref="BoundDryRunAnchor.IntentDigest"/> mismatch) — an
    /// anchor authorizes exactly the content its DryRun evaluated, never a substitute with the same
    /// footprint. Apply re-reads the current footprint and hard-stops with a drift diagnostic if it no
    /// longer matches <see cref="BoundDryRunAnchor.FootprintDigest"/>.
    /// </param>
    /// <param name="applierIdentity">
    /// Opaque, host-supplied applier identity: FieldWorks supplies its configured user when it has
    /// one, an agent supplies its own name (for example <c>linguistic-assistant</c>), and empty is
    /// permitted — the runner is stateless and never infers identity itself. Must not contain
    /// <c>|</c> or a control character, and must be at most
    /// <see cref="AppliedLogFormat.MaxUserLength"/> characters.
    /// </param>
    /// <param name="description">
    /// Free single-line human-facing text for the applied-log entry. May contain <c>|</c>; must not
    /// contain a control character or exceed <see cref="AppliedLogFormat.MaxDescriptionLength"/>
    /// characters. Defaults to empty.
    /// </param>
    /// <remarks>
    /// The whole Proposal runs inside one outer, undoable unit of work — the only rollback left in
    /// Motif, forced by the rule that one Change Set is one atomic unit of work. A rollback can leave
    /// derived caches stale and nothing here can repair that, so the obligation lands on the caller:
    /// <b>if this throws, discard <paramref name="cache"/> and reload the project from the file that
    /// was saved before the Dry Run</b> — strictly stronger than the rollback, since reload also
    /// discards the non-undoable schema phase (ADR 0005) rollback cannot reach (ADR 0016).
    /// </remarks>
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

        // Idempotence first: reading the applied-log needs no unit of work (docs/adr/0006 decision 1).
        if (ProjectAppliedLog.TryFindByProposalId(cache, proposalGuid, out var existingEntry))
        {
            return BuildAlreadyAppliedReceipt(proposal, fullIntentDigest, intentDigestHex, existingEntry!);
        }

        var appliedProposalIds = new HashSet<Guid>(
            ProjectAppliedLog.ReadAll(cache).Select(entry => entry.ProposalId));
        var missingPrerequisites = proposal.Requires
            .Where(required => !appliedProposalIds.Contains(required.ToGuid()))
            .Select(required => required.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(required => required, StringComparer.Ordinal)
            .ToArray();
        if (missingPrerequisites.Length > 0)
        {
            throw new ApplyPreconditionException(
                "Apply refused because the live project's applied history is missing prerequisite " +
                $"Proposal(s): {string.Join(", ", missingPrerequisites)}. This dependency/order " +
                "error cannot be forced; apply every prerequisite first.");
        }

        // One-use authorization (see BoundDryRunAnchor.IntentDigest): reject a substitute Proposal outright.
        if (!string.Equals(anchor.IntentDigest, fullIntentDigest, StringComparison.Ordinal))
        {
            throw new ApplyPreconditionException(
                "This anchor was not bound to the Proposal presented here: its intent digest is " +
                $"{anchor.IntentDigest}, but the supplied Proposal's is {fullIntentDigest}. An anchor " +
                "authorizes apply only for the exact content its DryRun evaluated -- run DryRun again " +
                "against this Proposal and apply with the anchor it produces.");
        }

        // Drift check (docs/adr/0004 decision 3): re-read footprint, hard-stop if it no longer matches the anchor.
        var currentFootprintDigest = FootprintProbe.ComputeCurrentFootprintDigest(cache, proposal);
        if (!string.Equals(currentFootprintDigest, anchor.FootprintDigest, StringComparison.Ordinal))
        {
            // Name both sides and where each came from, so a stale scratch copy is distinguishable from real drift.
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

        // Build and validate the entry before opening a unit of work: bad input fails fast, nothing to roll back.
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

        // One outer unit of work for the whole Proposal; see the remarks above for the failure-recovery contract.
        using (var undoHelper = new UndoableUnitOfWorkHelper(actionHandler, "Motif apply", "Motif apply"))
        {
            foreach (var operation in proposal.Operations)
            {
                var handler = OperationHandlerRegistry.Resolve(operation.Kind, "Stage D apply");
                effects.Add(handler.ApplyAndCaptureEffect(cache, operation, touchedTargets));
            }

            // Exactly one applied-log entry, written inside this same unit of work, so a rollback leaves none.
            ProjectAppliedLog.WriteEntry(cache, logEntry);

            // Success: commit instead of rollback-on-Dispose. Never Save here — that is the host's job after this closes.
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
        // Content check: same proposalId is "already applied" only if the digest matches too; else it's surfaced.
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
