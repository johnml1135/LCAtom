namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Emission orchestrator for the remaining verbs of the lexical-entry family that
/// <see cref="GeneratedCatalogWriter"/> — slice 1's <c>set|clear</c>
/// over <c>basic</c> fields — does not cover: <c>LexEntry.LexemeForm</c> (owning/atomic
/// <c>create|delete</c>), <c>LexEntry.DialectLabels</c>/<c>.DoNotPublishIn</c>/<c>.DoNotShowMainEntryIn</c>
/// (<c>rel/col</c>/<c>rel/seq</c> <c>addRef|removeRef</c>), and <c>MoForm.MorphType</c> (<c>rel/atomic</c>
/// <c>set|clear</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate writer from <see cref="GeneratedCatalogWriter.WriteAll"/>, not an extension of it.</b>
/// <c>GeneratedCatalogWriterTests</c> hard-asserts <c>WriteAll</c> produces exactly 14 files and
/// <c>GeneratedFilesAreUpToDateTests</c> drives its byte-identity check off that same call —
/// both predate this slice and this task must not modify an existing test. Widening
/// <c>WriteAll</c>'s own output would break the file-count assertion for no benefit: slice 1's
/// fields, kinds, and files are complete and unrelated to what this slice adds. This writer is
/// exercised by its own analogous tests (<c>Slice2GeneratedCatalogWriterTests</c>,
/// <c>Slice2GeneratedFilesAreUpToDateTests</c>) and called alongside <c>WriteAll</c> from
/// <c>Program.cs</c>, so <c>dotnet run -- emit</c> still regenerates everything in one command.
/// </para>
/// <para>
/// <b>Snapshotting is a sibling file per class, not an addition to slice 1's.</b> For the same reason:
/// <c>LexEntrySnapshotter.g.cs</c>/<c>MoFormSnapshotter.g.cs</c> are slice 1's and untouched here.
/// <c>{Class}RelationsSnapshotter.g.cs</c> covers only this slice's fields. Every generated handler
/// reads back only its own field (<c>TryGetValue</c> on one constant), never a full merged object
/// snapshot, so two sibling snapshotter classes per class are functionally equivalent to one merged
/// class; combining them would not change any handler's behavior.
/// </para>
/// </remarks>
public static class Slice2CatalogWriter
{
    public static IReadOnlyList<GeneratedCatalogWriter.WrittenFile> WriteAll(LoadedMotifModel model, string repoRoot)
    {
        var atomicRows = RelationFieldSelector.SelectAtomicSetClear(model.Rows);
        var collectionRows = RelationFieldSelector.SelectCollectionAddRemove(model.Rows);
        var owningAtomicRows = RelationFieldSelector.SelectOwningAtomicCreateDelete(model.Rows);

        var atomicSpecs = RelationFieldSpecBuilder.BuildAllAtomic(atomicRows);
        var collectionSpecs = RelationFieldSpecBuilder.BuildAllCollection(collectionRows);

        var lexemeFormRow = owningAtomicRows.Single(r => r.DeclaringClass == "LexEntry" && r.FieldName == "LexemeForm");
        if (owningAtomicRows.Count != 1)
        {
            throw new GeneratorException(
                $"Slice2CatalogWriter expected exactly one owning/atomic create|delete row for " +
                $"LexEntry/MoForm (LexEntry.LexemeForm); the manifest now has {owningAtomicRows.Count}. " +
                "A new row means a new field needs its own hand-written create validity logic before " +
                "this writer can emit it -- see LexEntryLexemeFormEmitter's remarks.");
        }

        var written = new List<GeneratedCatalogWriter.WrittenFile>();

        // Operations: one file per field, in SIL.Motif.Runner/Operations/Generated2.
        var operationsDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Operations", "Generated2");
        Directory.CreateDirectory(operationsDir);

        foreach (var spec in atomicSpecs)
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", ReferenceAtomicFieldEmitter.Render(spec)));

        foreach (var spec in collectionSpecs)
            written.Add(WriteFile(repoRoot, operationsDir, $"{spec.SnapshotFieldConstant}.g.cs", ReferenceCollectionFieldEmitter.Render(spec)));

        written.Add(WriteFile(
            repoRoot, operationsDir, "LexEntryLexemeForm.g.cs",
            LexEntryLexemeFormEmitter.Render(lexemeFormRow, model.Model.Classes)));

        // Snapshotting: one file per declaring class, in SIL.Motif.Runner/Snapshotting/Generated2.
        var snapshottingDir = Path.Combine(repoRoot, "src", "SIL.Motif.Runner", "Snapshotting", "Generated2");
        Directory.CreateDirectory(snapshottingDir);

        foreach (var declaringClass in new[] { "LexEntry", "MoForm" })
        {
            var lines = BuildPopulationLines(declaringClass, atomicSpecs, collectionSpecs, lexemeFormRow);
            var targetInterface = "I" + declaringClass;
            var varName = ConstructVarName(declaringClass);
            written.Add(WriteFile(
                repoRoot, snapshottingDir, $"{declaringClass}RelationsSnapshotter.g.cs",
                RelationsSnapshotterEmitter.Render(declaringClass, targetInterface, varName, lines)));
        }

        // SnapshotFields: a second shared partial-class file, in SIL.Motif.Model/Snapshot.
        var modelSnapshotDir = Path.Combine(repoRoot, "src", "SIL.Motif.Model", "Snapshot");
        Directory.CreateDirectory(modelSnapshotDir);
        var constants = BuildConstantEntries(atomicSpecs, collectionSpecs, lexemeFormRow);
        written.Add(WriteFile(
            repoRoot, modelSnapshotDir, "SnapshotFields.Generated2.g.cs", Slice2SnapshotFieldsEmitter.Render(constants)));

        return written;
    }

