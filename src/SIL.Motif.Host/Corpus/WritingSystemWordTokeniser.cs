using System.Text;
using SIL.LCModel.Core.WritingSystems;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// An <see cref="IWordTokeniser"/> that segments the way FieldWorks itself does: by asking a project's own
/// <see cref="CoreWritingSystemDefinition"/> which characters are word-forming, rather than asking .NET's
/// general Unicode punctuation classification (which is what <see cref="WhitespaceAndPunctuationTokeniser"/>
/// does).
/// </summary>
/// <remarks>
/// <para>
/// <b>This matches FieldWorks' real segmenter, not an idealisation of one.</b> FieldWorks' <c>WordMaker</c>
/// (liblcm <c>src/SIL.LCModel/DomainServices/ITextUtils.cs:1573-1629</c>) scans for maximal runs of
/// word-forming characters, asking the writing system via
/// <c>CoreWritingSystemDefinition.get_IsWordForming(int)</c> (liblcm
/// <c>src/SIL.LCModel.Core/WritingSystems/CoreWritingSystemDefinition.cs:166-172</c>), which is:
/// </para>
/// <code>
/// if (m_wordFormingOverrides.Contains(ch)) return true;   // from the project's LDML "main" character set
/// return TsStringUtils.IsWordForming(ch);                  // ICU letter/mark/modifier categories
/// </code>
/// <para>
/// This tokeniser calls the exact same method, on the exact same object type, in the exact same order of
/// preference: the writing system's own declared word-forming characters (its LDML <c>"main"</c> character
/// set) take precedence, and ICU's letter/mark/modifier categories are the fallback for any character the
/// writing system does not mention.
/// </para>
/// <para>
/// <b>Why "agreement" rather than "correctness" is the goal.</b> Everything Motif measures — a coverage
/// figure, a grammar gap — is measured against a grammar whose lexicon was built by FieldWorks, using
/// FieldWorks' own segmentation. If this tokeniser were independently "smarter" than FieldWorks — say, by
/// always treating <c>U+0027</c> as a possible glottal stop — it would tokenise words that FieldWorks itself
/// never produced when the lexicon was built, and every one of those would manufacture a false grammar gap
/// that has nothing to do with the grammar. Matching FieldWorks exactly, including its documented rough edges,
/// is therefore the correct behaviour for this tool's purpose, not a compromise on the way to a better one.
/// </para>
/// <para>
/// <b>This includes matching FieldWorks' behaviour for an unconfigured project.</b> A writing system that has
/// never had its Valid Characters populated has an empty <c>"main"</c> character set, so
/// <c>get_IsWordForming</c> falls through to the ICU fallback for everything — under which <c>U+0027</c> and
/// <c>U+2019</c> are punctuation, not letters, exactly as liblcm's own
/// <c>WritingSystemManagerTests.get_IsWordForming</c> (<c>tests/SIL.LCModel.Core.Tests/WritingSystems/WritingSystemManagerTests.cs:197-213</c>)
/// demonstrates by clearing <see cref="CoreWritingSystemDefinition.CharacterSets"/> and observing the answer
/// flip. So an edge apostrophe can still be dropped by this tokeniser — see <see cref="WhyTokenisationMayBeDegraded"/>
/// — and that is deliberate, not a bug this class should work around.
/// </para>
/// <para>
/// <b>A figure is only comparable to another figure computed under the same writing system.</b> Two projects'
/// writing systems can declare different <c>"main"</c> character sets, so the same input text can tokenise
/// differently under each. <see cref="Notes"/> therefore names the writing system this instance was built for.
/// </para>
/// <para>
/// <b>Digits are not word-forming, so this tokeniser splits on them — and that is a real hazard, not a
/// footnote.</b> <c>TsStringUtils.IsWordForming</c> (liblcm <c>src/SIL.LCModel.Core/Text/TsStringUtils.cs</c>)
/// accepts only letter, mark and modifier-symbol categories; <b>no numeric category appears in it at all</b>.
/// So <c>2nd</c> yields <c>nd</c>, <c>123</c> yields nothing, and — the case that matters — an orthography
/// that <b>marks tone with digits</b> gets shredded: <c>ma1</c> yields <c>ma</c>, and <c>a1b</c> yields two
/// tokens. That is a different failure from
/// <see cref="WhitespaceAndPunctuationTokeniser"/>, which deliberately keeps alphanumeric mixtures whole, so
/// the two tokenisers disagree about far more than apostrophes and a corpus must declare which it used.
/// </para>
/// <para>
/// This is FieldWorks' behaviour and is therefore correct here by the agreement argument above — and it has
/// the same remedy as the apostrophe case: <b>declare the digits in the writing system's <c>"main"</c>
/// character set</b> and they become word-forming for FieldWorks and for this tokeniser at once.
/// <see cref="WhyTokenisationMayBeDegraded"/> reports the undeclared case, which is where a
/// digit-tone-marking project would land by default.
/// </para>
/// </remarks>
public sealed class WritingSystemWordTokeniser : IWordTokeniser
{
    private readonly CoreWritingSystemDefinition _ws;

