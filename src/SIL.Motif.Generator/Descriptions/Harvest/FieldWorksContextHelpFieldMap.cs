namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// The curated, hand-verified fact <see cref="FieldWorksContextHelpHarvester"/> cannot derive on its own:
/// which <c>ContextHelp.xml</c> <c>id</c> names which (Class, Field). Re-running the harvester always re-pulls
/// whatever text currently sits at that id — only this mapping is a human judgement, checked in so it does
/// not have to be re-derived by hand on every regeneration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a curated map instead of assuming <c>id == Field</c>:</b> most ids do equal the field name
/// (<c>FirstAllomorph</c>, <c>RestOfMorphs</c>, ...), but several are qualified per class
/// (<c>NaturalClassAbbreviation</c>, <c>InflClassAbbreviation</c>, <c>CategoryAbbreviation</c> all mean
/// "Abbreviation" on three different classes) and some ids are shared across multiple classes' dialogs for the
/// same generic control (a bare <c>PartOfSpeech</c> id is reused for at least
/// <c>MoStemMsa.PartOfSpeech</c>/<c>MoDerivAffMsa.FromPartOfSpeech</c>/<c>MoUnclassifiedAffixMsa.PartOfSpeech</c>
/// and reads as stem-specific wording — "The category of this stem or root" — that would misdescribe an
/// affix). Assuming id-equals-field would import that ambiguity silently, exactly the failure D8 exists to
/// stop. Every entry below was checked against a concrete <c>field="..."</c> slice binding in
/// <c>FieldWorks/DistFiles/Language Explorer/Configuration/Parts/*.xml</c> (see <see cref="Entry.VerifiedAgainst"/>),
/// except the one flagged <c>ambiguous-wiring</c>, which is included with that caveat spelled out rather than
/// silently promoted to <c>exact</c>.
/// </para>
/// </remarks>
public static class FieldWorksContextHelpFieldMap
{
    public sealed record Entry(string ContextHelpId, string Class, string Field, string Confidence, string VerifiedAgainst);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new Entry("FirstAllomorph", "MoAlloAdhocProhib", "FirstAllomorph", "exact",
            "MorphologyParts.xml:2313, slice field=\"FirstAllomorph\" class=\"...AdhocCoProhibAtomicReferenceSlice\""),
        new Entry("RestOfAllos", "MoAlloAdhocProhib", "RestOfAllos", "exact",
            "ContextHelp.xml's own text (\"the other allomorph(s) which cannot occur with the key allomorph\") " +
            "names the same pairing as MorphologyParts.xml's FirstAllomorph/RestOfAllos slices in the same dialog"),
        new Entry("FirstMorpheme", "MoMorphAdhocProhib", "FirstMorpheme", "exact",
            "parallel construction to FirstAllomorph, for the morpheme-level rather than allomorph-level " +
            "co-occurrence restriction"),
        new Entry("RestOfMorphs", "MoMorphAdhocProhib", "RestOfMorphs", "exact",
            "parallel construction to RestOfAllos"),
        new Entry("MorphoSyntaxAnalysis", "LexSense", "MorphoSyntaxAnalysis", "exact",
            "LexSenseParts.xml:44, obj field=\"MorphoSyntaxAnalysis\" (class LexSense is the only declarer of " +
            "this field name in the model)"),
        new Entry("Representation", "PhCode", "Representation", "exact",
            "MorphologyParts.xml:2979, slice field=\"Representation\" label=\"Grapheme\" — label matches the " +
            "already-harvested fieldworks-labels.tsv row for PhCode.Representation exactly"),
        new Entry("NaturalClassAbbreviation", "PhNaturalClass", "Abbreviation", "ambiguous-wiring",
            "translated into 5 locales (Localizations/l10ns/*/strings-*.xml), so the string is almost certainly " +
            "live, but no direct field=\"Abbreviation\" slice with this help id nor a C# HelpKeyword literal " +
            "was found by static search — the runtime binding could not be confirmed, only the content's " +
            "unambiguous subject (\"the abbreviation for this natural class\")"),
    ];
}
