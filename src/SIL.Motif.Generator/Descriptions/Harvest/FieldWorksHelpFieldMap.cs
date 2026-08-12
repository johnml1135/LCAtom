namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// The curated, hand-verified fact <see cref="FieldWorksHelpHarvester"/> cannot derive: which compiled-help
/// page describes which (Class, Field). The same shape and the same discipline as
/// <see cref="FieldWorksContextHelpFieldMap"/> — re-running the harvest always re-reads whatever text now
/// sits on the page; only the mapping is a human judgement, checked in so it is not re-derived by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the mapping comes from.</b> FieldWorks itself keeps one:
/// <c>Src/LexText/LexTextDll/HelpTopicPaths.resx</c> maps a <c>khtpField-{Class}-{Field}</c> key to a page
/// under <c>User_Interface/Field_Descriptions/</c>. That resx is the index used here — not a filename
/// guessed from a field name — and each entry below records the key it came from, or says plainly that it
/// was found by search because the resx has no key for that field.
/// </para>
/// <para>
/// <b>Every entry was checked against the field's harvested label</b>
/// (<c>manifest/fieldworks-labels.tsv</c>) before being trusted, because a resx key's name and a model
/// field's name are not the same namespace: <c>khtpField-SourceForm</c> carries no class at all, and
/// <c>khtpField-ReversalIndexEntry-ReferringSenses</c> names a field the model calls <c>Senses</c>. In both
/// cases the page's own title matches the field's label exactly ("Source Form", "Referenced Senses"), which
/// is the evidence recorded in <see cref="Entry.VerifiedAgainst"/>.
/// </para>
/// <para>
/// <b>One field the resx points at was deliberately not mapped.</b>
/// <c>khtpField-lexiconEdit-MoInflAffMsa-CategoryInfo</c> resolves to <c>Category_Info_field.htm</c>. The
/// page describes a prose summary area whose harvested label is <c>Category Info.</c> — which is
/// <c>MoInflAffMsa.MainEdit</c>, not <c>MoInflAffMsa.PartOfSpeech</c>. <c>PartOfSpeech</c>'s own label is
/// <c>Category</c>, and no page describes it. It stays unsourced, which is the correct outcome: a page that
/// does not describe the field is not a source for it.
/// </para>
/// </remarks>
public static class FieldWorksHelpFieldMap
{
    /// <param name="RelativePath">Path inside the decompiled help tree.</param>
    /// <param name="Confidence">
    /// <c>exact</c> — the resx names this class and field, and the page title matches the harvested label.
    /// <c>renamed-path</c> — the resx key is right but its path is stale against the shipped help tree, so
    /// the page was located by title instead; the citation records both paths.
    /// <c>found-by-search</c> — the resx has no key for this field; the page was found by searching the
    /// field-description tree and matched on subject.
    /// <c>class-generic</c> — the field is a base-class field FieldWorks documents once per descendant list
    /// rather than once; the page cited is the most generic of those, and the citation says so.
    /// </param>
    public sealed record Entry(
        string Class, string Field, string RelativePath, string Confidence, string VerifiedAgainst);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new Entry("CmPossibility", "Abbreviation",
            "User_Interface/Field_Descriptions/Lists/Text_Chart_Temp_fields/abbreviation_field_tctemp.htm",
            "class-generic",
            "HelpTopicPaths.resx has 21 per-list keys for this one field (ProdRestrictEdit, confidenceEdit, " +
            "statusEdit, genresEdit, ...) and no canonical one, because the field is CmPossibility's, shared " +
            "by 13 descendant lists (ADR 0023 decision 2/4). All 38 Abbreviation pages under " +
            "Field_Descriptions were read: every one says \"the abbreviation of the name of the current " +
            "<this list's item>\", and this page is the one whose wording carries no list-specific noun"),

