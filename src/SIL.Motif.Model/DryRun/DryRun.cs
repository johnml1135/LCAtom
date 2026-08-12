using System.Collections.Generic;
using SIL.Motif.Model.Effects;

namespace SIL.Motif.Model.DryRun;

/// <summary>
/// The result of dry-running a Proposal against a live baseline, without mutating it. A minimal
/// slice of the full Assessment the Change Set contract describes — DryRun is this codebase's name
/// for that step — Stage C populates exactly these four fields; later stages add
/// resolved-target/storage mappings, warnings/conflicts, drift, and version-matrix fields to this
/// same record.
/// </summary>
/// <param name="IntentDigest">
/// The Proposal's intent digest (see <see cref="SIL.Motif.Contract.Canonicalization.IntentDigest"/>),
/// included so an DryRun is self-describing about which content it was computed for.
/// </param>
/// <param name="BaselineNote">
/// A human-readable description of the footprint-scoped baseline the effects were read back
/// against. Stage C does not compute a whole-project baseline digest: whole-project snapshot work
/// (onboarding, two-way diff, a first baseline digest) is inherently expensive and is budgeted
/// separately from the interactive per-footprint pre-flight path this dryRunner takes (ADR 0006
/// decision 2).
/// </param>
/// <param name="ExpectedEffects">
/// The full effect set: a delta of the Canonical Semantic Snapshot, scoped to the change and read
/// back from LibLCM by comparing a before/after footprint snapshot — never a replay of the
/// operations' intended writes, so it surfaces engine-computed consequences (ownership cascade,
/// inbound reference cleanup) the operations do not name.
/// </param>
/// <param name="EffectDigest">
/// <see cref="ExpectedEffectSetDigest.Compute"/> over <see cref="ExpectedEffects"/>.
/// </param>
/// <param name="Anchor">
/// The <see cref="BoundDryRunAnchor"/> this DryRun produced, binding a subsequent Apply to
/// this exact evaluated baseline (ADR 0004 decision 3). Persisted by the CLI's <c>dry-run</c>
/// command and required by <c>apply</c>.
/// </param>
public sealed record DryRun(
    string IntentDigest,
    string BaselineNote,
    IReadOnlyList<ExpectedEffect> ExpectedEffects,
    string EffectDigest,
    BoundDryRunAnchor Anchor);
