namespace SIL.Motif.Generator.Descriptions;

/// <summary>
/// One row of <c>manifest/kind-descriptions.tsv</c>: the sentence explaining *when an agent should
/// reach for* the operation on a given field, plus where that sentence came from.
/// </summary>
/// <param name="Class">Declaring LibLCM class.</param>
/// <param name="Field">Field name.</param>
/// <param name="Label">
/// The short human name, normally copied from <c>manifest/fieldworks-labels.tsv</c>. Present here only so the
/// "a description must not merely restate its label" bar can be checked without loading the harvest.
/// </param>
/// <param name="Description">
/// The sentence. Never hashed, so it is free to improve forever. Copied or closely adapted from
/// <see cref="Source"/> when one exists rather than paraphrased freely — a paraphrase is exactly how a
/// polarity bug (an exception feature described as blocking an affix, when the model says the stem must
/// bear it) got through review undetected.
/// </param>
/// <param name="Reviewed">
/// One of four values (documented in manifest/README.md):
/// <list type="bullet">
/// <item><c>sourced</c> — copied or closely adapted from a cited upstream source, not yet reviewed by a
/// linguist.</item>
/// <item><c>hand-corrected</c> — manually verified and, where necessary, corrected against a cited source
/// after a prior automated error; the regenerator preserves this text verbatim rather than overwriting it.</item>
/// <item><c>unsourced</c> — no upstream source has been found yet. The description is an unverified claim,
/// per ADR 0023's amendment: "draft should mean nobody has checked this against a source yet."</item>
/// <item><c>no-source-exists</c> — searched for exhaustively and found nowhere, with the search itself
/// cited in <see cref="SourceDetail"/> (<see cref="Harvest.DescriptionExemptions"/>). Distinct from
/// <c>unsourced</c>, which is an open question rather than an answered one.</item>
/// </list>
/// Tracked rather than gating emission: a wrong sentence is a documentation bug, not a data-corruption risk,
/// so an <c>unsourced</c> row is still usable while its source is tracked down.
/// </param>
/// <param name="Source">
/// Which upstream artifact the description came from — e.g. <c>liblcm/MasterLCModel.xml</c>,
/// <c>FieldWorks/ContextHelp.xml</c>, or the compiled help file — or empty when <see cref="Reviewed"/> is
/// <c>unsourced</c>. An exempt row carries the literal <c>none (searched)</c>, because the honest citation
/// for "nothing exists" is of the search.
/// </param>
/// <param name="SourceDetail">
/// Where in <see cref="Source"/> — a line number and element, e.g. <c>line 2555, rel id="ProdRestrict" under
/// class id="MoStemMsa"</c> — so a reviewer can go look at the exact citation rather than trusting the
/// paraphrase. Required whenever <see cref="Source"/> is non-empty.
/// </param>
/// <param name="SourceHash">
/// <see cref="Harvest.SourceDigest"/> of the exact upstream fragment this row was taken from — not of
/// <see cref="Description"/>, and not of the whole source file. Empty when there is no source.
/// <para>
/// For a <c>sourced</c> row the two are the same text, so the digest is merely convenient. It earns its
/// place on the rows whose text deliberately differs from their source: a <c>hand-corrected</c> row keeps a
/// human's fix, and an <c>adapted</c> row is derived from a sibling — so comparing <em>our</em> prose
/// against upstream can never detect that upstream moved. The digest can, and does.
/// </para>
/// </param>
public sealed record KindDescription(
    string Class,
    string Field,
    string Label,
    string Description,
    string Reviewed,
    string Source = "",
    string SourceDetail = "",
    string SourceHash = "")
{
    /// <summary>The manifest join key, matching <see cref="Join.FieldKey"/>'s shape.</summary>
    public string Key => $"{Class}.{Field}";
}
