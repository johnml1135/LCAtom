namespace SIL.Motif.Host.Corpus;

/// <summary>
/// A BCL-only <see cref="IWordTokeniser"/>: split on whitespace, trim edge punctuation, drop pure numerals.
/// <para>
/// <b>Prefer <see cref="WritingSystemWordTokeniser"/> for anything measured against a FieldWorks grammar.</b>
/// That one asks the project's own writing system which characters are word-forming, which is what FieldWorks
/// does; this one asks .NET's general Unicode classification, which is why it has the limitation described
/// below. This class remains because a corpus declares the tokeniser it was actually tokenised with, and
/// corpora already tokenised this way must keep working.
/// </para>
/// No dependency beyond the .NET base class library, so this project can bridge Documents into a
/// <see cref="CorpusDescriptor"/> without referencing SIL.Machine. A SIL.Machine tokeniser may sit behind
/// <see cref="IWordTokeniser"/> as well; this one is the fallback that ships with Motif itself, not a
/// placeholder for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule below is a deliberate choice, not an oversight</b> — <c>docs/adr/0036</c> decision 4 says
/// tokenisation decides most of what "unparsed" means, so what looks like a minor detail here is the
/// difference between a real grammar gap and a self-inflicted one:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Splits on Unicode whitespace</b> (<see cref="char.IsWhiteSpace(char)"/>, via the BCL's default
/// <see cref="string.Split(char[]?, StringSplitOptions)"/> behaviour) — not just the ASCII space, so a verse
/// using a no-break space or a line separator still splits correctly.
/// </item>
/// <item>
/// <b>Strips leading and trailing punctuation and symbol characters</b>
/// (<see cref="char.IsPunctuation(char)"/> / <see cref="char.IsSymbol(char)"/>) from each token, <b>but only
/// at the edges</b>. Word-internal punctuation — an apostrophe in <c>don't</c>, a hyphen in
/// <c>mother-in-law</c> — is left alone. A tokeniser that split on those would invent a form neither half of
/// which is the real word, and that invented form fails to parse and reads as a gap in the grammar rather
/// than the tokenisation artefact it actually is — the specific failure this rule exists to avoid.
/// </item>
/// <item>
/// <b>Drops tokens that are empty after stripping</b> — a token that was pure punctuation (an em dash on its
/// own line, a stray quotation mark) leaves nothing behind and contributes no word form.
/// </item>
/// <item>
/// <b>Drops tokens that are entirely digits</b>, because a verse number or a footnote marker is not a word
/// form. <b>Keeps alphanumeric mixtures</b> such as <c>2nd</c> or a form containing internal digits, because
/// those are not pure numerals and dropping them would be guessing.
/// </item>
/// <item>
/// <b>Does not change case.</b> Casing is a vernacular-orthography question — whether a language's own
/// conventions distinguish sentence-initial forms, proper nouns, or nothing at all — and normalising it here
/// would be Motif deciding an orthography question it has no standing to decide.
/// </item>
/// </list>
/// <para>
/// <b>Known limitation, and it bites exactly the languages this project serves.</b> A <b>word-initial or
/// word-final glottal stop written with a plain <c>'</c> (U+0027) or a
/// curly <c>’</c> (U+2019) is stripped</b>, because .NET classifies both as punctuation. Measured:
/// </para>
/// <list type="table">
/// <item><term>U+0027 APOSTROPHE</term><description>punctuation — <b>stripped at edges</b></description></item>
/// <item><term>U+2019 RIGHT SINGLE QUOTATION MARK</term><description>punctuation — <b>stripped at edges</b></description></item>
/// <item><term>U+02BB MODIFIER LETTER TURNED COMMA (ʻokina)</term><description>letter — survives</description></item>
/// <item><term>U+02BC MODIFIER LETTER APOSTROPHE</term><description>letter — survives</description></item>
/// <item><term>U+0294 LATIN LETTER GLOTTAL STOP</term><description>letter — survives</description></item>
/// </list>
/// <para>
/// So a project using the typographically correct characters is fine, and a project using what a keyboard
/// produces — which is most legacy data — silently loses an edge glottal and reads as a grammar gap. This is
/// the same failure the word-internal rule above exists to prevent, at the other end of the token.
/// </para>
/// <para>
/// <b>It is not fixable by classification alone</b>, which is why it is documented rather than patched:
/// <c>'mbali'</c> is genuinely ambiguous between a quoted word and a word with initial and final glottals,
/// and nothing in the character stream distinguishes them. The real fix is a writing-system-aware tokeniser
/// consulting the project's Valid Characters list — which Motif already reads, in
/// <see cref="WritingSystems.WritingSystemInventory"/>. Until then, a project in this situation should
/// declare a different tokeniser rather than accept this one's answer.
/// </para>
/// </remarks>
public sealed class WhitespaceAndPunctuationTokeniser : IWordTokeniser
{
    /// <summary>
    /// Matched against a corpus's declared <see cref="TokenisationRecord.Method"/> by
    /// <see cref="CorpusTokenisation.ToDescriptor"/>. This exact string is a fixture value in
    /// <c>CorpusIngestionTests</c> and <c>CorpusProvenanceTests</c> — changing it changes what those tests
    /// mean, not just what this class is called.
    /// </summary>
    public string Name => "whitespace-and-punctuation";

    /// <summary>
    /// Matched against a corpus's declared <see cref="TokenisationRecord.Version"/>. See <see cref="Name"/>
    /// for why this exact string matters beyond this class.
    /// </summary>
    public string Version => "1";

    /// <inheritdoc />
    public string Notes =>
        "Splits on Unicode whitespace. Strips leading and trailing punctuation and symbol characters " +
        "(char.IsPunctuation / char.IsSymbol) from each token but keeps word-internal punctuation such as " +
        "apostrophes and hyphens, because a form invented by splitting on one fails to parse and reads as a " +
        "gap in the grammar. Drops tokens left empty after stripping. Drops tokens that are entirely digits; " +
        "keeps alphanumeric mixtures. Does not change case — casing is a vernacular-orthography question, " +
        "not this tokeniser's to normalise. KNOWN LIMITATION: a word-initial or word-final glottal stop " +
        "written with U+0027 or U+2019 is stripped, because .NET classifies both as punctuation; U+02BB, " +
        "U+02BC and U+0294 are letters and survive. Declare a different tokeniser if your orthography uses " +
        "the former.";

    /// <inheritdoc />
    public IEnumerable<string> Tokenise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TokeniseCore(text);
    }

    private static IEnumerable<string> TokeniseCore(string text)
    {
        // (char[]?)null splits on Unicode whitespace (char.IsWhiteSpace), not just the ASCII space.
        foreach (var rawToken in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = StripEdgePunctuationAndSymbols(rawToken);
            if (trimmed.Length == 0) continue; // was punctuation-only; nothing to yield
            if (IsAllDigits(trimmed)) continue; // a verse number or footnote marker, not a word form
            yield return trimmed;
        }
    }

    /// <summary>Trims edge punctuation/symbols only, leaving <c>don't</c> and <c>mother-in-law</c> intact.</summary>
    private static string StripEdgePunctuationAndSymbols(string token)
    {
        var start = 0;
        var end = token.Length;

        while (start < end && IsEdgeCharacter(token[start])) start++;
        while (end > start && IsEdgeCharacter(token[end - 1])) end--;

        return start == 0 && end == token.Length ? token : token[start..end];
    }

    private static bool IsEdgeCharacter(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool IsAllDigits(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }
}
