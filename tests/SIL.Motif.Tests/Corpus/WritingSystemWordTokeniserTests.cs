using SIL.LCModel.Core.WritingSystems;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Host.LcmUtils;
using SIL.WritingSystems;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// <see cref="WritingSystemWordTokeniser"/> segments the way FieldWorks' own <c>WordMaker</c> does — maximal
/// runs of characters the writing system's <c>get_IsWordForming</c> accepts — rather than by .NET's general
/// Unicode punctuation classification, which is what <see cref="WhitespaceAndPunctuationTokeniser"/> uses and
/// why it silently strips an edge glottal stop written as a plain or curly apostrophe.
/// </summary>
/// <remarks>
/// The rules these tests defend, in order of how much damage getting them wrong would do:
/// <list type="number">
/// <item>An edge glottal stop written with a letter the writing system has declared in its LDML "main"
/// character set must survive — this is the entire point of the new tokeniser over the old one.</item>
/// <item>When a writing system has never declared "main" (the common, unconfigured case), this tokeniser must
/// agree with FieldWorks' own documented behaviour and drop the apostrophe — reproducing FieldWorks exactly,
/// including its rough edges, is correct here because the lexicon it measures against was built under that
/// same behaviour. "Fixing" this in Motif alone would make Motif disagree with the tool that built the data.</item>
/// <item>A non-BMP word-forming codepoint must never be split across its surrogate pair, because
/// <c>get_IsWordForming</c> is asked about the codepoint, not about a lone surrogate half.</item>
/// <item>Order and repetition must survive the tokeniser itself, exactly as the existing tokeniser's contract
/// requires, and <see cref="CorpusTokenisation"/> must still be the only place that sorts and deduplicates.</item>
/// <item>The degradation diagnostic must be silent when nothing is degraded and, when something is, must name
/// the one fix that actually repairs it — the Valid Characters wizard — because no amount of cleverness inside
/// Motif substitutes for populating the writing system's own declared characters.</item>
/// <item>The Name/Version binding <see cref="CorpusTokenisation"/> enforces for the existing tokeniser must
/// hold for this one too, so two differently-tokenised corpora still cannot be silently conflated.</item>
/// <item><c>WritingSystemManager.Set(string)</c> resolves a script code through the SLDR, which throws
/// <see cref="InvalidOperationException"/> if initialized twice in one process — so the static constructor
/// reuses <see cref="FwDataProjectLoader.Init"/>'s idempotent bootstrap rather than calling
/// <c>Sldr.Initialize</c> directly, avoiding a race with another LibLCM-touching test class's constructor.</item>
/// </list>
/// </remarks>
public class WritingSystemWordTokeniserTests
{
    // SLDR must init exactly once per process; reuses FwDataProjectLoader.Init (see class remarks).
    static WritingSystemWordTokeniserTests()
    {
        FwDataProjectLoader.Init();
    }

    /// <summary>Builds a writing system in-process, as liblcm's own get_IsWordForming tests do.</summary>
    private static CoreWritingSystemDefinition WritingSystem(string id, params string[] mainCharacters)
    {
        var wsManager = new WritingSystemManager();
        var ws = wsManager.Set(id);
        if (mainCharacters.Length > 0)
        {
            var main = new CharacterSetDefinition("main");
            foreach (var c in mainCharacters) main.Characters.Add(c);
            ws.CharacterSets.Add(main);
        }
        return ws;
    }

    /// <summary>
    /// Digits are not word-forming, so this tokeniser <b>splits</b> on them — and declaring them fixes it.
    /// </summary>
    /// <remarks>
    /// <c>TsStringUtils.IsWordForming</c> accepts only letter, mark and modifier-symbol categories; no numeric
    /// category appears in it at all. This is FieldWorks' behaviour and therefore correct here, but it is a
    /// far larger divergence from <see cref="WhitespaceAndPunctuationTokeniser"/> — which deliberately keeps
    /// alphanumeric mixtures whole — than the apostrophe case that prompted this class, and it is easy to
    /// assume the two tokenisers differ only over punctuation. The case that makes it matter: an orthography
    /// that <b>marks tone with digits</b> is shredded rather than merely trimmed, which no amount of
    /// edge-handling would catch. The remedy is the same as for the apostrophe — declare the digits in "main"
    /// — which is why this test asserts both halves.
    /// </remarks>
    [Fact]
    public void DigitsSplitAWordUntilTheWritingSystemDeclaresThem()
    {
        // Undeclared: a digit is a separator, not trimmable — "ma1" loses its tone mark, "a1b" splits wrongly.
        var undeclared = new WritingSystemWordTokeniser(WritingSystem("seh"));
        Assert.Equal(new[] { "ma" }, undeclared.Tokenise("ma1").ToArray());
        Assert.Equal(new[] { "a", "b" }, undeclared.Tokenise("a1b").ToArray());
        Assert.Empty(undeclared.Tokenise("123"));

        // Declared: the same text tokenises as whole words. One writing-system edit, both tools fixed.
        var declared = new WritingSystemWordTokeniser(WritingSystem("seh", "1", "2", "3"));
        Assert.Equal(new[] { "ma1" }, declared.Tokenise("ma1").ToArray());
        Assert.Equal(new[] { "a1b" }, declared.Tokenise("a1b").ToArray());
    }

