using SIL.LCModel;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// Exports a candidate <see cref="LcmCache"/> to a plain directory that a separate PanGloss process can
/// read without holding the cache, a scratch, or a Baseline open.
/// </summary>
/// <remarks>
/// This is the seam between "a Proposal has been applied in memory" and "PanGloss has bytes to read":
/// export happens once, while the candidate is still open, and the Assessment process that follows never
/// needs the <see cref="LcmCache"/> again (see <see cref="PanGlossAssessmentProcess"/>).
/// </remarks>
public interface IPanGlossCandidateExporter
{
    /// <summary>
    /// Saves <paramref name="candidate"/> and copies its backing project into
    /// <paramref name="emptyDestination"/>.
    /// </summary>
    /// <param name="candidate">
    /// The mutated, unsaved candidate cache — the project state after a Proposal was applied. Its own
    /// backing project file is saved as part of this call.
    /// </param>
    /// <param name="emptyDestination">
    /// A directory that must already exist and contain nothing. Refused otherwise, so a stale or
    /// half-written destination is never silently reused.
    /// </param>
    Task ExportAsync(LcmCache candidate, string emptyDestination, CancellationToken cancellationToken);
}
