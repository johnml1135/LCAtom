namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Emission orchestrator for the 78 rows <see cref="Slice3FieldSelector"/> selects —
/// ADR 0025's parser-first shape (<c>HcReachable=yes</c>), restricted to the three generic shapes
/// slices 1/2 already built (basic <c>MultiUnicode</c>/<c>MultiString</c>/<c>Boolean</c>
/// <c>set|clear</c>, <c>rel/atomic</c> <c>set|clear</c>, <c>rel/col</c>/<c>rel/seq</c>
/// <c>addRef|removeRef</c>), across every declaring class rather than only <c>LexEntry</c>/<c>MoForm</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate writer from <see cref="GeneratedCatalogWriter"/> and <see cref="Slice2CatalogWriter"/>,
/// not an extension of either</b> — the same reason <c>Slice2CatalogWriter</c> gives for not widening
/// slice 1: <c>GeneratedCatalogWriterTests</c> hard-asserts 14 files and <c>Slice2CatalogWriterTests</c>
/// hard-asserts 8, both predating this slice. This writer is exercised by its own analogous tests
/// (<c>Slice3CatalogWriterTests</c>, <c>Slice3GeneratedFilesAreUpToDateTests</c>) and called alongside
/// the other two from <c>Program.cs</c>, so <c>dotnet run -- emit</c> still regenerates everything in
/// one command.
/// </para>
/// <para>
/// <b>No owning/atomic in this slice.</b> Unlike <see cref="Slice2CatalogWriter"/>, this writer never
/// calls anything like <c>LexEntryLexemeFormEmitter</c>: every owning/atomic row beyond
/// <c>LexEntry.LexemeForm</c> needs its own hand-written entity-construction-validity logic (ADR
/// 0022's "Creation validity" row — "the model file does not encode validity"), which is new
/// hand-written machinery per field, not a template instantiation of an existing shape. The task this
/// writer completes is explicitly scoped to shapes an existing emitter already handles; owning/atomic
/// rows are recorded as skipped, not silently dropped.
/// </para>
/// <para>
/// <b>One class can need a <c>Snapshotter</c>, a <c>RelationsSnapshotter</c>, or both.</b> A basic
/// field and a <c>rel/atomic</c> or <c>rel/col</c>/<c>rel/seq</c> field on the same declaring class
/// (e.g. <c>MoCompoundRule.Disabled</c> alongside <c>MoCompoundRule.ToProdRestrict</c>) produce two
/// sibling snapshotter files, exactly as slice 1 vs slice 2 already do for different classes — see
/// <see cref="Slice2CatalogWriter"/>'s remarks for why two sibling snapshotter classes per class cost
/// nothing functionally (every generated handler reads back only its own field via
/// <c>TryGetValue</c>).
/// </para>
/// </remarks>
public static class Slice3CatalogWriter
{
    public static IReadOnlyList<GeneratedCatalogWriter.WrittenFile> WriteAll(LoadedMotifModel model, string repoRoot)
    {
        var basicRows = Slice3FieldSelector.SelectBasicSetClear(model.Rows);
        var atomicRows = Slice3FieldSelector.SelectAtomicSetClear(model.Rows);
        var collectionRows = Slice3FieldSelector.SelectCollectionAddRemove(model.Rows);

        var basicSpecs = BasicFieldSpecBuilder.BuildAll(basicRows);
        var atomicSpecs = RelationFieldSpecBuilder.BuildAllAtomic(atomicRows);
        var collectionSpecs = RelationFieldSpecBuilder.BuildAllCollection(collectionRows);

        var written = new List<GeneratedCatalogWriter.WrittenFile>();

        // Operations: one file per field, in SIL.Motif.Runner/Operations/Generated3.
        var operationsDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Operations", "Generated3");
        Directory.CreateDirectory(operationsDir);

        foreach (var spec in basicSpecs)
        {
            var content = spec.Sig == "Boolean" ? BooleanFieldEmitter.Render(spec) : AlternativesFieldEmitter.Render(spec);
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", content));
        }

        foreach (var spec in atomicSpecs)
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", ReferenceAtomicFieldEmitter.Render(spec)));

        foreach (var spec in collectionSpecs)
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", ReferenceCollectionFieldEmitter.Render(spec)));

        // One Snapshotter per class with a basic field, one Relations per class with a rel; both if both.
        var snapshottingDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Snapshotting", "Generated3");
        Directory.CreateDirectory(snapshottingDir);

        foreach (var group in basicSpecs.GroupBy(s => s.DeclaringClass).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var content = SnapshotterEmitter.Render(group.Key, group.ToList());
            written.Add(WriteFile(repoRoot, snapshottingDir, $"{group.Key}Snapshotter.g.cs", content));
        }

        var relDeclaringClasses = atomicSpecs.Select(s => s.DeclaringClass)
            .Concat(collectionSpecs.Select(s => s.DeclaringClass))
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal);

