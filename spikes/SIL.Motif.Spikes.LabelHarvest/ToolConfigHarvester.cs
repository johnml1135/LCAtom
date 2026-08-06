using System.Xml.Linq;

namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Harvests mechanism 3: tool labels from <c>Lists/Edit/toolConfiguration.xml</c>, joined to their
/// <c>(className, ownerClass, ownerField)</c> key from <c>Lists/areaConfiguration.xml</c>. See
/// <c>docs/research/2026-08-05-fieldworks-user-facing-names.md</c> §1.3 — the tool's label is keyed by the
/// tool, which is in turn keyed by <c>(ownerClass, ownerField)</c>; <c>className</c> is often the generic
/// base class (<c>CmPossibility</c>), so these records are class-level, not field-level.
/// </summary>
public static class ToolConfigHarvester
{
    public static IReadOnlyList<RawLabel> Harvest(string toolConfigPath, string areaConfigPath)
    {
        var toolLabels = LoadToolLabels(toolConfigPath);
        var toolFileName = Path.GetFileName(toolConfigPath);
        var areaFileName = Path.GetFileName(areaConfigPath);

        var areaDoc = XDocument.Load(areaConfigPath);
        var results = new List<RawLabel>();

        foreach (var parameters in areaDoc.Descendants("parameters"))
        {
            var tool = (string?)parameters.Attribute("tool");
            var className = (string?)parameters.Attribute("className");
            var ownerClass = (string?)parameters.Attribute("ownerClass");
            var ownerField = (string?)parameters.Attribute("ownerField");
            if (string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(className)) continue;
            if (!toolLabels.TryGetValue(tool, out var label)) continue;

            results.Add(new RawLabel(className, "", label, "", "tool-config",
                $"{toolFileName} tool={tool} + {areaFileName} ownerClass={ownerClass} ownerField={ownerField}"));
        }

        return results;
    }

    /// <summary>
    /// Maps a <c>CmPossibilityList</c> owning-field name (e.g. <c>ConfidenceLevels</c>) to the class actually
    /// filed under it (e.g. <c>CmPossibility</c>), read from <c>areaConfiguration.xml</c>'s
    /// <c>ownerField -&gt; className</c> attributes directly — independent of any tool having a label.
    /// This is the resolution table <see cref="StringsEnHarvester.Harvest"/> needs to place
    /// <c>PossibilityListItemTypeNames</c> entries on the right class.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HarvestOwnerFieldToClass(string areaConfigPath)
    {
        var doc = XDocument.Load(areaConfigPath);
        var map = new Dictionary<string, string>();

        foreach (var parameters in doc.Descendants("parameters"))
        {
            var className = (string?)parameters.Attribute("className");
            var ownerField = (string?)parameters.Attribute("ownerField");
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(ownerField)) continue;

            // Keep the first mapping seen; a handful of owner fields (e.g. "Status") appear twice for two
            // different tools (senseStatusEdit and statusEdit) but always agree on className.
            map.TryAdd(ownerField, className);
        }

        return map;
    }

    private static Dictionary<string, string> LoadToolLabels(string toolConfigPath)
    {
        var doc = XDocument.Load(toolConfigPath);
        var map = new Dictionary<string, string>();

        foreach (var tool in doc.Descendants("tool"))
        {
            var value = (string?)tool.Attribute("value");
            var label = (string?)tool.Attribute("label");
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(label)) continue;

            map.TryAdd(value, label);
        }

        return map;
    }
}
