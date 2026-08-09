using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// The bridge from Documents — running text, in order, with repetition — to a CorpusDescriptor — sorted,
/// distinct word forms — that <c>docs/issues.md</c> <c>B26</c> names as the missing connection between
/// ingestion and measurement.
/// </summary>
/// <remarks>
/// The rules these tests defend, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item>The bridge must preserve the asymmetry: a Document's order and repetition survive tokenisation, and
/// only <see cref="CorpusDescriptor.Create"/> sorts and deduplicates. Doing either step twice, or in the
/// wrong place, destroys information the other corpus consumers (frequency ranking, n-gram sequence) need.</item>
/// <item>A form invented by splitting on word-internal punctuation reads as a grammar gap that is really a
/// tokenisation bug — the specific failure <c>docs/adr/0036</c> decision 4 calls out by name.</item>
/// <item>Provenance has to survive the bridge intact, including its refusals: an unattested corpus must
/// still be unable to support an accuracy figure once it has been tokenised.</item>
/// <item>Two corpora tokenised differently are not comparable, so the declared tokenisation is binding and
/// a mismatch must fail loudly rather than silently re-stamp the corpus.</item>
/// <item>A label must never claim to cover more than it measured.</item>
/// </list>
/// </remarks>
public class CorpusTokenisationTests
{
    private static CorpusProvenance Provenance(CorpusQualification? qualification = null) => new(
        new CorpusOrigin(
            Description: "eBible, Sena",
            Uri: "https://github.com/BibleNLP/ebible",
            RetrievedUtc: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            Licence: "CC-BY-SA-4.0"),
        new TokenisationRecord("whitespace-and-punctuation", "1", "Splits on whitespace; strips edge punctuation."),
        qualification);

    private static CorpusDocument Document(string id, string text) => new(
        DocumentId: id,
        Title: id,
        Source: new DocumentSource.File($"{id}.txt"),
        Text: text,
        ContentSha256: new string('0', 64),
        IngestedUtc: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A tokeniser that otherwise behaves like the real one but records the order it was asked to
    /// tokenise texts in — the fact CorpusDescriptor's final, sorted Words can never reveal on its own.</summary>
    private sealed class RecordingTokeniser : IWordTokeniser
    {
        private readonly WhitespaceAndPunctuationTokeniser _inner = new();

        public string Name => _inner.Name;
        public string Version => _inner.Version;
        public string Notes => _inner.Notes;
        public List<string> TokenisedTexts { get; } = new();

        public IEnumerable<string> Tokenise(string text)
        {
            TokenisedTexts.Add(text);
            return _inner.Tokenise(text);
        }
    }

    /// <summary>
    /// Pins the glottal-stop limitation so it stays a recorded decision rather than becoming a surprise.
    /// </summary>
    /// <remarks>
    /// .NET classifies U+0027 and U+2019 as punctuation and U+02BB / U+02BC / U+0294 as letters, so an edge
    /// glottal written the way a keyboard produces it is stripped while the typographically correct
    /// characters survive. That is the same failure the word-internal rule prevents, at the other end of the
    /// token, and it hits legacy data for exactly the languages this project serves. It is not fixable by
    /// classification alone — <c>'mbali'</c> is genuinely ambiguous between a quoted word and one with edge
    /// glottals — so this test exists to make the behaviour visible and deliberate. See <c>docs/issues.md</c>
    /// <c>B29</c>; the real fix is a writing-system-aware tokeniser reading Valid Characters.
    /// </remarks>
    [Fact]
    public void EdgeGlottalsSurviveOnlyWhenWrittenWithLetterCharacters()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        // Written as letters (U+02BB okina, U+02BC modifier apostrophe, U+0294 glottal stop): kept.
        Assert.Equal(new[] { "ʻmbaliʼ", "ʔa" },
            tokeniser.Tokenise("ʻmbaliʼ ʔa").ToArray());

        // Written as punctuation (U+0027 plain, U+2019 curly): stripped. This is the lossy case.
        Assert.Equal(new[] { "mbali", "a" },
            tokeniser.Tokenise("'mbali’ ’a").ToArray());

        // Word-internal is unaffected either way — that rule already holds and must keep holding.
        Assert.Equal(new[] { "mba'li", "mba’li" },
            tokeniser.Tokenise("mba'li mba’li").ToArray());
    }

