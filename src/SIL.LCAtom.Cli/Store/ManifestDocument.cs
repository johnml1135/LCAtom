using SIL.LCAtom.Model.Assessment;

namespace SIL.LCAtom.Cli.Store;

/// <summary>
/// The on-disk shape of <c>manifests/&lt;changeSetId&gt;.json</c>: the mutable review state for a
/// committed Change Set, and a movable pointer at whichever <c>objects/&lt;intentDigest&gt;.json</c>
/// is current for this id — exactly a git ref pointing at a commit hash. See
/// docs/stage2-change-management.md, S1/S4.
/// </summary>
public sealed class ManifestDocument
{
    public string ChangeSetId { get; set; } = "";

    /// <summary>
    /// <c>proposed</c> (written by <c>finalize</c>, including an amend via <c>reopen</c> +
    /// re-<c>finalize</c> — any content change invalidates a prior approval, since approval is
    /// effect-digest-scoped) or <c>applied</c> (written by <c>apply</c>).
    /// </summary>
    public string Status { get; set; } = ManifestStatus.Proposed;

    public string? Label { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// The full <c>sha256:</c>-prefixed intent digest this manifest currently points at — the
    /// movable "ref" (docs/stage2-change-management.md, S1). <c>finalize</c> sets this to the newly
    /// committed object's digest on both a first commit and an amend; the id
    /// (<see cref="ChangeSetId"/>) never changes, only this pointer does.
    /// </summary>
    public string CurrentIntentDigest { get; set; } = "";

    /// <summary>
    /// The <see cref="BoundAssessmentAnchor"/> recorded by the most recent <c>assess</c> against
    /// <see cref="CurrentIntentDigest"/>'s content (docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
    /// decision 3). <c>null</c> when no assess has run yet, or after an amend invalidates the prior
    /// one (it was bound to the previous content's footprint/effect digest). <c>apply</c> requires
    /// this to be present and not stale — a bare apply with no bound Assessment is a hard error.
    /// </summary>
    public BoundAssessmentAnchor? Anchor { get; set; }
}

public static class ManifestStatus
{
    public const string Proposed = "proposed";
    public const string Applied = "applied";
}
