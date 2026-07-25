using System.Collections.Generic;
using SIL.LCAtom.Model.Effects;

namespace SIL.LCAtom.Model.Assessment;

/// <summary>
/// The result of assessing a Change Set against a live baseline, without mutating it. A minimal
/// slice of the full Assessment described in docs/change-set-contract.md, "Assessment" — Stage C
/// populates exactly these four fields; later stages add resolved-target/storage mappings,
/// warnings/conflicts, drift, and version-matrix fields to this same record.
/// </summary>
/// <param name="IntentDigest">
/// The Change Set's intent digest (see <see cref="SIL.LCAtom.Contract.Canonicalization.IntentDigest"/>),
/// included so an Assessment is self-describing about which content it was computed for.
/// </param>
/// <param name="BaselineNote">
/// A human-readable description of the footprint-scoped baseline the effects were read back
/// against (Stage C does not compute a whole-project baseline digest — see
/// docs/adr/0006-engine-reality-apply-readback-preflight.md on why that is a separate, more
/// expensive operation from the interactive per-footprint path this assessor takes).
/// </param>
/// <param name="ExpectedEffects">
/// The full effect set: a delta of the Canonical Semantic Snapshot, scoped to the change and read
/// back from LibLCM. See docs/change-set-contract.md, "Expected effects".
/// </param>
/// <param name="EffectDigest">
/// <see cref="ExpectedEffectSetDigest.Compute"/> over <see cref="ExpectedEffects"/>.
/// </param>
public sealed record Assessment(
    string IntentDigest,
    string BaselineNote,
    IReadOnlyList<ExpectedEffect> ExpectedEffects,
    string EffectDigest);
