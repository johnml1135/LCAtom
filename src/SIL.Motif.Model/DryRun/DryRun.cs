using System.Collections.Generic;
using SIL.Motif.Model.Effects;

namespace SIL.Motif.Model.DryRun;

/// <summary>
/// The result of dry-running a Proposal against a live baseline, without mutating it. A minimal
/// slice of the full DryRun described in docs/change-set-contract.md, "DryRun" — Stage C
/// populates exactly these four fields; later stages add resolved-target/storage mappings,
/// warnings/conflicts, drift, and version-matrix fields to this same record.
/// </summary>
/// <param name="IntentDigest">
/// The Proposal's intent digest (see <see cref="SIL.Motif.Contract.Canonicalization.IntentDigest"/>),
/// included so an DryRun is self-describing about which content it was computed for.
/// </param>
/// <param name="BaselineNote">
/// A human-readable description of the footprint-scoped baseline the effects were read back
/// against (Stage C does not compute a whole-project baseline digest — see
/// docs/adr/0006-engine-reality-apply-readback-preflight.md on why that is a separate, more
/// expensive operation from the interactive per-footprint path this dryRunner takes).
/// </param>
/// <param name="ExpectedEffects">
/// The full effect set: a delta of the Canonical Semantic Snapshot, scoped to the change and read
/// back from LibLCM. See docs/change-set-contract.md, "Expected effects".
/// </param>
/// <param name="EffectDigest">
/// <see cref="ExpectedEffectSetDigest.Compute"/> over <see cref="ExpectedEffects"/>.
/// </param>
/// <param name="Anchor">
/// The <see cref="BoundDryRunAnchor"/> this DryRun produced, binding a subsequent Apply to
/// this exact evaluated baseline (docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md,
/// decision 3). Persisted by the CLI's <c>dry-run</c> command and required by <c>apply</c>.
/// </param>
public sealed record DryRun(
    string IntentDigest,
    string BaselineNote,
    IReadOnlyList<ExpectedEffect> ExpectedEffects,
    string EffectDigest,
    BoundDryRunAnchor Anchor);
