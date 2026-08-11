namespace SIL.Motif.Host.Analysis;

/// <summary>
/// One analysis the parser produced for a word form, as recorded on a <see cref="StoredAssessment"/> —
/// present only when an Assessment is on record and covered this word (see
/// <see cref="WordFormAnalysisAggregate.AutomaticAnalyses"/> for what a <c>null</c> collection there means
/// versus an empty one).
/// </summary>
/// <param name="ContentDigest">
/// The parser's own identity digest for this analysis (<see cref="Parser.ParsedAnalysis.IdentityDigest"/>),
/// carried through unchanged. This is <b>not</b> computed the same way as
/// <see cref="ApprovedAnalysis.ContentDigest"/> — PanGloss's identity is coarser than FieldWorks': its
/// analysis identity keys each morpheme on the chosen morphosyntactic analysis (MSA) GUID alone, with no
/// field for the allomorph chosen or for per-morpheme sense — so the two digest spaces are never comparable
/// byte-for-byte as a match/mismatch verdict. This
/// field exists to distinguish and de-duplicate automatic analyses among themselves, not to be diffed
/// against the manual side. Reporting them side by side, without inferring agreement or disagreement from
/// whether the strings happen to match, is ADR 0038 decision 6's "Motif does not attribute cause" applied
/// to identity as well as to cause.
/// </param>
/// <param name="MorphBreakdown">
/// A human-readable rendering, resolved from the parser's morpheme GUIDs back through the live project —
/// the same presentational role as <see cref="ApprovedAnalysis.MorphBreakdown"/>, built from a different
/// and coarser source.
/// </param>
public sealed record AutomaticAnalysis(string ContentDigest, string MorphBreakdown);
