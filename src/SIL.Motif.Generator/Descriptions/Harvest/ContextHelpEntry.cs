namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>One <c>&lt;item id="..."&gt;text&lt;/item&gt;</c> from FieldWorks'
/// <c>DistFiles/Language Explorer/Configuration/ContextHelp.xml</c> — the balloon-help text shown for a
/// dialog control, keyed by the id FieldWorks' own help-lookup code uses.</summary>
public sealed record ContextHelpEntry(string Id, string Text, int LineNumber)
{
    public string Citation => $"line {LineNumber}, item id=\"{Id}\"";
}
