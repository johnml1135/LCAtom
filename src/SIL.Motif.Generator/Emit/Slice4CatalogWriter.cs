namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Emission orchestrator for the basic-Integer-enum shape <see cref="Slice4FieldSelector"/> selects
/// (e.g. <c>WfiWordform.SpellingStatus</c>).
/// </summary>
/// <remarks>
/// A separate writer from <see cref="GeneratedCatalogWriter"/>/<see cref="Slice2CatalogWriter"/>/
/// <see cref="Slice3CatalogWriter"/>, for the same reason <see cref="Slice3CatalogWriter"/> gives for not
/// widening slice 1/2: those writers' own <c>GeneratedFilesAreUpToDateTests</c>-style drift guards
/// hard-assert the file lists their own slice produces, so a new shape gets its own writer rather than
/// perturbing an existing one — even though the verbs are the same derived <c>set|clear</c>, the
/// range-checked Integer payload is not a shape those templates emit. Called alongside the other three
/// from <c>Program.cs</c>, so <c>dotnet run -- emit</c> still regenerates everything in one command.
/// </remarks>
public static class Slice4CatalogWriter
{
    public static IReadOnlyList<GeneratedCatalogWriter.WrittenFile> WriteAll(LoadedMotifModel model, string repoRoot)
    {
        var rows = Slice4FieldSelector.SelectIntegerEnum(model.Rows);
        var specs = IntegerEnumFieldSpecBuilder.BuildAll(rows);

        var written = new List<GeneratedCatalogWriter.WrittenFile>();

        // Operations: one file per field, in SIL.Motif.Runner/Operations/Generated4.
        var operationsDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Operations", "Generated4");
        Directory.CreateDirectory(operationsDir);

        foreach (var spec in specs)
        {
            written.Add(WriteFile(
                repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", IntegerEnumFieldEmitter.Render(spec)));
        }

        // Snapshotting: one file per declaring class, in SIL.Motif.Runner/Snapshotting/Generated4.
        var snapshottingDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Snapshotting", "Generated4");
        Directory.CreateDirectory(snapshottingDir);

        foreach (var group in specs.GroupBy(s => s.DeclaringClass).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var content = IntegerEnumSnapshotterEmitter.Render(group.Key, group.ToList());
            written.Add(WriteFile(repoRoot, snapshottingDir, $"{group.Key}Snapshotter.g.cs", content));
        }

        // SnapshotFields: reuses Slice3SnapshotFieldsEmitter, whose ConstantEntry/Render are spec-agnostic across basic/rel shapes, so this shape needs no separate copy.
        var modelSnapshotDir = Path.Combine(repoRoot, "src", "SIL.Motif.Model", "Snapshot");
        Directory.CreateDirectory(modelSnapshotDir);
        var constants = specs
            .Select(spec => new Slice3SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"basic {spec.Sig}"))
            .ToList();
        written.Add(WriteFile(
            repoRoot, modelSnapshotDir, "SnapshotFields.Generated4.g.cs", Slice3SnapshotFieldsEmitter.Render(constants)));

        return written;
    }

    private static GeneratedCatalogWriter.WrittenFile WriteFile(string repoRoot, string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return new GeneratedCatalogWriter.WrittenFile(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
    }
}
