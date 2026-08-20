namespace SIL.Motif.Host.Analysis;

/// <summary>
/// A navigable occurrence of an approved analysis. The Segment GUID identifies the durable container;
/// the analysis index identifies the word inside its current sequence and is never portable identity.
/// </summary>
public sealed record AnalysisOccurrenceLink(string SegmentGuid, int AnalysisIndex);

/// <summary>
/// One human-approved analysis on a word form — one member of the <i>set</i> ADR 0038 decision 2 calls
/// "the durable test". A word form with several genuinely ambiguous readings carries several of these;
/// nothing here collapses them.
/// </summary>
/// <param name="ContentDigest">
/// <see cref="AnalysisContent.ComputeDigest"/> over this analysis's morph bundles — the identity used for
/// set membership and for comparing two <see cref="WordFormAnalysisAggregate"/> instances of the same
/// word form across time (see <see cref="AnalysisAggregateDiff"/>). Never the <c>WfiAnalysis</c> GUID.
/// </param>
/// <param name="MorphBreakdown">
/// A human-readable rendering of the morph bundles — surface form and MSA abbreviation per morph, joined
/// in order — for a person or CLI to print. Carries no identity of its own; two analyses can render
/// identically and still be distinct content, or vice versa if display truncates something the digest
/// did not (this has not been observed, but the digest is the ground truth either way).
/// </param>
/// <param name="Occurrences">
/// Read-only navigation coordinates for each occurrence. They describe current project state and never
/// become canonical Proposal targets or comparison-footprint identity.
/// </param>
public sealed record ApprovedAnalysis(
    string ContentDigest,
    string MorphBreakdown,
    IReadOnlyList<AnalysisOccurrenceLink> Occurrences)
{
    /// <summary>How many positions in the project's texts reference this analysis.</summary>
    public int OccurrenceCount => Occurrences.Count;
}
