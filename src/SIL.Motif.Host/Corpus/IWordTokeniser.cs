namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Turns running text into word forms — the bridge <see cref="CorpusTokenisation"/> needs to turn a
/// <see cref="StoredCorpus"/>'s Documents into a <see cref="CorpusDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is an interface at all.</b> At corpus scale, tokenisation decides most of what "unparsed"
/// means (<c>docs/adr/0036-motif-has-its-own-data-store.md</c> decision 4): a form invented by splitting on
/// an apostrophe fails to parse and reads as a gap in the grammar rather than as the tokenisation artefact it
/// actually is. SIL.Machine's <c>LatinWordTokenizer</c> is the house candidate for real use, but this project
/// takes no dependency on SIL.Machine — this interface is the seam a SIL.Machine-backed implementation drops
/// in behind later, in whatever project is allowed to reference it.
/// <see cref="WhitespaceAndPunctuationTokeniser"/> is the BCL-only implementation available today.
/// </para>
/// <para>
/// <b>Name and Version are load-bearing, not descriptive.</b> <see cref="CorpusTokenisation.ToDescriptor"/>
/// throws rather than tokenising a corpus with an implementation whose <see cref="Name"/> or
/// <see cref="Version"/> disagrees with what the corpus's own <see cref="TokenisationRecord"/> declares
/// (pinned by `DeclaredVsSuppliedTokeniserMismatchThrows_NamingBothTheDeclaredAndSuppliedValues`) — two
/// corpora tokenised differently are not comparable even when the source text is identical, so the match
/// is checked by value, not assumed from context.
/// </para>
/// </remarks>
public interface IWordTokeniser
{
    /// <summary>
    /// The tokeniser's name, matched against a corpus's declared <see cref="TokenisationRecord.Method"/> by
    /// <see cref="CorpusTokenisation.ToDescriptor"/> before it will run.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The tokeniser's version, matched against a corpus's declared <see cref="TokenisationRecord.Version"/>.
    /// Bump this whenever the rules in <see cref="Notes"/> change, because a tokeniser version change
    /// silently changes every downstream figure computed from it.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// What this tokeniser does with the awkward cases — punctuation, numerals, mixed script, case — in the
    /// exact words that belong in a <see cref="TokenisationRecord.Notes"/> value. Lives here, rather than
    /// only in an XML doc comment, so <see cref="Describe"/> (and any caller building one by hand) does not
    /// have to retype it.
    /// </summary>
    string Notes { get; }

    /// <summary>
    /// Splits <paramref name="text"/> into word forms, in order, with repetition. Must not deduplicate or
    /// sort — that is what <see cref="CorpusDescriptor.Create"/> is for, and doing it here as well would
    /// destroy the frequency and sequence information a <see cref="CorpusDocument"/> exists to keep before
    /// <see cref="CorpusTokenisation"/> gets a chance to fold it in.
    /// </summary>
    IEnumerable<string> Tokenise(string text);

    /// <summary>
    /// Builds the <see cref="TokenisationRecord"/> a corpus tokenised with this implementation should
    /// declare, so a caller assembling a <see cref="CorpusProvenance"/> in-process gets a correct record
    /// without retyping <see cref="Name"/>, <see cref="Version"/> and <see cref="Notes"/> by hand.
    /// </summary>
    TokenisationRecord Describe() => new(Name, Version, Notes);
}