        new Entry("FsFeatDefn", "Abbreviation",
            "User_Interface/Field_Descriptions/Grammar/Inflection_Features_fields/abbreviation_field_feature_features.htm",
            "found-by-search",
            "no HelpTopicPaths.resx key exists for this class; page title \"Abbreviation field (Features)\" " +
            "and its own subheading \"Abbreviation field - Inflection Feature\" name the feature-definition " +
            "field, matching the harvested label \"Abbreviation\" on the MorphologyParts.xml slice " +
            "FsFeatDefn-Detail-AbbreviationAllA. The parallel Phonological_Features page says the same " +
            "thing for the same class in the phonology tool"),

        new Entry("FsFeatStrucType", "Abbreviation",
            "User_Interface/Field_Descriptions/Lists/Feature_Types_fields/abbreviation_field_feature_types.htm",
            "exact",
            "HelpTopicPaths.resx:680, khtpField-FsFeatStrucType-Abbreviation; page title \"Abbreviation " +
            "field (Feature Types)\" and the Feature Types list is FsFeatStrucType"),

        new Entry("FsSymFeatVal", "Abbreviation",
            "User_Interface/Field_Descriptions/Grammar/Inflection_Features_fields/Abbr_field_value_features.htm",
            "renamed-path",
            "HelpTopicPaths.resx:671, khtpField-FsSymFeatVal-Abbreviation, points at " +
            "Grammar/Features_fields/abbreviation_field_value_features.htm, which the shipped help tree no " +
            "longer contains — the directory is now Inflection_Features_fields and the file Abbr_field_" +
            "value_features.htm. Matched on title, \"Abbreviation field (Value - Features)\", whose text " +
            "(\"stores the abbreviation of one value name\") is the symbolic feature value's abbreviation"),

        new Entry("LexEtymology", "Form",
            "User_Interface/Field_Descriptions/Lexicon/Lexicon_Edit_fields/Entry_level_fields/Source_Form_Etymology.htm",
            "exact",
            "HelpTopicPaths.resx:440, khtpField-SourceForm (not class-qualified); page full name \"Source " +
            "Form\" matches this field's harvested label exactly, and the page places the field in the " +
            "Etymology field set"),

        new Entry("LexEtymology", "Gloss",
            "User_Interface/Field_Descriptions/Lexicon/Lexicon_Edit_fields/Entry_level_fields/Gloss_field_Etymology.htm",
            "exact",
            "HelpTopicPaths.resx:314, khtpField-LexEtymology-Gloss; the page's own text ties it to the " +
            "Source Form field, which is LexEtymology.Form"),

        new Entry("MoInflAffMsa", "Slots",
            "User_Interface/Field_Descriptions/Lexicon/Lexicon_Edit_fields/Grammatical_Info_Details_fields/Slots_field.htm",
            "exact",
            "HelpTopicPaths.resx:488, khtpField-MoInflAffMsa-Slots; page full name \"Slots\" matches the " +
            "harvested label, and the page describes the Grammatical Info Details level where this class's " +
            "slices live"),

        new Entry("ReversalIndexEntry", "PartOfSpeech",
            "User_Interface/Field_Descriptions/Lexicon/Reversal_Indexes_fields/category_field.htm",
            "exact",
            "HelpTopicPaths.resx:500, khtpField-ReversalIndexEntry-PartOfSpeech; page full name \"Reversal " +
            "Category\" matches this field's harvested label exactly"),

        new Entry("ReversalIndexEntry", "Senses",
            "User_Interface/Field_Descriptions/Lexicon/Reversal_Indexes_fields/senses_field.htm",
            "exact",
            "HelpTopicPaths.resx:506, khtpField-ReversalIndexEntry-ReferringSenses — a key name the model " +
            "does not use. Confirmed by label rather than by name: the page's full name is \"Referenced " +
            "Senses\", which is exactly this field's harvested label (ReversalParts.xml, part " +
            "ReversalIndexEntry-Detail-CurrentSenses)"),
    ];
}
