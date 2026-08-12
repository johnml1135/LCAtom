using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.Parser;

namespace SIL.Motif.Host.Analysis;

/// <summary>
/// One parser run, paired with the word set it was declared to run over — the two facts
/// <see cref="AnalysisAssessmentProvenance"/> needs and neither carries alone.
/// </summary>
/// <remarks>
/// Mirrors <see cref="GrammarCoverageFigure.Compute(BatchAnalysis, CorpusDescriptor, string)"/>'s same
/// two-input shape, for the same reason: a coverage-shaped fact needs the grammar's identity
/// (<see cref="AssessReport.GrammarSourceSha256"/>, which the parser hands back for free) and the word
/// set's identity (<see cref="CorpusDescriptor.Sha256"/>) together, and this is where a caller who already
/// ran <c>PanGlossParser.Assess</c> hands both over. <b>Producing this pair is exactly the slow,
/// explicitly-invoked step ADR 0038 decision 5 keeps out of the read path</b> — nothing in this namespace
/// runs the parser; a <see cref="StoredAssessment"/> only ever arrives already made.
/// </remarks>
public sealed record StoredAssessment(AssessReport Report, CorpusDescriptor Corpus);
