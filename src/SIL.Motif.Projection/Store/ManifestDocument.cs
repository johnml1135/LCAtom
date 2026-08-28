using SIL.Motif.Model.DryRun;

namespace SIL.Motif.Projection.Store;

/// <summary>
/// The review-state rendering shape for one Proposal: the mutable review state for a committed
/// Proposal, and a movable pointer at whichever <c>ProposalRevisions</c> row is current for this id —
/// exactly a git ref pointing at a commit hash. A revision is content-addressed and write-once, never
/// revisited once written; this document is built from the one mutable <c>Proposals</c> row, keyed by
/// the frozen <see cref="ProposalId"/> rather than by digest, so <c>finalize</c> (including an amend)
/// writes a new revision and moves this pointer rather than rewriting the revision.
/// </summary>
/// <remarks>
/// Lives here rather than beside <c>SIL.Motif.Worker.Store.ProposalRepository</c> so a projection
/// (List, Show, the anchor a DryRun binds, the status an Apply writes) can be built from it without
/// the projection layer depending on the Worker project — the dependency runs the other way, from
/// Worker to here, so a future Avalonia consumer can read the same rendering shape without pulling in
/// the Worker.
/// </remarks>
public sealed class ManifestDocument
{
    public string ProposalId { get; set; } = "";

    /// <summary>
    /// <c>proposed</c> (written by <c>finalize</c>, including an amend via <c>reopen</c> +
    /// re-<c>finalize</c> — any content change invalidates a prior approval, since approval is
    /// effect-digest-scoped) or <c>applied</c> (written by <c>apply</c>).
    /// </summary>
    public string Status { get; set; } = ManifestStatus.Proposed;

    public string? Label { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// The full <c>sha256:</c>-prefixed intent digest this manifest currently points at — a movable
    /// pointer, exactly like a git ref moving to point at a new commit. <c>finalize</c> sets this to
    /// the newly committed object's digest on both a first commit and an amend; the id
    /// (<see cref="ProposalId"/>) never changes, only this pointer does. <c>null</c> for a Draft: it
    /// has no committed revision yet, so there is nothing for this pointer to name (ADR 0041 decision 3).
    /// </summary>
    public string? CurrentIntentDigest { get; set; }

    /// <summary>
    /// The <see cref="BoundDryRunAnchor"/> recorded by the most recent <c>dry-run</c> against
    /// <see cref="CurrentIntentDigest"/>'s content (ADR 0004 decision 3). <c>null</c> when no dry run
    /// has been computed yet, or after an amend invalidates the prior one (it was bound to the
    /// previous content's footprint/effect digest). <c>apply</c> requires this to be present and not
    /// stale — a bare apply with no bound DryRun is a hard error.
    /// </summary>
    public BoundDryRunAnchor? Anchor { get; set; }

    /// <summary>
    /// The most recent human or AI verdict on <see cref="CurrentIntentDigest"/> (ADR 0031 decision 7):
    /// present only while <see cref="Status"/> is <see cref="ManifestStatus.Approved"/> or
    /// <see cref="ManifestStatus.Rejected"/>. <c>null</c> otherwise, including after an amend — a
    /// Decision is bound to the content it was recorded against, so any content change invalidates it
    /// the same way it invalidates <see cref="Anchor"/>.
    /// </summary>
    public Decision? Decision { get; set; }

    /// <summary>
    /// The <see cref="ProposalId"/> of the Proposal that superseded this one, set only when
    /// <see cref="Status"/> is <see cref="ManifestStatus.Superseded"/>.
    /// </summary>
    public string? SupersededBy { get; set; }
}

/// <summary>
/// A recorded verdict on a Proposal's exact content, per ADR 0031 decision 7 — the record must always
/// show whether an AI or a human made the call, never leave it to be inferred.
/// </summary>
public sealed class Decision
{
    public string Outcome { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string? Comment { get; set; }

    /// <summary>The <c>CurrentIntentDigest</c> this Decision was recorded against; an amend moves the pointer and drops the Decision.</summary>
    public string BoundIntentDigest { get; set; } = "";
    public string TimestampUtc { get; set; } = "";
}

public static class DecisionOutcome
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public static class DecisionActorType
{
    public const string Human = "human";
    public const string Ai = "ai";
}

/// <summary>
/// The six statuses ADR 0031 decision 3 names. Dependency ("requires another Proposal") is structure,
/// not a status, and is never represented here.
/// </summary>
public static class ManifestStatus
{
    public const string Proposed = "proposed";
    public const string Deferred = "deferred";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Applied = "applied";
    public const string Superseded = "superseded";
}