        foreach (var declaringClass in relDeclaringClasses)
        {
            var lines = BuildPopulationLines(declaringClass, atomicSpecs, collectionSpecs);
            var targetInterface = "I" + declaringClass;
            var varName = Derivation.ConstructDerivation.Derive(declaringClass);
            written.Add(WriteFile(
                repoRoot, snapshottingDir, $"{declaringClass}RelationsSnapshotter.g.cs",
                RelationsSnapshotterEmitter.Render(declaringClass, targetInterface, varName, lines)));
        }

        // SnapshotFields: a third shared partial-class file, in SIL.Motif.Model/Snapshot.
        var modelSnapshotDir = Path.Combine(repoRoot, "src", "SIL.Motif.Model", "Snapshot");
        Directory.CreateDirectory(modelSnapshotDir);
        var constants = BuildConstantEntries(basicSpecs, atomicSpecs, collectionSpecs);
        written.Add(WriteFile(
            repoRoot, modelSnapshotDir, "SnapshotFields.Generated3.g.cs", Slice3SnapshotFieldsEmitter.Render(constants)));

        return written;
    }

    private static IReadOnlyList<RelationsSnapshotterEmitter.PopulationLine> BuildPopulationLines(
        string declaringClass,
        IReadOnlyList<ReferenceAtomicFieldSpec> atomicSpecs,
        IReadOnlyList<ReferenceCollectionFieldSpec> collectionSpecs)
    {
        var lines = new List<RelationsSnapshotterEmitter.PopulationLine>();
        var varName = Derivation.ConstructDerivation.Derive(declaringClass);

        foreach (var spec in atomicSpecs.Where(s => s.DeclaringClass == declaringClass))
        {
            lines.Add(new RelationsSnapshotterEmitter.PopulationLine(
                $"{spec.DeclaringClass}.{spec.FieldName} ({spec.Sig}, rel/atomic)",
                $"AddIfPopulated(fields, SnapshotFields.{spec.SnapshotFieldConstant}, ReferenceFieldSnapshotting.ReadAlternatives({varName}.{spec.AccessorPropertyName}));"));
        }

        foreach (var spec in collectionSpecs.Where(s => s.DeclaringClass == declaringClass))
        {
            lines.Add(new RelationsSnapshotterEmitter.PopulationLine(
                $"{spec.DeclaringClass}.{spec.FieldName} ({spec.Sig}, rel/{spec.Card.ToString().ToLowerInvariant()})",
                $"AddIfPopulated(fields, SnapshotFields.{spec.SnapshotFieldConstant}, ReferenceCollectionFieldSnapshotting.ReadAlternatives({varName}.{spec.AccessorPropertyName}));"));
        }

        return lines;
    }

    private static IReadOnlyList<Slice3SnapshotFieldsEmitter.ConstantEntry> BuildConstantEntries(
        IReadOnlyList<BasicFieldSpec> basicSpecs,
        IReadOnlyList<ReferenceAtomicFieldSpec> atomicSpecs,
        IReadOnlyList<ReferenceCollectionFieldSpec> collectionSpecs)
    {
        var entries = new List<Slice3SnapshotFieldsEmitter.ConstantEntry>();

        foreach (var spec in basicSpecs)
        {
            entries.Add(new Slice3SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"basic {spec.Sig}"));
        }

        foreach (var spec in atomicSpecs)
        {
            entries.Add(new Slice3SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"rel/atomic {spec.Sig}"));
        }

        foreach (var spec in collectionSpecs)
        {
            entries.Add(new Slice3SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"rel/{spec.Card.ToString().ToLowerInvariant()} {spec.Sig}"));
        }

        return entries;
    }

    private static GeneratedCatalogWriter.WrittenFile WriteFile(string repoRoot, string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return new GeneratedCatalogWriter.WrittenFile(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
    }
}
