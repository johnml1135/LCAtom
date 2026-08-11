namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Orchestrates the field-emission pipeline: select the field list (<see cref="BasicFieldSelector"/>),
/// derive each field's facts (<see cref="BasicFieldSpecBuilder"/>), render every generated file, and
/// write them to their checked-in locations under the repo root. This is the "console command"
/// half of the emission mechanism (see <c>Program.cs</c>'s doc comment for why that mechanism was
/// chosen over a build-time source generator or MSBuild target).
/// </summary>
public static class GeneratedCatalogWriter
{
    public sealed record WrittenFile(string RelativePath);

    public static IReadOnlyList<WrittenFile> WriteAll(LoadedMotifModel model, string repoRoot)
    {
        var selected = BasicFieldSelector.SelectSlice1BasicFields(model.Rows);
        var specs = BasicFieldSpecBuilder.BuildAll(selected);

        var glossSpec = specs.Single(s => s.DeclaringClass == "LexSense" && s.FieldName == "Gloss");
        var nonGlossSpecs = specs.Where(s => s != glossSpec).ToList();

        var written = new List<WrittenFile>();

        // Operations: one file per field, in SIL.Motif.Runner/Operations/Generated.
        var operationsDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Operations", "Generated");
        Directory.CreateDirectory(operationsDir);

        written.Add(WriteFile(repoRoot, operationsDir, "LexSenseGloss.g.cs", GlossFieldEmitter.Render(glossSpec)));
        foreach (var spec in nonGlossSpecs)
        {
            var content = spec.Sig == "Boolean" ? BooleanFieldEmitter.Render(spec) : AlternativesFieldEmitter.Render(spec);
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", content));
        }

        // Snapshotting: one file per declaring class, in SIL.Motif.Runner/Snapshotting/Generated.
        var snapshottingDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Snapshotting", "Generated");
        Directory.CreateDirectory(snapshottingDir);

        foreach (var group in specs.GroupBy(s => s.DeclaringClass).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var content = SnapshotterEmitter.Render(group.Key, group.ToList());
            written.Add(WriteFile(repoRoot, snapshottingDir, $"{group.Key}Snapshotter.g.cs", content));
        }

        // SnapshotFields: one shared partial-class file, in SIL.Motif.Model/Snapshot.
        var modelSnapshotDir = Path.Combine(repoRoot, "src", "SIL.Motif.Model", "Snapshot");
        Directory.CreateDirectory(modelSnapshotDir);
        written.Add(WriteFile(
            repoRoot, modelSnapshotDir, "SnapshotFields.Generated.g.cs", SnapshotFieldsEmitter.Render(nonGlossSpecs)));

        return written;
    }

    private static WrittenFile WriteFile(string repoRoot, string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);

        // LF only: avoids a churny CRLF<->LF diff depending on which OS regenerates this file.
        File.WriteAllText(path, content.Replace("\r\n", "\n"));

        return new WrittenFile(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
    }
}