    /// <summary>
    /// An empty document selection is refused, because the number it would produce looks measured.
    /// </summary>
    /// <remarks>
    /// Zero words build a perfectly valid descriptor, and a grammar coverage figure over it is 0% of nothing
    /// — indistinguishable in a report from a grammar that analysed none of a real corpus. Passing
    /// <c>null</c> is how a caller says "all of them"; passing an empty collection is a bug, and the failure
    /// is the quiet kind that only shows up as a number somebody trusts.
    /// </remarks>
    [Fact]
    public void AnEmptyDocumentSelectionIsRefusedRatherThanMeasured()
    {
        var corpus = new StoredCorpus("ebible-seh", Provenance(),
            new[] { Document("sehNT", "mbali nyumba") });

        var ex = Assert.Throws<ArgumentException>(() =>
            CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser(), Array.Empty<string>()));

        Assert.Contains("not a figure", ex.Message);

        // And null still means every document — the two must not be conflated.
        var all = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser(), null);
        Assert.Equal(new[] { "mbali", "nyumba" }, all.Words);
    }

    // ---------------------------------------------------------------- the asymmetry

    [Fact]
    public void OrderAndRepetitionSurviveTokenisation_ButTheResultingDescriptorIsSortedAndDistinct()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();
        var corpus = StoredCorpus.Create("c", Provenance())
            .With(Document("d", "nyumba mbali mbali nyumba"));

        // Half one: the tokeniser itself must not sort or deduplicate — order and repetition are the data.
        Assert.Equal(
            new[] { "nyumba", "mbali", "mbali", "nyumba" },
            tokeniser.Tokenise(corpus.Documents[0].Text));

        // Half two: CorpusTokenisation.ToDescriptor's result is sorted and distinct, because that
        // transformation belongs to CorpusDescriptor.Create alone, never to the bridge or the tokeniser.
        var descriptor = CorpusTokenisation.ToDescriptor(corpus, tokeniser);
        Assert.Equal(new[] { "mbali", "nyumba" }, descriptor.Words);
    }

    // ---------------------------------------------------------------- tokenisation rules

    [Fact]
    public void WordInternalApostrophesAndHyphensSurvive_ButEdgePunctuationIsStripped()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        var tokens = tokeniser.Tokenise("\"don't\" (mother-in-law), 'quoted'.").ToList();

        // A form invented by splitting on a word-internal apostrophe or hyphen fails to parse and reads as a
        // gap in the grammar that is really a tokenisation artefact — the specific failure to avoid.
        Assert.Contains("don't", tokens);
        Assert.Contains("mother-in-law", tokens);

        // Edge punctuation — quotes, parentheses, the trailing comma and period — must not survive in any
        // yielded token.
        Assert.DoesNotContain(tokens, t => t.Contains('"') || t.Contains('(') || t.Contains(')'));
        Assert.Equal("quoted", tokens[^1]);
    }

    [Fact]
    public void PureDigitTokensAreDropped_ButAlphanumericMixturesAreKept()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        var tokens = tokeniser.Tokenise("Verse 23 says the 2nd house at v42 stood.").ToList();

        // A verse number or footnote marker is not a word form; keeping it would pollute a coverage figure
        // with tokens no grammar is meant to analyse.
        Assert.DoesNotContain("23", tokens);

        // But a form that merely contains digits is not "entirely digits" — dropping it would be guessing.
        Assert.Contains("2nd", tokens);
        Assert.Contains("v42", tokens);
    }

    [Fact]
    public void CaseIsNotFolded()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        var tokens = tokeniser.Tokenise("Mbali MBALI mbali").ToList();

        // Casing is a vernacular-orthography question, not the tokeniser's to normalise — three differently
        // cased spellings must survive as three distinct forms.
        Assert.Equal(new[] { "Mbali", "MBALI", "mbali" }, tokens);
    }

    // ---------------------------------------------------------------- provenance

    [Fact]
    public void ProvenanceFlowsThrough_AndAnUnattestedCorpusStillCannotSupportAccuracyAfterTheBridge()
    {
        var corpus = StoredCorpus.Create("seh-wikipedia", Provenance())
            .With(Document("d", "mbali nyumba"));

        var descriptor = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser());

        Assert.Equal(corpus.Provenance, descriptor.Provenance);

        // Building the bridge must not be a back door around ADR 0036 decision 5: no qualification means no
        // accuracy claims, before the bridge and after it alike.
        Assert.False(descriptor.SupportsAccuracyClaims);
    }

    [Fact]
    public void AQualifiedCorpusSupportsAccuracyAfterTheBridgeToo()
    {
        var qualification = new CorpusQualification(
            KnownClean: true, InScope: true, Attestor: "A. Linguist",
            AttestedUtc: DateTimeOffset.UtcNow, Note: "Checked.");
        var corpus = StoredCorpus.Create("c", Provenance(qualification))
            .With(Document("d", "mbali"));

        var descriptor = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser());

        // The bridge must not accidentally launder qualification away either — it is a straight pass-through.
        Assert.True(descriptor.SupportsAccuracyClaims);
    }

    // ---------------------------------------------------------------- the declared tokenisation is binding

    [Fact]
    public void DeclaredVsSuppliedTokeniserMismatchThrows_NamingBothTheDeclaredAndSuppliedValues()
    {
        var declaredElsewhere = new CorpusProvenance(
            new CorpusOrigin("eBible, Sena", null, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), "CC-BY-SA-4.0"),
            new TokenisationRecord("SIL.Machine LatinWordTokenizer", "3.6.2", "A different tokeniser's notes."));
        var corpus = StoredCorpus.Create("c", declaredElsewhere).With(Document("d", "mbali"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser()));

        // Two corpora tokenised differently are not comparable even over identical bytes (ADR 0036 decision
        // 4); the message must name both the declared and the supplied values, or a reader cannot tell which
        // one is the mistake.
        Assert.Contains("SIL.Machine LatinWordTokenizer", ex.Message);
        Assert.Contains("3.6.2", ex.Message);
        Assert.Contains("whitespace-and-punctuation", ex.Message);
    }

    // ---------------------------------------------------------------- document selection

    [Fact]
    public void ADocumentSubsetProducesALabelThatDoesNotImplyTheWholeCorpus()
    {
        var corpus = StoredCorpus.Create("ebible-seh", Provenance())
            .With(Document("sehNT", "mbali"))
            .With(Document("sehPD", "nyumba"));

        var whole = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser());
        var subset = CorpusTokenisation.ToDescriptor(
            corpus, new WhitespaceAndPunctuationTokeniser(), documentIds: new[] { "sehNT" });

        // Following LcmWordformCorpus.Extract's precedent: a label must not let a figure look like it covers
        // more than it actually measured.
        Assert.Equal("ebible-seh", whole.CorpusId);
        Assert.NotEqual("ebible-seh", subset.CorpusId);
        Assert.Contains("sehNT", subset.CorpusId);
        Assert.Equal(new[] { "mbali" }, subset.Words);
    }

    [Fact]
    public void UnknownDocumentIdThrows_NamingTheId()
    {
        var corpus = StoredCorpus.Create("c", Provenance()).With(Document("d1", "mbali"));

        var ex = Assert.Throws<ArgumentException>(() => CorpusTokenisation.ToDescriptor(
            corpus, new WhitespaceAndPunctuationTokeniser(), documentIds: new[] { "no-such-doc" }));

        Assert.Contains("no-such-doc", ex.Message);
    }

    [Fact]
    public void MultipleDocumentsConcatenateInTheCorpusOrder()
    {
        var recorder = new RecordingTokeniser();
        var corpus = StoredCorpus.Create("c", Provenance())
            .With(Document("first", "nyumba"))
            .With(Document("second", "mbali"))
            .With(Document("third", "chuma"));

        CorpusTokenisation.ToDescriptor(corpus, recorder);

        // The order Documents were added is the order an n-gram model built downstream would see. Getting
        // this wrong is invisible in the final descriptor, because CorpusDescriptor.Create sorts — so this
        // has to be pinned at the point the bridge feeds text to the tokeniser, not at the final result.
        Assert.Equal(new[] { "nyumba", "mbali", "chuma" }, recorder.TokenisedTexts);
    }
}
