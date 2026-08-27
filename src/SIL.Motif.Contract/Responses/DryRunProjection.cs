using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>
/// The <c>dry-run</c> report: the effects a Proposal would have, read back from a live baseline
/// without mutating it.
/// </summary>
public sealed record DryRunProjection(
    string ProposalId,
    string IntentDigest,
    string BaselineNote,
    IReadOnlyList<EffectView> Effects,
    string EffectDigest,
    string FootprintDigest);
