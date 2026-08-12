using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads FieldWorks' <c>DistFiles/Language Explorer/Configuration/ContextHelp.xml</c> — one flat list of
/// <c>&lt;item id="..."&gt;</c> balloon-help strings shown for dialog controls. Purely mechanical: every id
/// and its current text, no field-name judgement here at all. Which id names which (Class, Field) is a
/// separate, curated fact — see <see cref="FieldWorksContextHelpFieldMap"/> — because the id often does not
/// literally equal the field name (e.g. <c>NaturalClassAbbreviation</c> for <c>PhNaturalClass.Abbreviation</c>)
/// and a handful of ids are reused generically across several classes' dialogs, which a bare string match
/// would get wrong.
/// </summary>
/// <remarks>
/// <c>manifest/fieldworks-labels.tsv</c> already harvests <c>strings-en.xml</c>, the <c>.fwlayout</c> slice
/// system and tool/area config (ADR 0023 decision 5's amendment: "only 20 rows carry any prose"). This is a
/// fourth, previously-unharvested source, called out explicitly in this task because it is where those 20
/// prose rows actually live — <c>ContextHelp.xml</c> is a different file from anything
/// <c>SIL.Motif.Spikes.LabelHarvest</c> reads.
/// </remarks>
public static class FieldWorksContextHelpHarvester
{
    public static IReadOnlyDictionary<string, ContextHelpEntry> Harvest(string contextHelpXmlPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(contextHelpXmlPath);
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not read '{contextHelpXmlPath}': {ex.Message}", ex);
        }

        return HarvestText(contextHelpXmlPath, text);
    }

    /// <summary>Exposed for tests, which supply the XML content inline rather than on disk.</summary>
    public static IReadOnlyDictionary<string, ContextHelpEntry> HarvestText(string path, string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(new StringReader(xml), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new GeneratorException($"Could not parse '{path}': {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new GeneratorException($"'{path}' has no root element.");

        var result = new Dictionary<string, ContextHelpEntry>(StringComparer.Ordinal);
        foreach (var item in root.Elements("item"))
        {
            var id = (string?)item.Attribute("id");
            if (id is null) continue;
            if (result.ContainsKey(id)) continue; // first wins; duplicates would be an upstream data question, not ours

            var text = NormalizeWhitespace(item.Value);
            if (text.Length == 0) continue; // e.g. <item id="NoHelp" caption="No Help"></item>

            var lineInfo = (IXmlLineInfo)item;
            result[id] = new ContextHelpEntry(id, text, lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0);
        }

        return result;
    }

    private static string NormalizeWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();
}
