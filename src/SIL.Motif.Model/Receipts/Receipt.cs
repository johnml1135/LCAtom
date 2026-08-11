using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Model.AppliedLog;
using SIL.Motif.Model.Effects;

namespace SIL.Motif.Model.Receipts;

/// <summary>
/// The result of <c>ProposalApplier.Apply</c>: a minimal slice of the full Application Receipt,
/// which is emitted only after all operations, read-back, and invariant validation succeed and the
/// unit of work commits. Stage D populates exactly these fields; later stages add per-operation
/// outcomes, canonical-ID-to-storage-GUID mappings, and accepted-warnings/version-matrix fields to
/// this same record.
/// </summary>
/// <param name="ProposalId">The Proposal's stable, frozen id (see "Proposal identity vs content digest").</param>
/// <param name="IntentDigest">
/// The full <c>sha256:</c>-prefixed intent digest of the Proposal actually presented to this
/// call (not necessarily the one recorded in <see cref="AppliedLogEntry"/>, which stores only the
/// hex — see <see cref="AppliedLog.AppliedLogFormat"/>).
/// </param>
/// <param name="AlreadyApplied">
/// <c>true</c> when <see cref="ProposalId"/> was already present in the applied-change log: Apply
/// did nothing. This is the log's idempotence check — presence of a Proposal's GUID means Motif
/// already applied it at some point; it does not mean that apply's effects are still present, only
/// that this apply call is a no-op.
/// </param>
/// <param name="BaselineNote">
/// Human-readable description of the footprint-scoped baseline the effects were read back
/// against, or (when <see cref="AlreadyApplied"/>) a note that no baseline was read because the
/// idempotence check short-circuited before any unit of work opened.
/// </param>
/// <param name="ResultNote">
/// Human-readable summary of the outcome: what was applied, or why nothing was (already applied,
/// including a content-check note if the supplied Proposal's intent digest differs from the one
/// recorded at the prior apply — same <c>changeSetId</c> identity, different content, surfaced
/// rather than reported as a clean "already applied").
/// </param>
/// <param name="ActualEffects">
/// The actual effect closure: the observed effects, read back from LibLCM after commit — as
/// distinct from an DryRun's expected effects. Empty when <see cref="AlreadyApplied"/>, because
/// no mutation happened.
/// </param>
/// <param name="EffectDigest"><see cref="ExpectedEffectSetDigest.Compute"/> over <see cref="ActualEffects"/>.</param>
/// <param name="AppliedLogEntry">
/// The applied-change-log entry this Proposal is recorded under: the one just written (a fresh
/// apply) or the one found already present (<see cref="AlreadyApplied"/>).
/// </param>
public sealed record Receipt(
    CanonicalId ProposalId,
    string IntentDigest,
    bool AlreadyApplied,
    string BaselineNote,
    string ResultNote,
    IReadOnlyList<ExpectedEffect> ActualEffects,
    string EffectDigest,
    AppliedLogEntry AppliedLogEntry);
