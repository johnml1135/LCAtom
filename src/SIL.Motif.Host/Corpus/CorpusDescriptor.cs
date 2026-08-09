using System.Security.Cryptography;
using System.Text;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// A deterministic, hashable description of the words a coverage figure was measured over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this has to exist at all.</b> A coverage percentage is meaningless without saying which words it
/// was measured over — the same grammar can score anywhere from 0% to 100% depending on the sample, and a
/// number that silently changes because someone else's Chorus sync added or removed a wordform is worse
/// than no number (<c>docs/adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md</c> §4, closing `B24`).
/// A corpus hash is what makes that drift detectable rather than invisible.
/// </para>
/// <para>
/// <b>Deliberately independent of <c>LcmCache</c> and of the parser.</b> Hashing an ordered word list has
/// nothing to do with FieldWorks or PanGloss, so this type takes a plain word list and nothing else — every
/// test of the hashing and ordering behaviour below runs with no project and no parser executable. See
/// <see cref="SIL.Motif.Host.Corpus.LcmWordformCorpus"/> for the part that actually opens a project.
/// </para>
/// </remarks>
/// <param name="CorpusId">
/// A caller-assigned label for what this corpus is — typically a project name, or a project name plus a
/// note that it was capped. This is deliberately not derived automatically from anything: a caller who
/// measured a deliberately limited subset must be able to say so here, rather than have a figure look like
/// it covers a whole project when it does not.
/// </param>
/// <param name="Words">
/// The corpus's word forms, sorted ordinally and <b>distinct</b>. Sorting makes <see cref="Sha256"/> depend
/// only on content — two extractions that merely enumerated the same wordforms in a different order must
/// hash identically, or the hash would flag drift that never happened.
/// <para>
/// Deduplicating is the same argument one step further, and it was missed on the first pass. A corpus is a
/// <i>set</i> of word forms: <see cref="Parser.CoverageFigure.Compute"/> compares it to what was analysed with
/// set semantics, and a word form analysed twice is analysed once. If this list kept duplicates, two corpora
/// covering identically the same words would carry different hashes and a figure would report drift against
/// itself. Identity and comparison have to agree about what a corpus is.
/// </para>
/// </param>
/// <param name="Sha256">
/// <c>sha256:</c> followed by 64 lowercase hex characters, computed over <see cref="Words"/> joined by
/// newlines. A coverage figure citing a <see cref="CorpusId"/> whose current hash no longer matches this
/// one is stale and must say so rather than presenting itself as current.
/// </param>
/// <param name="Provenance">
/// Where the words came from, how text became word forms, and what — if anything — somebody attests about the
/// result. Optional only so that a corpus extracted from the open project itself, whose origin is not in
/// question, need not restate it; **any corpus from outside the project should carry one**, and a corpus
/// without one can never support an accuracy figure.
/// </param>
public sealed record CorpusDescriptor(
    string CorpusId,
    IReadOnlyList<string> Words,
    string Sha256,
    CorpusProvenance? Provenance = null)
{
    /// <summary>
    /// Whether a report over this corpus may state accuracy, as opposed to reach.
    /// </summary>
    /// <remarks>
    /// A corpus with no provenance cannot: an unattested corpus is exactly the case where a failed analysis is
    /// ambiguous between a grammar gap, a typo, and an out-of-scope token. Reach figures are unaffected —
    /// they are what a large uncurated corpus is <i>for</i>.
    /// </remarks>
    public bool SupportsAccuracyClaims => Provenance?.SupportsAccuracyClaims ?? false;

    /// <summary>
    /// The only supported way to build a <see cref="CorpusDescriptor"/>: deduplicates and sorts
    /// <paramref name="words"/>, then hashes the result, so <see cref="Words"/> and <see cref="Sha256"/> can
    /// never disagree with each other the way they could if a caller were allowed to hand both in
    /// independently.
    /// </summary>
    /// <remarks>
    /// <b>Deduplicating and sorting here is right for a selection and wrong for a corpus, and the distinction
    /// matters now that this data feeds more than the parser.</b> This type is the set of distinct forms handed
    /// to PanGloss, where order and repetition are noise. The <i>corpus</i> — the running text it was derived
    /// from — keeps both, because frequency is what ranks the unparsed-form worklist and sequence is what
    /// n-gram models for spelling correction and word prediction are built from. Do not reach for this type
    /// when you need either.
    /// </remarks>
    public static CorpusDescriptor Create(
        string corpusId, IEnumerable<string> words, CorpusProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(corpusId)) throw new ArgumentException("Required.", nameof(corpusId));
        if (words is null) throw new ArgumentNullException(nameof(words));

        var ordered = words
            .Distinct(StringComparer.Ordinal)
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();
        return new CorpusDescriptor(corpusId, ordered, ComputeSha256(ordered), provenance);
    }

    /// <summary>
    /// Newline-joined UTF-8 bytes, not canonical JSON. A flat list of plain word forms has no structure a
    /// delimiter can't carry — none of Sena 3's, Amharic's, or Indonesian's wordforms contain a literal
    /// newline — so the extra machinery RFC 8785 canonicalization exists for elsewhere in this codebase
    /// (<c>SIL.Motif.Contract.Canonicalization</c>) would buy nothing here, and this type deliberately has
    /// no dependency on that project (see the class remarks).
    /// </summary>
    private static string ComputeSha256(IReadOnlyList<string> orderedWords)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", orderedWords));
        var hash = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
