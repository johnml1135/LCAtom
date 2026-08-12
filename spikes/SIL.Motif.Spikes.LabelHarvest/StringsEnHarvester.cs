using System.Xml.Linq;

namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Harvests mechanism 1 from <c>strings-en.xml</c>: class names and possibility-list "kind" names. That
/// file carries no per-field group at all, so every record from here is class-level
/// (<see cref="RawLabel.Field"/> is empty).
/// </summary>
public static class StringsEnHarvester
{
    /// <param name="path">Path to <c>strings-en.xml</c>.</param>
    /// <param name="ownerFieldToClass">Maps a <c>CmPossibilityList</c> owning-field name (e.g.
    /// <c>ConfidenceLevels</c>) to the runtime class filed under it (e.g. <c>CmPossibility</c>), as resolved
    /// from <c>Lists/areaConfiguration.xml</c> (<see cref="ToolConfigHarvester.HarvestOwnerFieldToClass"/>).
    /// Entries in the <c>PossibilityListItemTypeNames</c> group whose key is not in this map are dropped
    /// rather than guessed, because which class actually owns that list is a runtime fact this file alone
    /// does not carry (ADR 0023, decision 1's evidence).</param>
    /// <param name="knownClasses">The set of class names known to the manifest, used to reject
    /// <c>AlternativeTitles</c> entries whose id prefix is a UI concept (e.g. <c>Concordance</c>) rather than
    /// an LCM class.</param>
    public static IReadOnlyList<RawLabel> Harvest(
        string path,
        IReadOnlyDictionary<string, string> ownerFieldToClass,
        IReadOnlySet<string> knownClasses)
    {
        var doc = XDocument.Load(path);
        var fileName = Path.GetFileName(path);
        var results = new List<RawLabel>();

        var root = doc.Root ?? throw new InvalidOperationException($"{path}: no root element");

        // ClassNames: <string id="ClassId" txt="Label"/> — one label per class, no field.
        var classNames = root.Elements("group").FirstOrDefault(g => (string?)g.Attribute("id") == "ClassNames");
        if (classNames is not null)
        {
            foreach (var s in classNames.Elements("string"))
            {
                var id = (string?)s.Attribute("id");
                var txt = (string?)s.Attribute("txt");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(txt)) continue;

                results.Add(new RawLabel(id, "", txt, "", "strings-en",
                    $"{fileName}, group=ClassNames, id={id}"));
            }
        }

        // Keyed by owning field, not class: resolve via areaConfiguration.xml, and drop what will not resolve.
        var listItemTypeNames = root.Elements("group").FirstOrDefault(g => (string?)g.Attribute("id") == "PossibilityListItemTypeNames");
        if (listItemTypeNames is not null)
        {
            foreach (var s in listItemTypeNames.Elements("string"))
            {
                var ownerField = (string?)s.Attribute("id");
                var txt = (string?)s.Attribute("txt");
                if (string.IsNullOrEmpty(ownerField) || string.IsNullOrEmpty(txt)) continue;
                if (!ownerFieldToClass.TryGetValue(ownerField, out var cls)) continue;

                results.Add(new RawLabel(cls, "", txt, "", "strings-en",
                    $"{fileName}, group=PossibilityListItemTypeNames, ownerField={ownerField} -> {cls}"));
            }
        }

        // Keep only ids prefixed by a known class, so UI concepts like "Concordance" cannot become classes.
        var alternativeTitles = root.Elements("group").FirstOrDefault(g => (string?)g.Attribute("id") == "AlternativeTitles");
        if (alternativeTitles is not null)
        {
            foreach (var s in alternativeTitles.Elements("string"))
            {
                var id = (string?)s.Attribute("id");
                var txt = (string?)s.Attribute("txt");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(txt)) continue;

                var dash = id.IndexOf('-');
                if (dash <= 0) continue;
                var prefix = id[..dash];
                if (!knownClasses.Contains(prefix)) continue;

                results.Add(new RawLabel(prefix, "", txt, "", "strings-en",
                    $"{fileName}, group=AlternativeTitles, id={id}"));
            }
        }

        return results;
    }
}
