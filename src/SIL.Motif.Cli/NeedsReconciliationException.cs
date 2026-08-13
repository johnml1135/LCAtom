using System;

namespace SIL.Motif.Cli;

/// <summary>
/// Names which durability step failed after a Proposal's mutation was already committed to a live
/// cache, so a caller can tell "this needs inspection" from "this needs a different inspection".
/// </summary>
public enum ReconciliationBoundary
{
    /// <summary>
    /// The unit of work committed, but persisting it to the project file did not confirm completion
    /// (see <see cref="SIL.Motif.Host.LcmUtils.FwDataProjectLoader.Save"/>'s remarks on its background
    /// commit thread) -- the file's on-disk state is not guaranteed intact.
    /// </summary>
    Save,

    /// <summary>
    /// The project was applied and saved, but recording that fact in Motif's own proposal store
    /// failed -- the project and the store now disagree about whether this Proposal is applied.
    /// </summary>
    ReceiptRecording,
}

/// <summary>
/// Thrown when an apply's mutation is already durable, or may be, but the step that would confirm or
/// record that fact failed -- so the outcome cannot be resolved to a clean success or a clean
/// rollback. This is distinct from every other failure <c>ProposalApplier.Apply</c> itself can throw:
/// those roll back the unit of work and leave nothing durable, so discarding the cache and starting
/// over is safe. A failure at <see cref="Boundary"/> must not be retried blindly, because this
/// exception alone does not say which side of that boundary the true state landed on -- a human (or a
/// caller with an independent way to check) must inspect the project before doing anything else with
/// it.
/// </summary>
public sealed class NeedsReconciliationException : Exception
{
    public ReconciliationBoundary Boundary { get; }

    public NeedsReconciliationException(ReconciliationBoundary boundary, string message, Exception innerException)
        : base(message, innerException)
    {
        Boundary = boundary;
    }
}