    public WritingSystemWordTokeniser(CoreWritingSystemDefinition writingSystem)
    {
        _ws = writingSystem ?? throw new ArgumentNullException(nameof(writingSystem));

        // "main" feeds get_IsWordForming's private overrides; TryGet is the only public proxy for that state.
        WritingSystemDeclaresWordFormingCharacters =
            _ws.CharacterSets.TryGet("main", out var main) && main.Characters.Count > 0;
    }

    /// <summary>
    /// Matched against a corpus's declared <see cref="TokenisationRecord.Method"/> by
    /// <see cref="CorpusTokenisation.ToSelection"/>. A new name, not a replacement for
    /// <see cref="WhitespaceAndPunctuationTokeniser.Name"/> — both must keep working, because a corpus
    /// declares whichever one it was actually tokenised with.
    /// </summary>
    public string Name => "fieldworks-word-forming";

    /// <summary>
    /// Matched against a corpus's declared <see cref="TokenisationRecord.Version"/>. See
    /// <see cref="IWordTokeniser.Version"/> for why this exact string matters beyond this class.
    /// </summary>
    public string Version => "1";

    /// <summary>
    /// True when this instance's writing system has populated its LDML <c>"main"</c> character set, so
    /// <c>get_IsWordForming</c> has something beyond the ICU fallback to consult. False means every character
    /// not already an ICU letter/mark/modifier — including a plain or curly apostrophe — is treated as
    /// non-word-forming, matching FieldWorks' own unconfigured-project behaviour.
    /// </summary>
    public bool WritingSystemDeclaresWordFormingCharacters { get; }

    /// <inheritdoc />
    public string Notes =>
        "Segmentation follows FieldWorks' WordMaker (liblcm ITextUtils.cs:1573-1629): a word is a maximal " +
        "run of characters the writing system considers word-forming, decided per character by " +
        "CoreWritingSystemDefinition.get_IsWordForming(int). The writing system's declared word-forming " +
        "characters (its LDML \"main\" character set) take precedence; ICU letter/mark/modifier categories " +
        $"are the fallback for any character the writing system does not mention. Built for writing system " +
        $"'{_ws.Id}' — a figure computed under one writing system's declared characters is not comparable to " +
        "one computed under a different writing system's, or under the same writing system before or after " +
        "its Valid Characters were edited. " +
        (WritingSystemDeclaresWordFormingCharacters
            ? "This writing system's \"main\" character set is populated."
            : "This writing system's \"main\" character set is empty or undeclared, so it currently relies " +
              "entirely on the ICU fallback — under which a plain or curly apostrophe is punctuation, not a " +
              "letter, and is dropped at a word edge. See WhyTokenisationMayBeDegraded().");

    /// <summary>
    /// When <see cref="WritingSystemDeclaresWordFormingCharacters"/> is false, explains why in words fit to
    /// surface in a report, and names the one fix that actually resolves it. Returns <c>null</c> when the
    /// writing system has declared its word-forming characters, because there is nothing degraded to report.
    /// </summary>
    public string? WhyTokenisationMayBeDegraded()
    {
        if (WritingSystemDeclaresWordFormingCharacters) return null;

        return $"Writing system '{_ws.Id}' has not declared its word-forming characters (its LDML \"main\" " +
               "character set is empty or absent), so an edge apostrophe is treated as punctuation and " +
               "dropped, matching FieldWorks' own behaviour for an unconfigured project. Running FieldWorks' " +
               "Valid Characters wizard fixes this in FieldWorks and here — no change inside Motif can " +
               "substitute for it, because Motif reads the same declared characters FieldWorks would write.";
    }

    /// <inheritdoc />
    public IEnumerable<string> Tokenise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TokeniseCore(text);
    }

    /// <summary>Codepoint by codepoint, not char by char, so a surrogate pair stays one character.</summary>
    private IEnumerable<string> TokeniseCore(string text)
    {
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            int codepoint;
            int width;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                width = 2;
            }
            else
            {
                codepoint = text[i];
                width = 1;
            }

            if (_ws.get_IsWordForming(codepoint))
            {
                current.Append(text, i, width);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            i += width;
        }

        if (current.Length > 0) yield return current.ToString();
    }
}