    // ---------------------------------------------------------------- the fix: edge glottals survive

    [Fact]
    public void WithApostropheDeclaredInMain_AnEdgeGlottalSurvivesAsOneToken()
    {
        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);

        // With ' declared word-forming, "'mbali'" is one run, not stripped pieces — why this tokeniser exists.
        Assert.Equal(new[] { "'mbali'" }, tokeniser.Tokenise("'mbali'").ToArray());
    }

    /// <summary>
    /// DO NOT "fix" this by special-casing apostrophes in <see cref="WritingSystemWordTokeniser"/>.
    /// </summary>
    /// <remarks>
    /// An unconfigured writing system falls through to the ICU fallback for every character, under which
    /// U+0027 is punctuation, not a letter — exactly liblcm's own
    /// <c>WritingSystemManagerTests.get_IsWordForming</c> after <c>CharacterSets.Clear()</c>. Motif must
    /// agree with FieldWorks here, not improve on it, because the lexicon being measured against was built
    /// under FieldWorks' own (unconfigured) segmentation.
    /// </remarks>
    [Fact]
    public void WithNoCharacterSetsDeclared_TheEdgeApostropheIsDropped_ThisIsDeliberateFieldWorksAgreement()
    {
        var ws = WritingSystem("seh"); // no CharacterSets.Add at all
        var tokeniser = new WritingSystemWordTokeniser(ws);

        Assert.Equal(new[] { "mbali" }, tokeniser.Tokenise("'mbali'").ToArray());
    }

    [Fact]
    public void LetterGlottalsSurviveEvenWithNoCharacterSetsDeclared_BecauseTheyAreIcuLetters()
    {
        var ws = WritingSystem("haw"); // no CharacterSets declared
        var tokeniser = new WritingSystemWordTokeniser(ws);

        // U+02BB, U+02BC, U+0294 are ICU letters, so the ICU fallback keeps them — unlike U+0027/U+2019.
        Assert.Equal(new[] { "ʻohana" }, tokeniser.Tokenise("ʻohana").ToArray());
        Assert.Equal(new[] { "mbaʼli" }, tokeniser.Tokenise("mbaʼli").ToArray());
        Assert.Equal(new[] { "ʔa" }, tokeniser.Tokenise("ʔa").ToArray());
    }

    // ---------------------------------------------------------------- order, repetition, the asymmetry

    [Fact]
    public void OrderAndRepetitionSurvive_ButCorpusTokenisationStillSortsAndDedupes()
    {
        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);

        // The tokeniser itself must not sort or dedupe — that's IWordTokeniser's contract, not the old impl's.
        Assert.Equal(
            new[] { "nyumba", "mbali", "mbali", "nyumba" },
            tokeniser.Tokenise("nyumba mbali mbali nyumba").ToArray());

        var provenance = new CorpusProvenance(
            new CorpusOrigin("test", null, DateTimeOffset.UtcNow, "CC-BY-SA-4.0"),
            new TokenisationRecord(tokeniser.Name, tokeniser.Version, tokeniser.Notes));
        var corpus = StoredCorpus.Create("c", provenance)
            .With(new CorpusDocument("d", "d", new DocumentSource.File("d.txt"),
                "nyumba mbali mbali nyumba", new string('0', 64), DateTimeOffset.UtcNow));

        var descriptor = CorpusTokenisation.ToDescriptor(corpus, tokeniser);

        // Only CorpusDescriptor.Create sorts and dedupes — never the tokeniser, never the bridge.
        Assert.Equal(new[] { "mbali", "nyumba" }, descriptor.Words);
    }

    // ---------------------------------------------------------------- surrogate pairs

    [Fact]
    public void ANonBmpWordFormingCodepointIsNotSplitAcrossItsSurrogatePair()
    {
        // U+10451 Deseret Small Letter Bee is ICU-fallback word-forming; its surrogate pair must not split.
        const string deseretLetter = "\U00010451"; // DESERET SMALL LETTER BEE (Lo)
        var ws = WritingSystem("seh"); // no CharacterSets declared
        var tokeniser = new WritingSystemWordTokeniser(ws);

        var tokens = tokeniser.Tokenise($"a{deseretLetter}b !").ToArray();

        // A single token holding the full surrogate pair intact, not two mangled halves or a split token.
        Assert.Equal(new[] { $"a{deseretLetter}b" }, tokens);
        Assert.Equal(4, tokens[0].Length); // 'a' + high+low surrogate + 'b' = 4 UTF-16 code units
    }

    // ---------------------------------------------------------------- the degradation diagnostic

    [Fact]
    public void TheDiagnosticIsAbsentWhenMainCharacterSetIsDeclared()
    {
        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);

        Assert.True(tokeniser.WritingSystemDeclaresWordFormingCharacters);
        Assert.Null(tokeniser.WhyTokenisationMayBeDegraded());
    }

    [Fact]
    public void TheDiagnosticFiresAndNamesTheValidCharactersWizard_WhenMainIsUndeclared()
    {
        var ws = WritingSystem("seh"); // no CharacterSets declared
        var tokeniser = new WritingSystemWordTokeniser(ws);

        Assert.False(tokeniser.WritingSystemDeclaresWordFormingCharacters);

        var why = tokeniser.WhyTokenisationMayBeDegraded();
        Assert.NotNull(why);
        // The only fix that repairs both tools at once — no cleverness inside Motif substitutes for it.
        Assert.Contains("Valid Characters wizard", why);
        Assert.Contains("seh", why); // names the writing system a degraded figure was computed under
    }

    [Fact]
    public void TheDiagnosticFiresWhenMainIsPresentButEmpty()
    {
        // An empty "main" set must still count as undeclared — it contributes nothing to get_IsWordForming.
        var wsManager = new WritingSystemManager();
        var ws = wsManager.Set("seh");
        ws.CharacterSets.Add(new CharacterSetDefinition("main"));
        var tokeniser = new WritingSystemWordTokeniser(ws);

        Assert.False(tokeniser.WritingSystemDeclaresWordFormingCharacters);
        Assert.NotNull(tokeniser.WhyTokenisationMayBeDegraded());
    }

    // ---------------------------------------------------------------- Notes names the writing system

    [Fact]
    public void NotesNameTheWritingSystemAndTheWordMakerLineage()
    {
        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);

        // Notes must name the writing system, not just the algorithm — figures across writing systems don't compare.
        Assert.Contains("seh", tokeniser.Notes);
        Assert.Contains("WordMaker", tokeniser.Notes);
        Assert.Contains("main", tokeniser.Notes);
    }

    // ---------------------------------------------------------------- Name/Version binding still holds

    [Fact]
    public void TheOldTokeniserNameAndVersionAreUnchanged()
    {
        // This new tokeniser must not touch the existing one — corpora declaring the old name still work.
        var whitespaceTokeniser = new WhitespaceAndPunctuationTokeniser();
        Assert.Equal("whitespace-and-punctuation", whitespaceTokeniser.Name);
        Assert.Equal("1", whitespaceTokeniser.Version);
    }

    [Fact]
    public void CorpusTokenisationRefusesThisTokeniser_ForACorpusDeclaringTheOldOne()
    {
        var provenance = new CorpusProvenance(
            new CorpusOrigin("test", null, DateTimeOffset.UtcNow, "CC-BY-SA-4.0"),
            new TokenisationRecord("whitespace-and-punctuation", "1", "Splits on whitespace; strips edge punctuation."));
        var corpus = StoredCorpus.Create("c", provenance)
            .With(new CorpusDocument("d", "d", new DocumentSource.File("d.txt"),
                "mbali", new string('0', 64), DateTimeOffset.UtcNow));

        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);

        // ToDescriptor must still refuse a mismatched tokeniser — differently-tokenised corpora aren't comparable.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CorpusTokenisation.ToDescriptor(corpus, tokeniser));
        Assert.Contains("whitespace-and-punctuation", ex.Message);
        Assert.Contains("fieldworks-word-forming", ex.Message);
    }

    [Fact]
    public void CorpusTokenisationAcceptsThisTokeniser_ForACorpusDeclaringIt()
    {
        var ws = WritingSystem("seh", "'");
        var tokeniser = new WritingSystemWordTokeniser(ws);
        var provenance = new CorpusProvenance(
            new CorpusOrigin("test", null, DateTimeOffset.UtcNow, "CC-BY-SA-4.0"),
            new TokenisationRecord(tokeniser.Name, tokeniser.Version, tokeniser.Notes));
        var corpus = StoredCorpus.Create("c", provenance)
            .With(new CorpusDocument("d", "d", new DocumentSource.File("d.txt"),
                "'mbali' nyumba", new string('0', 64), DateTimeOffset.UtcNow));

        var descriptor = CorpusTokenisation.ToDescriptor(corpus, tokeniser);

        Assert.Equal(new[] { "'mbali'", "nyumba" }, descriptor.Words);
    }
}