    private static IReadOnlyList<RelationsSnapshotterEmitter.PopulationLine> BuildPopulationLines(
        string declaringClass,
        IReadOnlyList<ReferenceAtomicFieldSpec> atomicSpecs,
        IReadOnlyList<ReferenceCollectionFieldSpec> collectionSpecs,
        Join.JoinedRow lexemeFormRow)
    {
        var lines = new List<RelationsSnapshotterEmitter.PopulationLine>();
        var varName = ConstructVarName(declaringClass);

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

        if (declaringClass == lexemeFormRow.DeclaringClass)
        {
            lines.Add(new RelationsSnapshotterEmitter.PopulationLine(
                $"{lexemeFormRow.DeclaringClass}.{lexemeFormRow.FieldName} ({lexemeFormRow.Sig}, owning/atomic)",
                $"AddIfPopulated(fields, SnapshotFields.{lexemeFormRow.DeclaringClass}{lexemeFormRow.FieldName}, ReferenceFieldSnapshotting.ReadAlternatives({varName}.{lexemeFormRow.FieldName}OA));"));
        }

        return lines;
    }

    private static IReadOnlyList<Slice2SnapshotFieldsEmitter.ConstantEntry> BuildConstantEntries(
        IReadOnlyList<ReferenceAtomicFieldSpec> atomicSpecs,
        IReadOnlyList<ReferenceCollectionFieldSpec> collectionSpecs,
        Join.JoinedRow lexemeFormRow)
    {
        var entries = new List<Slice2SnapshotFieldsEmitter.ConstantEntry>();

        foreach (var spec in atomicSpecs)
        {
            entries.Add(new Slice2SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"rel/atomic {spec.Sig}"));
        }

        foreach (var spec in collectionSpecs)
        {
            entries.Add(new Slice2SnapshotFieldsEmitter.ConstantEntry(
                spec.DeclaringClass, spec.FieldName, spec.Group, spec.Construct, spec.SnapshotFieldConstant,
                $"rel/{spec.Card.ToString().ToLowerInvariant()} {spec.Sig}"));
        }

        var group = Derivation.GroupDerivation.Derive(lexemeFormRow.DeclaringClass);
        var construct = Derivation.ConstructDerivation.Derive(lexemeFormRow.DeclaringClass);
        entries.Add(new Slice2SnapshotFieldsEmitter.ConstantEntry(
            lexemeFormRow.DeclaringClass, lexemeFormRow.FieldName, group, construct,
            lexemeFormRow.DeclaringClass + lexemeFormRow.FieldName, $"owning/atomic {lexemeFormRow.Sig}"));

        return entries;
    }

    private static string ConstructVarName(string declaringClass) => Derivation.ConstructDerivation.Derive(declaringClass);

    private static GeneratedCatalogWriter.WrittenFile WriteFile(string repoRoot, string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return new GeneratedCatalogWriter.WrittenFile(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'));
    }
}
