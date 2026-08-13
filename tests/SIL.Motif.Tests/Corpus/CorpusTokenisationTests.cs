using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// The bridge from Documents — running text, in order, with repetition — to a CorpusDescriptor — sorted,
/// distinct word forms — the missing connection between ingestion and measurement.
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
            Description: "eBible, Testlang",
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

    /// <summary>Wraps the real tokeniser but records call order — sorted Words can never reveal that.</summary>
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
    /// glottals — so this test exists to make the behaviour visible and deliberate. The real fix is a
    /// writing-system-aware tokeniser reading Valid Characters.
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
        var corpus = new StoredCorpus("ebible-tst", Provenance(),
            new[] { Document("tstNT", "mbali nyumba") });

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

        // Half two: ToDescriptor's result is sorted/distinct — that's CorpusDescriptor.Create's job, not the bridge's.
        var descriptor = CorpusTokenisation.ToDescriptor(corpus, tokeniser);
        Assert.Equal(new[] { "mbali", "nyumba" }, descriptor.Words);
    }

    // ---------------------------------------------------------------- tokenisation rules

    [Fact]
    public void WordInternalApostrophesAndHyphensSurvive_ButEdgePunctuationIsStripped()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        var tokens = tokeniser.Tokenise("\"don't\" (mother-in-law), 'quoted'.").ToList();

        // Splitting on a word-internal apostrophe/hyphen invents a form that misreads as a grammar gap, not a bug.
        Assert.Contains("don't", tokens);
        Assert.Contains("mother-in-law", tokens);

        // Edge punctuation (quotes, parens, trailing comma/period) must not survive in any yielded token.
        Assert.DoesNotContain(tokens, t => t.Contains('"') || t.Contains('(') || t.Contains(')'));
        Assert.Equal("quoted", tokens[^1]);
    }

    [Fact]
    public void PureDigitTokensAreDropped_ButAlphanumericMixturesAreKept()
    {
        var tokeniser = new WhitespaceAndPunctuationTokeniser();

        var tokens = tokeniser.Tokenise("Verse 23 says the 2nd house at v42 stood.").ToList();

        // A verse number or footnote marker is not a word form; keeping it would pollute a coverage figure.
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

        // Casing is a vernacular-orthography question, not the tokeniser's — three casings stay three distinct forms.
        Assert.Equal(new[] { "Mbali", "MBALI", "mbali" }, tokens);
    }

    // ---------------------------------------------------------------- provenance

    [Fact]
    public void ProvenanceFlowsThrough_AndAnUnattestedCorpusStillCannotSupportAccuracyAfterTheBridge()
    {
        var corpus = StoredCorpus.Create("tst-wikipedia", Provenance())
            .With(Document("d", "mbali nyumba"));

        var descriptor = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser());

        Assert.Equal(corpus.Provenance, descriptor.Provenance);

        // Not a back door around ADR 0036 decision 5: no qualification means no accuracy claims, before or after.
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
            new CorpusOrigin("eBible, Testlang", null, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), "CC-BY-SA-4.0"),
            new TokenisationRecord("SIL.Machine LatinWordTokenizer", "3.6.2", "A different tokeniser's notes."));
        var corpus = StoredCorpus.Create("c", declaredElsewhere).With(Document("d", "mbali"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser()));

        // Differently-tokenised corpora aren't comparable (ADR 0036 decision 4); message must name both values.
        Assert.Contains("SIL.Machine LatinWordTokenizer", ex.Message);
        Assert.Contains("3.6.2", ex.Message);
        Assert.Contains("whitespace-and-punctuation", ex.Message);
    }

    // ---------------------------------------------------------------- document selection

    [Fact]
    public void ADocumentSubsetProducesALabelThatDoesNotImplyTheWholeCorpus()
    {
        var corpus = StoredCorpus.Create("ebible-tst", Provenance())
            .With(Document("tstNT", "mbali"))
            .With(Document("tstPD", "nyumba"));

        var whole = CorpusTokenisation.ToDescriptor(corpus, new WhitespaceAndPunctuationTokeniser());
        var subset = CorpusTokenisation.ToDescriptor(
            corpus, new WhitespaceAndPunctuationTokeniser(), documentIds: new[] { "tstNT" });

        // Following LcmWordformCorpus.Extract's precedent: a label must not claim to cover more than it measured.
        Assert.Equal("ebible-tst", whole.CorpusId);
        Assert.NotEqual("ebible-tst", subset.CorpusId);
        Assert.Contains("tstNT", subset.CorpusId);
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

        // Document order is what a downstream n-gram model sees; invisible after Create sorts, so pin it at the feed.
        Assert.Equal(new[] { "nyumba", "mbali", "chuma" }, recorder.TokenisedTexts);
    }
}
