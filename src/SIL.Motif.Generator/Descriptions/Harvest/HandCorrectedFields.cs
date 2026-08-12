namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// The <c>ProdRestrict</c>-exception-feature family: all five fields whose description once modelled an
/// exception feature as something that <i>blocks</i> an affix, when <c>MasterLCModel.xml</c> says the stem
/// must <i>bear</i> the class for the affix to attach. Corrected by hand, and preserved verbatim by
/// <see cref="KindDescriptionRefresher"/> — regeneration must never silently reintroduce the inversion by
/// overwriting the fix with a fresh mechanical paraphrase of the same source text.
/// </summary>
public static class HandCorrectedFields
{
    public static readonly IReadOnlySet<(string Class, string Field)> ProdRestrictFamily = new HashSet<(string, string)>
    {
        ("MoStemMsa", "ProdRestrict"),
        ("MoCompoundRule", "ToProdRestrict"),
        ("MoDerivAffMsa", "FromProdRestrict"),
        ("MoDerivAffMsa", "ToProdRestrict"),
        ("MoInflAffMsa", "FromProdRestrict"),
    };
}
