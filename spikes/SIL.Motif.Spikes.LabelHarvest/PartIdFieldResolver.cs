using System.Xml.Linq;

namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// A <c>.fwlayout</c> <c>&lt;part ref="X"&gt;</c> does not always name a field directly — it often names a
/// composite <c>Parts/*.xml</c> part definition instead, whose id embeds the real field plus a
/// writing-system-scope suffix (e.g. <c>MoInflAffixSlot-Detail-NameAllA</c> wraps
/// <c>&lt;slice field="Name"&gt;</c>; the fwlayout ref is <c>NameAllA</c>, not <c>Name</c>). Confirmed by
/// direct inspection while building this harvester — ADR 0023's own "Name" vs "Slot Name" example on
/// MoInflAffixSlot only round-trips to the real field once this indirection is resolved; without it,
/// <c>NameAllA</c> is recorded as a synthetic pseudo-field distinct from <c>Name</c>, silently hiding the
/// exact per-view conflict the ADR describes.
/// <para>
/// Every part id sampled across the tree has exactly three hyphen-delimited segments:
/// <c>ClassName-TypeSegment-Suffix</c> (1,210 ids checked, all exactly 3 segments). The type segment
/// (<c>Detail</c>/<c>detail</c>/<c>Jt</c>/<c>jtview</c>/…) is irrelevant to the lookup and dropped.
/// </para>
/// </summary>
public static class PartIdFieldResolver
{
    public static IReadOnlyDictionary<(string Class, string Suffix), string> BuildFieldMap(string partsDir)
    {
        var map = new Dictionary<(string Class, string Suffix), string>();

        foreach (var file in Directory.EnumerateFiles(partsDir, "*Parts.xml"))
        {
            var doc = XDocument.Load(file);
            foreach (var bin in doc.Root!.Elements("bin"))
            {
                foreach (var part in bin.Elements("part"))
                {
                    var id = (string?)part.Attribute("id");
                    if (string.IsNullOrEmpty(id)) continue;

                    var segments = id.Split('-');
                    if (segments.Length < 3) continue;
                    var cls = segments[0];
                    var suffix = string.Join('-', segments[2..]);

                    var field = (string?)part.Descendants("slice").FirstOrDefault()?.Attribute("field");
                    if (string.IsNullOrEmpty(field)) continue;

                    // First writer wins: a few suffixes are defined more than once, and every sample agreed.
                    map.TryAdd((cls, suffix), field);
                }
            }
        }

        return map;
    }
}
