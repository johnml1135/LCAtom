using SIL.Motif.Host.Corpus;

namespace SIL.Motif.Host.Parser;

/// <summary>
/// A coverage figure, computed from one <see cref="BatchAnalysis"/> over one <see cref="CorpusDescriptor"/>,
/// carrying everything <c>docs/adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md</c> §4 requires a
/// coverage number to cite. There is deliberately no path in this codebase that produces a bare coverage
/// percentage — that omission is the defect this type exists to make impossible rather than merely
/// discouraged.
/// </summary>
/// <param name="CorpusId">Which corpus this was measured over. See <see cref="CorpusDescriptor.CorpusId"/>.</param>
/// <param name="CorpusSha256">
/// The corpus's content hash at measurement time. A figure whose corpus no longer hashes to this value
/// describes a corpus that has since changed and must not be trusted as still describing it.
/// </param>
/// <param name="GrammarSourceSha256">
/// The grammar's identity. Always taken from an <see cref="AssessReport"/>'s own field — this type never
/// hashes a grammar itself, because <see cref="AssessReport.GrammarSourceSha256"/> already carries the
/// parser's own hash of exactly what it read (<see cref="GrammarCoverageFigure"/>'s remarks on <c>Compute</c>).
/// </param>
/// <param name="Engine">Which of PanGloss's two Motif-usable modes produced this — see <see cref="ParserEngine"/>.</param>
/// <param name="PerWordTimeoutMs">
/// The per-word cap the run enforced. <c>null</c> means the run had no cap, which is only trustworthy as
/// "not a lower bound" when <see cref="TimedOutCount"/> is also zero — a run can only time out words if it
/// was capped, so the two fields are consistent by construction, never by convention.
/// </param>
/// <param name="TimedOutCount">
/// How many words hit <see cref="PerWordTimeoutMs"/>. Non-zero forces <see cref="IsLowerBound"/>, per
/// <c>docs/issues.md</c> <c>D9</c>: a timeout is a fact about the machine and the cap, never counted as an
/// analysis failure.
/// </param>
/// <param name="Analysed">Words the parser produced at least one analysis for — the numerator.</param>
/// <param name="Adjudicated">
/// The denominator: analysed plus no-analysis, exactly <see cref="BatchAnalysis.Adjudicated"/>. Timed-out
/// and skipped words are excluded because they carry no verdict either way, so including them would let a
/// figure move for reasons that have nothing to do with the grammar.
/// </param>
public sealed record GrammarCoverageFigure(
    string CorpusId,
    string CorpusSha256,
    string GrammarSourceSha256,
    ParserEngine Engine,
    int? PerWordTimeoutMs,
    int TimedOutCount,
    int Analysed,
    int Adjudicated)
{
    /// <summary>
    /// The coverage fraction — <see cref="Analysed"/> divided by <see cref="Adjudicated"/>, or <c>null</c>
    /// when nothing was adjudicated. Zero adjudicated words is not "0% coverage": every word either timed
    /// out or was skipped, so nothing was ever judged against the grammar, and reporting either 0% or 100%
    /// would assert a verdict this run never reached.
    /// </summary>
    public double? Fraction => Adjudicated == 0 ? null : (double)Analysed / Adjudicated;

    /// <summary>
    /// <c>true</c> when this figure is a lower bound and must present itself as one rather than as a
    /// measurement (<c>docs/issues.md</c> <c>D9</c>). Computed from <see cref="TimedOutCount"/> rather than
    /// trusted from a caller, for the same reason <see cref="BatchAnalysis.IsLowerBound"/> is a property and
    /// not a constructor parameter: it must be impossible for a figure with timeouts to forget to say so.
    /// </summary>
    public bool IsLowerBound => TimedOutCount > 0;

    /// <summary>
    /// Computes a coverage figure from one parser run and the corpus it was declared to run over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this takes a <see cref="BatchAnalysis"/> and a grammar hash separately, rather than one
    /// object.</b> <see cref="PanGlossParser.AnalyseBatch"/> is the call that carries a per-word timeout and
    /// therefore the only one that can produce <see cref="WordOutcome.TimedOut"/> rows — the raw material a
    /// lower-bound figure needs. <see cref="PanGlossParser.Assess"/> is the call that resolves identities to
    /// FieldWorks GUIDs and, as a side effect, carries <see cref="AssessReport.GrammarSourceSha256"/>. A
    /// coverage figure needs facts that only exist on two different reports of the same grammar; this method
    /// does not run either call itself; it only assembles what a caller already obtained from both.
    /// </para>
    /// <para>
    /// <b>Refuses rather than silently mismeasuring</b> when <paramref name="analysis"/> did not in fact run
    /// over <paramref name="corpus"/>'s word set — a coverage figure's provenance is only honest if it
    /// really describes the words it cites, and a caller passing the wrong corpus for a batch is a bug to
    /// surface immediately, not a mismatch to paper over with whichever count happens to be smaller.
    /// </para>
    /// </remarks>
    public static GrammarCoverageFigure Compute(BatchAnalysis analysis, CorpusDescriptor corpus, string grammarSourceSha256)
    {
        if (analysis is null) throw new ArgumentNullException(nameof(analysis));
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (string.IsNullOrWhiteSpace(grammarSourceSha256))
            throw new ArgumentException("Required.", nameof(grammarSourceSha256));

        var analysedWords = new HashSet<string>(analysis.Words.Select(w => w.Word), StringComparer.Ordinal);
        var corpusWords = new HashSet<string>(corpus.Words, StringComparer.Ordinal);
        if (!analysedWords.SetEquals(corpusWords))
            throw new ArgumentException(
                $"The batch analysed {analysedWords.Count} distinct word(s) but corpus '{corpus.CorpusId}' " +
                $"describes {corpusWords.Count}. A coverage figure must be measured over exactly the corpus " +
                "it cites; passing a mismatched pair would make the provenance this type exists to carry " +
                "false.",
                nameof(analysis));

        return new GrammarCoverageFigure(
            CorpusId: corpus.CorpusId,
            CorpusSha256: corpus.Sha256,
            GrammarSourceSha256: grammarSourceSha256,
            Engine: analysis.Engine,
            PerWordTimeoutMs: analysis.PerWordTimeoutMs,
            TimedOutCount: analysis.TimedOut,
            Analysed: analysis.Analysed,
            Adjudicated: analysis.Adjudicated);
    }

    /// <summary>
    /// Convenience overload taking the whole <see cref="AssessReport"/> the grammar hash came free with,
    /// rather than making every caller spell out <c>report.GrammarSourceSha256</c>.
    /// </summary>
    public static GrammarCoverageFigure Compute(BatchAnalysis analysis, CorpusDescriptor corpus, AssessReport grammarSource)
    {
        if (grammarSource is null) throw new ArgumentNullException(nameof(grammarSource));
        return Compute(analysis, corpus, grammarSource.GrammarSourceSha256);
    }
}
