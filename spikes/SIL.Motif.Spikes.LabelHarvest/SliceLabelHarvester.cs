using System.Xml.Linq;

namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Harvests mechanism 2: the <c>.fwlayout</c> / <c>Parts/*.xml</c> slice system. See
/// <c>docs/research/2026-08-05-fieldworks-user-facing-names.md</c> §1.2 — this is the closest thing in
/// FieldWorks to a canonical <c>(class, field) -&gt; label [+ tooltip]</c> registry, keyed by which
/// <c>&lt;layout class="…"&gt;</c> (in <c>.fwlayout</c> files) or <c>&lt;bin class="…"&gt;</c> (in
/// <c>Parts/*.xml</c> files) a labeled <c>&lt;part&gt;</c>/<c>&lt;slice&gt;</c> is nested under — never the
/// bare <c>ref</c>/<c>field</c> string alone, which is reused across unrelated classes (e.g. <c>Gloss</c> on
/// both <c>LexSense</c> and <c>LexEtymology</c>).
/// </summary>
public static class SliceLabelHarvester
{
    /// <summary>
    /// Harvests a <c>.fwlayout</c> file: top-level <c>&lt;layout class="C" type="T" name="N"&gt;</c> blocks,
    /// each containing <c>&lt;part ref="Field" label="Label"/&gt;</c> at any depth (some are nested inside
    /// grouping elements like <c>&lt;indent&gt;</c>). <c>.fwlayout</c> parts never carry a tooltip.
    /// </summary>
    /// <param name="path">The <c>.fwlayout</c> file to harvest.</param>
    /// <param name="fieldMap">Resolves a <c>ref</c> that names a composite <c>Parts/*.xml</c> part (e.g.
    /// <c>NameAllA</c>) to the real field it wraps (e.g. <c>Name</c>) — see
    /// <see cref="PartIdFieldResolver"/>. When no entry matches, the <c>ref</c> is used literally, which is
    /// correct for the many refs that already name a bare field (<c>LexemeForm</c>, <c>Gloss</c>).</param>
    public static IReadOnlyList<RawLabel> HarvestFwLayout(
        string path, IReadOnlyDictionary<(string Class, string Suffix), string> fieldMap)
    {
        var doc = XDocument.Load(path);
        var fileName = Path.GetFileName(path);
        var root = doc.Root ?? throw new InvalidOperationException($"{path}: no root element");
        var results = new List<RawLabel>();

        foreach (var layout in root.Elements("layout"))
        {
            var cls = (string?)layout.Attribute("class");
            var type = (string?)layout.Attribute("type");
            var name = (string?)layout.Attribute("name");
            if (string.IsNullOrEmpty(cls)) continue;

            foreach (var part in layout.Descendants("part"))
            {
                var reference = (string?)part.Attribute("ref");
                var label = (string?)part.Attribute("label");
                if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(label)) continue;
                if (reference.StartsWith('_')) continue; // structural placeholders, e.g. _CustomFieldPlaceholder

                var resolved = fieldMap.TryGetValue((cls, reference), out var realField);
                var field = resolved ? realField! : reference;
                var detail = resolved
                    ? $"{fileName}, layout class={cls} type={type} name={name}, ref={reference} resolved via Parts/*.xml"
                    : $"{fileName}, layout class={cls} type={type} name={name}";

                results.Add(new RawLabel(cls, field, label, "", "slice", detail));
            }
        }

        return results;
    }

    /// <summary>
    /// Harvests a <c>Parts/*.xml</c> file: top-level <c>&lt;bin class="C"&gt;</c> blocks, each containing
    /// <c>&lt;part id="…"&gt;</c> elements that wrap a <c>&lt;slice field="Field" label="Label"
    /// tooltip="Tooltip"/&gt;</c> at any depth.
    /// </summary>
    public static IReadOnlyList<RawLabel> HarvestPartsXml(string path)
    {
        var doc = XDocument.Load(path);
        var fileName = Path.GetFileName(path);
        var root = doc.Root ?? throw new InvalidOperationException($"{path}: no root element");
        var results = new List<RawLabel>();

        foreach (var bin in root.Elements("bin"))
        {
            var cls = (string?)bin.Attribute("class");
            if (string.IsNullOrEmpty(cls)) continue;

            // One real FieldWorks data-quality bug found while building this harvester:
            // CellarParts.xml has <bin class="CmSemanticDomain>"> — a stray '>' baked into the attribute
            // value. Strip it so the row still matches the real class instead of silently missing coverage.
            cls = cls.TrimEnd('>');

            foreach (var slice in bin.Descendants("slice"))
            {
                var field = (string?)slice.Attribute("field");
                var label = (string?)slice.Attribute("label");
                if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(label)) continue;
                if (field.StartsWith('_')) continue;

                var tooltip = (string?)slice.Attribute("tooltip") ?? "";
                var partId = slice.Ancestors("part").FirstOrDefault()?.Attribute("id")?.Value ?? "";

                results.Add(new RawLabel(cls, field, label, tooltip, "slice",
                    $"{fileName}, bin class={cls}, part id={partId}"));
            }
        }

        return results;
    }
}
