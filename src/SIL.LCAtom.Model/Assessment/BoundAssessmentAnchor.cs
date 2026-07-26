namespace SIL.LCAtom.Model.Assessment;

/// <summary>
/// Binds a subsequent Apply to the specific evaluated baseline a prior <see cref="Assessment"/> read.
/// See docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md, decision 3 ("Apply is bound to a
/// prior Assessment"): a bare apply with no bound Assessment is a hard error, and a moved footprint
/// (this anchor's <see cref="FootprintDigest"/> no longer matching the live project) stops apply with
/// a drift diagnostic rather than proceeding.
/// </summary>
/// <param name="FootprintDigest">
/// A digest of exactly the pre-mutation state of every target this Change Set's operations touch —
/// the "before" half of <see cref="Effects.ExpectedEffect"/>, not the full expected effect set. Apply
/// recomputes this immediately before mutating (a pure read, legal at any transaction state per
/// docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 1) and hard-stops on a mismatch:
/// the live project moved since this Assessment was recorded.
/// </param>
/// <param name="EffectDigest">
/// The Assessment's own <see cref="Assessment.EffectDigest"/>, carried here too so the anchor is
/// self-describing about which expected-effect computation it binds to.
/// </param>
/// <param name="RunnerVersion">The <c>SIL.LCAtom.Runner</c> assembly version that computed this Assessment.</param>
/// <param name="LibLcmVersion">The <c>SIL.LCModel</c> assembly version the Assessment ran against.</param>
/// <param name="ProjectionVersion">
/// The Canonical Semantic Snapshot / expected-effect projection shape version (see
/// <see cref="Snapshot.SnapshotFields.ProjectionVersion"/>) — bumped only if that shape ever changes
/// in a way that could alter digests for unchanged content.
/// </param>
/// <param name="AssessedAtUtc">
/// UTC ISO 8601 basic timestamp (matching <c>AppliedLog.AppliedLogFormat.FormatTimestamp</c>'s
/// convention) of when this Assessment ran.
/// </param>
public sealed record BoundAssessmentAnchor(
    string FootprintDigest,
    string EffectDigest,
    string RunnerVersion,
    string LibLcmVersion,
    string ProjectionVersion,
    string AssessedAtUtc);
