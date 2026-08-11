namespace SIL.Motif.Host.Analysis;

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
/// <param name="OccurrenceCount">
/// How many places in the project's own texts reference this analysis via <c>Segment.AnalysesRS</c> —
/// <c>IWfiAnalysis.OccurrencesInTexts</c>'s count. Reported because it was asked for, not because it
/// changes what "approved" means: ADR 0038's own framing draws a hard line between the type-level test
/// (this analysis, approved or not) and occurrence assignment (which sentence used it), which is
/// disambiguation and stays out of scope here. This count answers "how many occurrences", never "which
/// ones".
/// </param>
public sealed record ApprovedAnalysis(string ContentDigest, string MorphBreakdown, int OccurrenceCount);
