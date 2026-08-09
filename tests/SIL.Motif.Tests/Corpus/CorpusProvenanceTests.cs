using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// A corpus states where it came from, and whether anybody is prepared to vouch for it — and the second
/// decides which kinds of figure may be computed over it.
/// </summary>
/// <remarks>
/// The rule these tests defend: a large uncurated corpus is excellent evidence of **reach** and worthless as
/// evidence of **correctness**, because a failed analysis in it is ambiguous between a real grammar gap, a
/// typo, and a token the grammar was never meant to cover. Those demand opposite responses, so a single
/// accuracy number over such a corpus tells a project to work on whichever cause is loudest rather than
/// whichever matters. The code must refuse to produce that number rather than footnote it.
/// </remarks>
public class CorpusProvenanceTests
{
    private static CorpusOrigin Wikipedia() => new(
        Description: "Wikipedia, Sena edition",
        Uri: "https://seh.wikipedia.org/",
        RetrievedUtc: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Licence: "CC-BY-SA-4.0");

    private static TokenisationRecord Tokenisation() => new(
        Method: "whitespace-and-punctuation",
        Version: "1",
        Notes: "Splits on whitespace; strips leading/trailing punctuation; numerals dropped.");

    [Fact]
    public void AnUncuratedCorpusCarriesItsOrigin_ButCannotSupportAccuracy()
    {
        var provenance = new CorpusProvenance(Wikipedia(), Tokenisation());
        var corpus = CorpusDescriptor.Create("seh-wikipedia", new[] { "mbali", "ya" }, provenance);

        Assert.False(corpus.SupportsAccuracyClaims);

        // The refusal has to explain itself, or a reader concludes the tool is broken rather than the corpus
        // unvetted — and has to say what the corpus IS still good for.
        var why = provenance.WhyAccuracyIsNotComputable();
        Assert.Contains("Wikipedia", why);
        Assert.Contains("nobody has attested", why);
        Assert.Contains("Reach figures over it remain valid", why);
    }

    [Fact]
    public void AttestingOnlyHalfOfIt_StillDoesNotSupportAccuracy_AndNamesWhichHalfIsMissing()
    {
        // "Known clean" is the easy claim; "in scope" is the one the proposal calls easiest to get wrong,
        // because a corpus full of names and borrowings fails it while looking fine.
        var halfAttested = new CorpusProvenance(
            Wikipedia(), Tokenisation(),
            new CorpusQualification(
                KnownClean: true, InScope: false,
                Attestor: "A. Linguist", AttestedUtc: DateTimeOffset.UtcNow,
                Note: "Spot-checked for typos; makes no claim about names or borrowings."));

        Assert.False(halfAttested.SupportsAccuracyClaims);
        Assert.Contains("not attested in scope", halfAttested.WhyAccuracyIsNotComputable());
        Assert.DoesNotContain("not attested clean", halfAttested.WhyAccuracyIsNotComputable());
    }

    [Fact]
    public void AnAttestationWithNoNamedAttestor_DoesNotCount()
    {
        // The signature is the point. An unsigned claim that a corpus is fit is not evidence, it is a setting.
        var unsigned = new CorpusProvenance(
            Wikipedia(), Tokenisation(),
            new CorpusQualification(true, true, Attestor: "  ", DateTimeOffset.UtcNow, Note: "looks fine"));

        Assert.False(unsigned.SupportsAccuracyClaims);
        Assert.Contains("no attestor is named", unsigned.WhyAccuracyIsNotComputable());
    }

    [Fact]
    public void AFullyAttestedCorpus_SupportsAccuracy_AndHasNothingToExplain()
    {
        var attested = new CorpusProvenance(
            new CorpusOrigin("Curated verb paradigm list", null, DateTimeOffset.UtcNow, null),
            Tokenisation(),
            new CorpusQualification(
                KnownClean: true, InScope: true,
                Attestor: "A. Linguist", AttestedUtc: DateTimeOffset.UtcNow,
                Note: "Every form checked against the paradigm; no names or loanwords included."));

        Assert.True(attested.SupportsAccuracyClaims);
        Assert.Equal(string.Empty, attested.WhyAccuracyIsNotComputable());
    }

    [Fact]
    public void ProvenanceIsOutsideTheHash_SoAttestingACorpusDoesNotMakeItADifferentCorpus()
    {
        // Attesting changes what may be claimed about the words, not which words they are. If it moved the
        // hash, signing a corpus would invalidate every figure previously computed over exactly those words.
        var words = new[] { "mbali", "ya", "miseru" };
        var bare = CorpusDescriptor.Create("seh", words);
        var attested = CorpusDescriptor.Create("seh", words,
            new CorpusProvenance(Wikipedia(), Tokenisation(),
                new CorpusQualification(true, true, "A. Linguist", DateTimeOffset.UtcNow, "checked")));

        Assert.Equal(bare.Sha256, attested.Sha256);
        Assert.False(bare.SupportsAccuracyClaims);
        Assert.True(attested.SupportsAccuracyClaims);
    }
}
