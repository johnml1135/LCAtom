using System.Collections.Generic;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Model.Effects;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// What every operation kind's handler must supply: resolve, snapshot, lower, and re-snapshot one
/// operation (<see cref="ApplyAndCaptureEffect"/>), and read the same target's CURRENT footprint with
/// no mutation at all (<see cref="ReadCurrentFootprint"/>). Mirrors the two static methods
/// <c>SetGlossOperationHandler</c> exposed — see that type's remarks, now preserved on
/// this interface instead of duplicated ad hoc per kind.
/// </summary>
/// <remarks>
/// A real dispatch table, not a single hardcoded case, because
/// <see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/>, <see cref="SIL.Motif.Runner.Apply.ProposalApplier"/>,
/// and <see cref="SIL.Motif.Runner.Apply.FootprintProbe"/> each dispatch across ten fields (twenty
/// kinds), not one. Each generated kind file registers
/// one instance per verb with <see cref="OperationHandlerRegistry"/>; the three call sites above look
/// the handler up by <see cref="OperationEnvelope.Kind"/> instead of switching on it.
/// </remarks>
public interface IOperationHandler
{
    /// <summary>
    /// Resolves the operation's target, snapshots it, applies the lowering inside the caller's
    /// already-open unit of work, re-snapshots, and returns the one <see cref="ExpectedEffect"/> this
    /// caused. Must not open or close any unit of work itself (docs/adr/0006, decision 5).
    /// </summary>
    /// <param name="touchedTargets">Appended with the resolved target's <see cref="CanonicalId"/>,
    /// so callers can report which objects a Proposal touched.</param>
    ExpectedEffect ApplyAndCaptureEffect(
        LcmCache cache, OperationEnvelope operation, List<CanonicalId> touchedTargets);

    /// <summary>
    /// Reads the target's CURRENT, pre-mutation footprint with no mutation and no unit of work at
    /// all — legal at any transaction state (docs/adr/0006, decision 1). Returns an
    /// <see cref="ExpectedEffect"/> whose <c>Before</c> and <c>After</c> are the same value; only
    /// <c>Before</c> participates in <see cref="FootprintDigest"/>.
    /// </summary>
    ExpectedEffect ReadCurrentFootprint(LcmCache cache, OperationEnvelope operation);
}
