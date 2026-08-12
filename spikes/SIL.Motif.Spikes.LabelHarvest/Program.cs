namespace SIL.Motif.Spikes.LabelHarvest;

/// <summary>
/// Harvests FieldWorks' own linguist-facing vocabulary into <c>manifest/fieldworks-labels.tsv</c>, per
/// ADR 0023 decision 5. Three mechanisms carry a label: the <c>.fwlayout</c>/<c>Parts</c> slice registry,
/// <c>strings-en.xml</c>, and <c>toolConfiguration.xml</c>. This tool implements a prior investigation of
/// those sources rather than redoing it.
/// </summary>
/// <remarks>
/// Usage: <c>dotnet run --project spikes/SIL.Motif.Spikes.LabelHarvest -- [FieldWorksRoot] [ManifestPath] [OutputPath]</c>
/// All three arguments are optional; defaults assume the conventional sibling-checkout layout used
/// throughout this repo (<c>../FieldWorks</c> next to <c>motif</c>).
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        var fieldWorksRoot = args.Length > 0 ? args[0] : FindDefault("FieldWorks");
        var manifestPath = args.Length > 1 ? args[1] : FindDefault(Path.Combine("motif", "manifest", "liblcm-inventory.tsv"));
        var outputPath = args.Length > 2 ? args[2] : FindDefault(Path.Combine("motif", "manifest", "fieldworks-labels.tsv"));

        var configRoot = Path.Combine(fieldWorksRoot, "DistFiles", "Language Explorer", "Configuration");
        var stringsEnPath = Path.Combine(configRoot, "strings-en.xml");
        var areaConfigPath = Path.Combine(configRoot, "Lists", "areaConfiguration.xml");
        var toolConfigPath = Path.Combine(configRoot, "Lists", "Edit", "toolConfiguration.xml");
        var partsDir = Path.Combine(configRoot, "Parts");

        foreach (var required in new[] { stringsEnPath, areaConfigPath, toolConfigPath, partsDir })
        {
            if (File.Exists(required) || Directory.Exists(required)) continue;
            Console.Error.WriteLine($"missing required source: {required}");
            return 2;
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"missing manifest (needed for coverage reporting, read-only): {manifestPath}");
            return 2;
        }

        var manifestRows = ManifestReader.Read(manifestPath);
        var knownClasses = manifestRows.Select(r => r.Class).ToHashSet();

        var raw = new List<RawLabel>();

        // Resolve composite part ids first, so e.g. MoInflAffixSlot's "NameAllA" ref lands on the field "Name".
        var fieldMap = PartIdFieldResolver.BuildFieldMap(partsDir);
        foreach (var fwlayout in Directory.EnumerateFiles(partsDir, "*.fwlayout"))
            raw.AddRange(SliceLabelHarvester.HarvestFwLayout(fwlayout, fieldMap));
        foreach (var partsXml in Directory.EnumerateFiles(partsDir, "*Parts.xml"))
            raw.AddRange(SliceLabelHarvester.HarvestPartsXml(partsXml));

        // Mechanism 3: tool/area config, and the ownerField -> class table mechanism 1 needs.
        var ownerFieldToClass = ToolConfigHarvester.HarvestOwnerFieldToClass(areaConfigPath);
        raw.AddRange(ToolConfigHarvester.Harvest(toolConfigPath, areaConfigPath));

        // Mechanism 1: strings-en.xml class names and list-purpose names.
        raw.AddRange(StringsEnHarvester.Harvest(stringsEnPath, ownerFieldToClass, knownClasses));

        var rows = LabelResolver.Resolve(raw);
        LabelTsvWriter.Write(outputPath, rows);

        PrintSummary(raw, rows, manifestRows, stringsEnPath, areaConfigPath, toolConfigPath, partsDir, outputPath);
        return 0;
    }

    private static void PrintSummary(
        List<RawLabel> raw,
        IReadOnlyList<LabelRow> rows,
        IReadOnlyList<ManifestRow> manifestRows,
        string stringsEnPath, string areaConfigPath, string toolConfigPath, string partsDir,
        string outputPath)
    {
        Console.WriteLine("FieldWorks label harvest");
        Console.WriteLine("------------------------");
        Console.WriteLine($"strings-en.xml    : {stringsEnPath}");
        Console.WriteLine($"areaConfig.xml    : {areaConfigPath}");
        Console.WriteLine($"toolConfig.xml    : {toolConfigPath}");
        Console.WriteLine($"Parts/            : {partsDir}");
        Console.WriteLine();
        Console.WriteLine($"raw label facts   : {raw.Count:N0}");
        Console.WriteLine($"output rows       : {rows.Count:N0}  -> {outputPath}");
        Console.WriteLine($"  by source       : {string.Join(", ", rows.GroupBy(r => r.Source).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}");
        Console.WriteLine($"  by confidence   : {string.Join(", ", rows.GroupBy(r => r.Confidence).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"))}");
        Console.WriteLine();

        var inScope = manifestRows.Where(r => r.Scope == "in").ToList();
        var exactPairs = rows.Where(r => r.Field.Length > 0 && r.Confidence != "ambiguous")
            .Select(r => (r.Class, r.Field)).ToHashSet();
        var ambiguousPairs = rows.Where(r => r.Field.Length > 0 && r.Confidence == "ambiguous")
            .Select(r => (r.Class, r.Field)).ToHashSet();
        var anyFieldLabelPairs = rows.Where(r => r.Field.Length > 0)
            .Select(r => (r.Class, r.Field)).ToHashSet();
        var classOnlyClasses = rows.Where(r => r.Field.Length == 0)
            .Select(r => r.Class).ToHashSet();

        var coveredExact = inScope.Count(r => exactPairs.Contains((r.Class, r.Field)));
        var coveredAmbiguous = inScope.Count(r => ambiguousPairs.Contains((r.Class, r.Field)));
        var coveredAny = inScope.Count(r => anyFieldLabelPairs.Contains((r.Class, r.Field)));
        var classOnlyOnly = inScope.Count(r => !anyFieldLabelPairs.Contains((r.Class, r.Field)) && classOnlyClasses.Contains(r.Class));
        var uncovered = inScope.Count - coveredAny - classOnlyOnly;

        Console.WriteLine($"in-scope manifest rows           : {inScope.Count:N0}");
        Console.WriteLine($"  field-level label, unambiguous  : {coveredExact:N0}");
        Console.WriteLine($"  field-level label, ambiguous    : {coveredAmbiguous:N0}");
        Console.WriteLine($"  field-level label, any          : {coveredAny:N0} ({100.0 * coveredAny / inScope.Count:N1}%)");
        Console.WriteLine($"  class-only label, no field hit  : {classOnlyOnly:N0}");
        Console.WriteLine($"  no label at all                 : {uncovered:N0}");
    }

    private static string FindDefault(string relativeToReposParent)
    {
        // Sibling checkouts: walk up to the shared parent rather than hardcoding an absolute path.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "motif")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                $"could not locate a 'repos'-style parent directory containing 'motif' by walking up from {AppContext.BaseDirectory}; pass an explicit path instead");

        return Path.Combine(dir.FullName, relativeToReposParent);
    }
}
