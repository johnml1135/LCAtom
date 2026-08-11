namespace SIL.Motif.Model.DryRun;

/// <summary>
/// Binds a subsequent Apply to the specific evaluated baseline a prior <see cref="DryRun"/> read.
/// Apply is bound to a prior DryRun (ADR 0004 decision 3): a bare apply with no bound DryRun is a
/// hard error, and a moved footprint (this anchor's <see cref="FootprintDigest"/> no longer matching
/// the live project) stops apply with a drift diagnostic rather than proceeding.
/// </summary>
/// <param name="FootprintDigest">
/// A digest of exactly the pre-mutation state of every target this Proposal's operations touch —
/// the "before" half of <see cref="Effects.ExpectedEffect"/>, not the full expected effect set. Apply
/// recomputes this immediately before mutating (a pure read, legal at any transaction state — ADR
/// 0006 decision 1) and hard-stops on a mismatch: the live project moved since this DryRun was
/// recorded.
/// </param>
/// <param name="EffectDigest">
/// The DryRun's own <see cref="DryRun.EffectDigest"/>, carried here too so the anchor is
/// self-describing about which expected-effect computation it binds to.
/// </param>
/// <param name="RunnerVersion">The <c>SIL.Motif.Runner</c> assembly version that computed this DryRun.</param>
/// <param name="LibLcmVersion">The <c>SIL.LCModel</c> assembly version the DryRun ran against.</param>
/// <param name="ProjectionVersion">
/// The Canonical Semantic Snapshot / expected-effect projection shape version (see
/// <see cref="Snapshot.SnapshotFields.ProjectionVersion"/>) — bumped only if that shape ever changes
/// in a way that could alter digests for unchanged content.
/// </param>
/// <param name="DryRunAtUtc">
/// UTC ISO 8601 basic timestamp (matching <c>AppliedLog.AppliedLogFormat.FormatTimestamp</c>'s
/// convention) of when this DryRun ran.
/// </param>
public sealed record BoundDryRunAnchor(
    string FootprintDigest,
    string EffectDigest,
    string RunnerVersion,
    string LibLcmVersion,
    string ProjectionVersion,
    string DryRunAtUtc);
