using SIL.Motif.Model.DryRun;

namespace SIL.Motif.Cli.Store;

/// <summary>
/// The on-disk shape of <c>manifests/&lt;proposalId&gt;.json</c>: the mutable review state for a
/// committed Proposal, and a movable pointer at whichever <c>objects/&lt;intentDigest&gt;.json</c>
/// is current for this id — exactly a git ref pointing at a commit hash. The object itself is
/// content-addressed and write-once, never revisited once written; this manifest is the one mutable
/// file in the store, keyed by the frozen <see cref="ProposalId"/> rather than by digest, so
/// <c>finalize</c> (including an amend) writes a new object and moves this manifest's pointer rather
/// than renaming or rewriting the object.
/// </summary>
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
    /// (<see cref="ProposalId"/>) never changes, only this pointer does.
    /// </summary>
    public string CurrentIntentDigest { get; set; } = "";

    /// <summary>
    /// The <see cref="BoundDryRunAnchor"/> recorded by the most recent <c>dry-run</c> against
    /// <see cref="CurrentIntentDigest"/>'s content (ADR 0004 decision 3). <c>null</c> when no dry run
    /// has been computed yet, or after an amend invalidates the prior one (it was bound to the
    /// previous content's footprint/effect digest). <c>apply</c> requires this to be present and not
    /// stale — a bare apply with no bound DryRun is a hard error.
    /// </summary>
    public BoundDryRunAnchor? Anchor { get; set; }
}

public static class ManifestStatus
{
    public const string Proposed = "proposed";
    public const string Applied = "applied";
}
