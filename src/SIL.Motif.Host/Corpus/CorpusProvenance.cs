namespace SIL.Motif.Host.Corpus;

/// <summary>Where a corpus's text came from.</summary>
/// <param name="Description">Human-facing statement of the source — "Wikipedia, Sena edition".</param>
/// <param name="Uri">The retrievable location, when there is one.</param>
/// <param name="RetrievedUtc">When it was pulled. A dump moves; a corpus does not.</param>
/// <param name="Licence">
/// The licence the source text carries, verbatim — <c>CC-BY-SA-4.0</c>, <c>proprietary</c>, whatever it is.
/// <b>Not optional for external sources, and not decoration.</b> A dictionary published from Wikipedia-derived
/// data carries an attribution obligation, and the moment to record that is when the text arrives, not when
/// someone asks two years later.
/// </param>
/// <param name="Capabilities">
/// What that licence <b>permits</b>, as opposed to what it is called. Optional here and defaulted to
/// <see cref="LicenceCapabilities.Unknown"/>, because a corpus assembled inside the project from the project's
/// own words has no external licence to establish. For anything ingested from outside it should be filled in —
/// see <see cref="LicenceCapabilities"/> for why the name alone cannot answer "may I build an n-gram model
/// from this".
/// </param>
public sealed record CorpusOrigin(
    string Description,
    string? Uri,
    DateTimeOffset RetrievedUtc,
    string? Licence,
    LicenceCapabilities? Capabilities = null)
{
    /// <summary>The licence capabilities, with the honest default when none were established.</summary>
    public LicenceCapabilities EffectiveCapabilities =>
        Capabilities ?? LicenceCapabilities.Unknown();
}

/// <summary>How running text became word forms.</summary>
/// <param name="Method">What split the text — the tokeniser's name.</param>
/// <param name="Version">Its version, because a tokeniser change silently changes every downstream figure.</param>
/// <param name="Notes">What it did with the awkward cases: punctuation, numerals, mixed script, case.</param>
/// <remarks>
/// At corpus scale <b>tokenisation decides most of what "unparsed" means</b>. A form the tokeniser invented by
/// splitting on an apostrophe fails to parse and looks like a gap in the grammar. Two corpora tokenised
/// differently are not comparable even when the source text is identical, so a figure without this recorded is
/// not reproducible — which is why it sits beside the origin rather than in a README.
/// </remarks>
public sealed record TokenisationRecord(string Method, string Version, string Notes);

/// <summary>
/// Somebody's signed claim that a corpus is fit to measure accuracy against. Absent by default and absent for
/// most corpora, which is the honest state rather than a defect.
/// </summary>
/// <param name="KnownClean">Somebody looked, and the words are words.</param>
/// <param name="InScope">
/// The grammar <b>should</b> analyse every token here. The strongest claim and the easiest to get wrong — a
/// corpus full of names and borrowings fails this while looking fine.
/// </param>
/// <param name="Attestor">Who is saying so. A person, named.</param>
/// <param name="AttestedUtc">When they said it.</param>
/// <param name="Note">Why they believe it — the part FieldWorks' own approval mechanism has nowhere to put.</param>
public sealed record CorpusQualification(
    bool KnownClean,
    bool InScope,
    string Attestor,
    DateTimeOffset AttestedUtc,
    string Note);

/// <summary>
/// Everything a corpus says about itself: where it came from, how it was tokenised, and what — if anything —
/// somebody is willing to claim about its fitness.
/// </summary>
/// <remarks>
/// <para>
/// Origin and tokenisation are <b>required</b>; qualification is optional and its absence is meaningful. The
/// three answer one reader question — <i>can I trust a number computed over this?</i> — and were folded into a
/// single record precisely because keeping them apart guarantees one gets filled in and the other does not.
/// </para>
/// <para>
/// <b>The asymmetry this enforces.</b> A large uncurated corpus — a Wikipedia pull — is excellent evidence of
/// <i>reach</i>: what fraction of real running text a grammar touches, and which unparsed forms are most
/// frequent, which is the best worklist there is for what to add next. It is worthless as evidence of
/// <i>correctness</i>, because a failed analysis there is ambiguous between a real gap, a typo, and a token the
/// grammar was never meant to cover — three causes demanding opposite responses. So such a corpus carries an
/// origin and no qualification, and <see cref="SupportsAccuracyClaims"/> says so out loud.
/// </para>
/// </remarks>
public sealed record CorpusProvenance(
    CorpusOrigin Origin,
    TokenisationRecord Tokenisation,
    CorpusQualification? Qualification = null)
{
    /// <summary>
    /// Whether an accuracy figure may be computed over this corpus at all.
    /// </summary>
    /// <remarks>
    /// When this is <c>false</c>, a report must say accuracy is <b>not computable, and why</b> — it must not
    /// print a number with an asterisk. "I could not look" must never read as "everything is fine", and a
    /// precision figure over an unvetted corpus is that failure in its most persuasive form, because it looks
    /// like evidence. Reach figures remain perfectly valid.
    /// </remarks>
    public bool SupportsAccuracyClaims =>
        Qualification is { KnownClean: true, InScope: true }
        && !string.IsNullOrWhiteSpace(Qualification.Attestor);

    /// <summary>The sentence a report prints instead of an accuracy number when it has none to print.</summary>
    public string WhyAccuracyIsNotComputable()
    {
        if (SupportsAccuracyClaims) return string.Empty;

        if (Qualification is null)
            return $"Accuracy is not computable for '{Origin.Description}': nobody has attested that this " +
                   "corpus is clean and in scope. Reach figures over it remain valid.";

        var failures = new List<string>();
        if (!Qualification.KnownClean) failures.Add("it is not attested clean");
        if (!Qualification.InScope) failures.Add("it is not attested in scope");
        if (string.IsNullOrWhiteSpace(Qualification.Attestor)) failures.Add("no attestor is named");

        return $"Accuracy is not computable for '{Origin.Description}': " +
               string.Join(", ", failures) + ". Reach figures over it remain valid.";
    }
}
