namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Turns a <see cref="StoredCorpus"/>'s Documents into the <see cref="CorpusDescriptor"/> that
/// <see cref="Parser.GrammarCoverageFigure.Compute"/> consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The asymmetry this preserves.</b> A <see cref="CorpusDocument"/> keeps its text in order, with
/// repetition; a <see cref="CorpusDescriptor"/> is sorted and distinct. This type tokenises in order and
/// hands the result to <see cref="CorpusDescriptor.Create"/>, which is the only place deduplication and
/// sorting happen — never here, and never inside an <see cref="IWordTokeniser"/>.
/// </para>
/// <para>
/// <b>Provenance flows through deliberately.</b> <see cref="CorpusDescriptor.Create"/> is called with
/// <paramref name="corpus"/>'s own <see cref="CorpusProvenance"/>, so <see cref="CorpusDescriptor.SupportsAccuracyClaims"/>
/// answers correctly on the far side of the bridge: a corpus nobody has attested clean and in scope must
/// still be unable to support an accuracy figure once it has been tokenised. Building the bridge must not be
/// a back door to that refusal.
/// </para>
/// </remarks>
public static class CorpusTokenisation
{
    /// <summary>
    /// Tokenises <paramref name="corpus"/>'s Documents — all of them, or the subset named by
    /// <paramref name="documentIds"/> — in the order they were added to the corpus, concatenates the word
    /// forms, and folds them into a <see cref="CorpusDescriptor"/> carrying <paramref name="corpus"/>'s
    /// provenance.
    /// </summary>
    /// <param name="corpus">The corpus to tokenise.</param>
    /// <param name="tokeniser">
    /// The tokeniser to run. Its <see cref="IWordTokeniser.Name"/> and <see cref="IWordTokeniser.Version"/>
    /// must match what <paramref name="corpus"/> declared at ingestion — see remarks.
    /// </param>
    /// <param name="documentIds">
    /// When supplied, only these Documents are tokenised, and the resulting
    /// <see cref="CorpusDescriptor.CorpusId"/> says so rather than reading as if it covered the whole corpus
    /// — the same discipline <see cref="LcmWordformCorpus.Extract"/>'s <c>corpusId</c> parameter documents
    /// for a capped sample. Omit, or pass <c>null</c>, to tokenise every Document.
    /// </param>
    /// <remarks>
    /// <b>The declared tokenisation is binding.</b> Two corpora tokenised differently are not comparable even
    /// when the source text is identical (ADR 0036 decision 4; the corpus ingestion contract), so this
    /// throws rather than running a tokeniser that disagrees with
    /// what <paramref name="corpus"/>'s <see cref="CorpusProvenance.Tokenisation"/> already declares, or
    /// silently re-stamping the corpus with whatever happened to run (pinned by
    /// `DeclaredVsSuppliedTokeniserMismatchThrows_NamingBothTheDeclaredAndSuppliedValues`). If the corpus
    /// genuinely needs to change which tokeniser it was measured with, that is a decision for whoever owns
    /// the corpus to make explicitly, not a side effect of calling this method.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="corpus"/> or <paramref name="tokeniser"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="tokeniser"/>'s name or version does not match what <paramref name="corpus"/> declared.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="documentIds"/> names a document the corpus does not have.</exception>
    public static CorpusDescriptor ToDescriptor(
        StoredCorpus corpus,
        IWordTokeniser tokeniser,
        IReadOnlyCollection<string>? documentIds = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(tokeniser);

        DemandDeclaredTokeniserMatches(corpus, tokeniser);

        var selected = SelectDocuments(corpus, documentIds);
        var label = Label(corpus.CorpusId, corpus.Documents.Count, selected);
        var words = selected.SelectMany(document => tokeniser.Tokenise(document.Text));

        return CorpusDescriptor.Create(label, words, corpus.Provenance);
    }

    private static void DemandDeclaredTokeniserMatches(StoredCorpus corpus, IWordTokeniser tokeniser)
    {
        var declared = corpus.Provenance.Tokenisation;
        if (string.Equals(declared.Method, tokeniser.Name, StringComparison.Ordinal) &&
            string.Equals(declared.Version, tokeniser.Version, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Corpus '{corpus.CorpusId}' declares tokenisation '{declared.Method}' version '{declared.Version}', " +
            $"but the supplied tokeniser is '{tokeniser.Name}' version '{tokeniser.Version}'. Two corpora " +
            "tokenised differently are not comparable even when the source text is identical, so this refuses " +
            "to run rather than silently re-stamp the corpus's declared tokenisation. Use the declared " +
            "tokeniser, or change the corpus's provenance deliberately if the tokeniser genuinely changed.");
    }

    private static IReadOnlyList<CorpusDocument> SelectDocuments(
        StoredCorpus corpus, IReadOnlyCollection<string>? documentIds)
    {
        if (documentIds is null) return corpus.Documents;

        // Zero words would produce a valid-looking descriptor and a figure that is 0% of nothing; refuse instead.
        if (documentIds.Count == 0)
        {
            throw new ArgumentException(
                $"No documents selected from corpus '{corpus.CorpusId}'. An empty selection would produce a " +
                "descriptor with no words, and a figure over no words is not a figure. Pass null to tokenise " +
                "every document.",
                nameof(documentIds));
        }

        var wanted = new HashSet<string>(documentIds, StringComparer.Ordinal);
        // Filtered from the corpus's own order, not documentIds' order, so a subset concatenates like the whole.
        var selected = corpus.Documents.Where(d => wanted.Contains(d.DocumentId)).ToList();

        var found = selected.Select(d => d.DocumentId).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(id => !found.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Corpus '{corpus.CorpusId}' has no document(s) named: {string.Join(", ", missing)}.",
                nameof(documentIds));
        }

        return selected;
    }

    /// <summary>Names a partial selection as partial, matching <see cref="LcmWordformCorpus.Extract"/>.</summary>
    private static string Label(string corpusId, int totalDocumentCount, IReadOnlyList<CorpusDocument> selected)
    {
        if (selected.Count == totalDocumentCount) return corpusId;

        var ids = string.Join(", ", selected.Select(d => d.DocumentId));
        return $"{corpusId} ({selected.Count} of {totalDocumentCount} document(s): {ids})";
    }
}
