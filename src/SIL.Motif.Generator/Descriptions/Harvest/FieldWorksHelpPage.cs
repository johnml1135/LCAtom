namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// One <c>User_Interface/Field_Descriptions/...</c> page out of FieldWorks' compiled help
/// (<c>DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm</c>): the field's own help topic, whose
/// <c>Description:</c> row is the sentence FieldWorks itself shows a linguist asking what a field is for.
/// </summary>
/// <param name="RelativePath">Path inside the decompiled help tree, e.g.
/// <c>User_Interface/Field_Descriptions/Lists/Feature_Types_fields/abbreviation_field_feature_types.htm</c>.
/// This is what <c>HelpTopicPaths.resx</c> stores, so it is the citation a reader can follow.</param>
/// <param name="Title">The page's <c>&lt;title&gt;</c>, e.g. <c>Abbreviation field (Feature Types)</c> —
/// carried so a reviewer can check the page really is about the field it was mapped to, without opening
/// the help file.</param>
/// <param name="Description">
/// The first paragraph of the page's <c>Description:</c> row. First paragraph only, matching
/// <see cref="LibLcmFieldComment.FirstParagraph"/>'s convention: these pages continue into UI-configuration
/// advice ("Feature type abbreviations in the Grammar Sketch under Feature System") that belongs to the
/// application, not to the field.
/// </param>
public sealed record FieldWorksHelpPage(string RelativePath, string Title, string Description)
{
    public string Citation => $"{RelativePath}, Description row (page title: \"{Title}\")";
}
