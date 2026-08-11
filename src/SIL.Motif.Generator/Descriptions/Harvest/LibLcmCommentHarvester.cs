using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Reads the same <c>MasterLCModel.xml</c> <see cref="Model.MasterLcModelParser"/> reads, but for a different
/// purpose: pulling the <c>&lt;comment&gt;</c> prose off each field declaration rather than its
/// class/sig/card shape. Kept separate from <see cref="Model.MasterLcModelParser"/> deliberately — that
/// parser feeds the operation-shape derivations (ADR 0022) and touching its output shape for a
/// documentation concern would widen its blast radius for no reason. This one feeds
/// <see cref="KindDescriptionRefresher"/> and <see cref="Ordering.OrderingEvidenceHarvester"/> — both
/// documentation-shaped consumers of the same prose.
/// </summary>
/// <remarks>
/// The generator already parses this file for its class/sig/card shape; this reads the same file for the
/// prose it otherwise ignores.
/// </remarks>
public static class LibLcmCommentHarvester
{
    public static IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> Harvest(string masterLcModelXmlPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(masterLcModelXmlPath);
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not read '{masterLcModelXmlPath}': {ex.Message}", ex);
        }

        return HarvestText(masterLcModelXmlPath, text);
    }

    /// <summary>Exposed for tests, which supply the XML content inline rather than on disk — the same
    /// convention <see cref="KindDescriptionTsvParser.ParseText"/> uses.</summary>
    public static IReadOnlyDictionary<(string Class, string Field), LibLcmFieldComment> HarvestText(string path, string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(new StringReader(xml), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new GeneratorException($"Could not parse '{path}' as MasterLCModel.xml: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new GeneratorException($"'{path}' has no root element.");

        var result = new Dictionary<(string, string), LibLcmFieldComment>();

        foreach (var classElement in root.Descendants("class"))
        {
            var classId = (string?)classElement.Attribute("id")
                ?? throw new GeneratorException($"'{path}': a <class> element has no id attribute.");

            foreach (var tag in FieldTags)
            {
                foreach (var element in classElement.Descendants(tag))
                {
                    var fieldId = (string?)element.Attribute("id");
                    if (fieldId is null) continue; // malformed, but comment-harvesting is best-effort, not the schema check

                    var key = (classId, fieldId);
                    if (result.ContainsKey(key)) continue; // first declaration wins, same as the model's own lookup order

                    var commentElement = element.Element("comment");
                    if (commentElement is null) continue; // self-closing or comment-less field — nothing to harvest

                    var paragraphs = commentElement.Elements("para")
                        .Select(p => NormalizeWhitespace(p.Value))
                        .Where(p => p.Length > 0)
                        .ToList();
                    if (paragraphs.Count == 0) continue;

                    var lineInfo = (IXmlLineInfo)element;
                    var lineNumber = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;

                    result[key] = new LibLcmFieldComment(
                        classId, fieldId, tag, paragraphs[0], paragraphs.Count, lineNumber,
                        string.Join(" ", paragraphs));
                }
            }
        }

        return result;
    }

    private static readonly string[] FieldTags = ["basic", "owning", "rel"];

    /// <summary>Collapses embedded newlines/tabs/runs of spaces to single spaces, since the harvested text becomes one TSV cell.</summary>
    private static string NormalizeWhitespace(string value) => Regex.Replace(value, @"\s+", " ").Trim();
}
