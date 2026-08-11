using System.Text.RegularExpressions;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// The <c>&lt;comment&gt;</c> found on one <c>&lt;basic|owning|rel&gt;</c> declaration in
/// <c>MasterLCModel.xml</c>, keyed by (<see cref="Class"/>, <see cref="Field"/>) the same way
/// <see cref="Model.ModelField"/> is.
/// </summary>
/// <param name="Class">The declaring class — the <c>&lt;class id=...&gt;</c> the field element sits inside.</param>
/// <param name="Field">The field name.</param>
/// <param name="ElementTag"><c>basic</c>, <c>owning</c>, or <c>rel</c> — cited so a reviewer can find the
/// exact element in the XML rather than search all three.</param>
/// <param name="FirstParagraph">
/// The first <c>&lt;para&gt;</c> under <c>&lt;comment&gt;</c>, whitespace-normalized. First, not "all
/// concatenated," because the worked examples in this model file are consistently in the first paragraph and
/// later paragraphs are usually editorial history ("Changed from X.") — concatenating everything would bury
/// the substance in renaming trivia (see <see cref="IsPlaceholderOnly"/>).
/// </param>
/// <param name="ParagraphCount">How many <c>&lt;para&gt;</c> elements the comment has in total.</param>
/// <param name="LineNumber">1-based line number of the field element in the source file, for citation.</param>
/// <param name="FullText">
/// Every paragraph, joined by a single space. Added for <see cref="Ordering.OrderingEvidenceHarvester"/>,
/// which is looking for a statement about ordering and cannot use <see cref="FirstParagraph"/>: these
/// comments routinely put the shape in the first paragraph and the ordering rule in a later one
/// (<c>MoInflAffixTemplate.PrefixSlots</c> is the pattern). Optional and defaulting to empty so every
/// existing call site — the description pipeline, and its tests' inline fixtures — keeps compiling and
/// behaving exactly as it did.
/// </param>
public sealed record LibLcmFieldComment(
    string Class,
    string Field,
    string ElementTag,
    string FirstParagraph,
    int ParagraphCount,
    int LineNumber,
    string FullText = "")
{
    private static readonly Regex PureRenameNote = new(@"^Changed from .+\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the only paragraph this field has says nothing about what the field *means* — either a bare
    /// editorial rename note ("Changed from RestOfAllomorphs.") or a literal author placeholder ("Put
    /// something here."). Measured against the 92 fields <c>manifest/kind-descriptions.tsv</c> already
    /// describes: <c>MoAlloAdhocProhib.RestOfAllos</c>, <c>MoMorphAdhocProhib.RestOfMorphs</c> and
    /// <c>PhCode.Representation</c> are exactly this case. A field whose <em>first</em> paragraph is
    /// substantive keeps that paragraph even if a later one is also just a rename note (e.g.
    /// <c>MoStemMsa.ProdRestrict</c>).
    /// </summary>
    public bool IsPlaceholderOnly =>
        ParagraphCount == 1 &&
        (PureRenameNote.IsMatch(FirstParagraph) ||
         string.Equals(FirstParagraph, "Put something here.", StringComparison.OrdinalIgnoreCase));

    /// <summary>Human-readable citation for the <c>SourceDetail</c> column.</summary>
    public string Citation => $"line {LineNumber}, <{ElementTag} id=\"{Field}\"> under <class id=\"{Class}\">";
}
