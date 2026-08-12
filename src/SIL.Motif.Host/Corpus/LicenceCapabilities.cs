using System;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// What the licence on a body of text actually permits, separated from the licence's name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not just the licence string.</b> <see cref="CorpusOrigin.Licence"/> records the licence
/// verbatim, which is right for the record and useless for a decision: no part of Motif should be parsing
/// <c>"CC-BY-NC-ND-4.0"</c> at the point where it is about to build an n-gram model. This record is the
/// decision-shaped form of the same fact, resolved once at ingestion.
/// </para>
/// <para>
/// <b>Why it matters here specifically.</b> The largest source of text for the languages this project serves
/// is eBible, and roughly 805 of its ~1,004 translations are <b>No-Derivatives</b>. Measuring how much of a
/// corpus a grammar reaches is reading, and reading is fine. Building a spelling-correction or
/// word-prediction model from that same text produces a <i>derived work</i>, and for most of eBible that is
/// not permitted. Those two uses run over identical bytes, so nothing about the data distinguishes them —
/// only this record does.
/// </para>
/// <para>
/// <b>Unknown is not permission.</b> Every flag is a nullable three-state: yes, no, or nobody has established
/// it. <see cref="MayDerive"/> being <c>null</c> blocks derivation exactly as <c>false</c> does, and the two
/// give different explanations. This mirrors <see cref="CorpusProvenance.SupportsAccuracyClaims"/>: the record
/// states what is known; consuming code must refuse rather than assume, so "I could not establish it" never
/// reads as "it is fine".
/// </para>
/// <para>
/// <b>This is a recorded claim, not legal advice.</b> Motif does not interpret licences; whoever ingests the
/// corpus states what they were told, and <see cref="Basis"/> says who told them.
/// </para>
/// </remarks>
/// <param name="MayRedistribute">Whether the text itself may be passed on to someone else.</param>
/// <param name="MayDerive">
/// Whether works derived from the text — n-gram models, spell-checkers, word lists published as such — are
/// permitted. The flag that eBible's No-Derivatives majority turns off.
/// </param>
/// <param name="MayUseCommercially">Whether the use may be commercial.</param>
/// <param name="RequiresAttribution">
/// Whether published output must credit the source. Not a gate — it never blocks anything — but an obligation
/// that has to survive from ingestion to publication or it cannot be honoured.
/// </param>
/// <param name="Basis">
/// Where these answers came from: <c>"eBible licences.tsv"</c>, <c>"SIL holds copyright"</c>, a person's name.
/// Required, because an unsourced permission claim is the one kind that is worse than no claim.
/// </param>
public sealed record LicenceCapabilities(
    bool? MayRedistribute,
    bool? MayDerive,
    bool? MayUseCommercially,
    bool RequiresAttribution,
    string Basis)
{
    /// <summary>Nothing has been established. The correct state for a corpus whose licence nobody checked.</summary>
    public static LicenceCapabilities Unknown(string basis = "not established") =>
        new(null, null, null, RequiresAttribution: true, Basis: basis);

    /// <summary>Whether a derived artefact — an n-gram model, a published word list — may be built from this.</summary>
    public bool PermitsDerivedArtefacts => MayDerive is true;

    /// <summary>
    /// The sentence to show instead of building a derived artefact, when one may not be built.
    /// </summary>
    /// <remarks>
    /// Names the corpus and the basis, because "you may not" without "according to what" is an error message a
    /// person cannot act on — they cannot tell whether to go and find the licence or to stop.
    /// </remarks>
    public string WhyDerivedArtefactsAreNotPermitted(string corpusDescription)
    {
        if (PermitsDerivedArtefacts) return string.Empty;

        return MayDerive is false
            ? $"No derived work may be built from '{corpusDescription}': its licence forbids derivatives " +
              $"(basis: {Basis}). Reach and grammar coverage figures over it remain permitted, because reading is not " +
              "deriving."
            : $"No derived work may be built from '{corpusDescription}': nobody has established what its " +
              $"licence permits (basis: {Basis}). Establish the licence, or use a corpus that has one. Reach " +
              "and grammar coverage figures over it remain permitted, because reading is not deriving.";
    }

    /// <summary>Throws unless a derived artefact may be built. For the call sites that must not proceed.</summary>
    public void DemandDerivationIsPermitted(string corpusDescription)
    {
        if (!PermitsDerivedArtefacts)
            throw new InvalidOperationException(WhyDerivedArtefactsAreNotPermitted(corpusDescription));
    }
}
