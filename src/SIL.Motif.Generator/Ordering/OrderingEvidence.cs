namespace SIL.Motif.Generator.Ordering;

/// <summary>
/// What the model itself says about whether order matters for one field whose <c>ComparisonClass</c> is not
/// <c>unordered</c> — quoted, cited, and digested, so the claim can be re-checked rather than trusted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evidence, not authority.</b> <c>ComparisonClass</c> stays derived from <c>Card</c> with a closed
/// exception table ([ADR 0022](../../../docs/adr/0022-structure-is-derived-policy-is-five-rows.md) decision
/// 2); nothing here feeds that derivation. What this answers is the question the derivation cannot:
/// <em>is the rule right for this particular row?</em> The manifest trust audit found that it is
/// not always — <c>MoAlloAdhocProhib.Allomorphs</c> is <c>positional</c> by the rule, but the model says
/// order matters only when a <em>sibling</em> field is set — and that 63 of the 64 non-<c>unordered</c> rows
/// carried no citation at all.
/// </para>
/// <para>
/// <b>Why the drift digest matters here specifically.</b> An ordering statement is the kind of sentence that
/// gets edited without ceremony ("the order is from the innermost affix out" gaining or losing a "not"), and
/// a wrong <c>ComparisonClass</c> does not fail anything loudly: it makes the mechanical diff report a
/// reordering as meaningful, or miss one that was. The failure is silent in both directions, which is
/// exactly the case for pinning the sentence rather than remembering it.
/// </para>
/// </remarks>
/// <param name="Statement">
/// The sentences of the field's <c>MasterLCModel.xml</c> comment that speak to ordering, or empty when the
/// comment says nothing about it (or when there is no comment at all). Empty is a real, reportable answer —
/// it means the <c>positional</c> classification rests on <c>card=seq</c> alone.
/// </param>
/// <param name="MatchedTerms">
/// Which ordering words <see cref="OrderingVocabulary"/> matched, so the selection is auditable rather than
/// magic. A reviewer who disagrees with an inclusion can see precisely why it was included.
/// </param>
/// <param name="SourceHash">
/// <see cref="Descriptions.Harvest.SourceDigest"/> of <see cref="Statement"/>. Empty when there is none.
/// </param>
public sealed record OrderingEvidence(
    string Class,
    string Field,
    string Card,
    string ComparisonClass,
    string Statement,
    string MatchedTerms,
    string Source,
    string SourceDetail,
    string SourceHash)
{
    public string Key => $"{Class}.{Field}";

    /// <summary>Whether the model says anything at all about order for this field.</summary>
    public bool HasStatement => Statement.Length > 0;
}
