using SIL.Motif.Generator.Emit;

namespace SIL.Motif.Generator;

/// <summary>
/// The generator's console entry point. Today it has exactly one command, <c>emit</c>, which is
/// MOT-4's chosen emission mechanism.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism chosen: checked-in files, produced by running this console command</b> — not a
/// build-time Roslyn source generator, and not an MSBuild target wired into
/// <c>SIL.Motif.Runner.csproj</c>'s build. Reasons, in order of weight:
/// </para>
/// <list type="number">
/// <item><b>Reviewability.</b> "The generated output must be inspectable by a human reviewer (this
/// repo reviews diffs)" is the task's own constraint. A Roslyn source generator's output lives in
/// <c>obj/generated</c> by default and is invisible to a PR diff unless
/// <c>EmitCompilerGeneratedFiles</c> is turned on and the output copied out — an extra step that
/// existed nowhere else in this repo. Checked-in <c>.g.cs</c> files under
/// <c>Operations/Generated/</c>/<c>Snapshotting/Generated/</c> are ordinary text a reviewer reads
/// exactly like any other file, with a header banner naming the command that produced them.</item>
/// <item><b>The <c>netstandard2.0</c>/<c>net48</c> constraint (ADR 0020).</b> A build-time source
/// generator is a Roslyn analyzer component with its own SDK and packaging concerns; an MSBuild
/// target that shells out to this project during <c>SIL.Motif.Runner</c>'s build would make the
/// Runner's build depend on the Generator project (net10.0-only, references the <c>SIL.LCModel</c>
/// NuGet package for <c>MasterLCModel.xml</c>) succeeding first, in the right order, on every
/// build — including whatever build FieldWorks' own <c>net48</c> solution eventually runs. A
/// checked-in file has no such coupling: <c>dotnet build</c> on a clean clone compiles plain C#,
/// exactly as <c>MOT-3</c> established for the Generator's own model-loading dependency.</item>
/// <item><b>Precedent already in this repo.</b> <c>SIL.Motif.Generator</c> (MOT-2/MOT-3) is already
/// a plain console-shaped library invoked by tests/a human, not a source generator — this just adds
/// its first thing to actually emit. And LibLCM itself generates the majority of its own model
/// classes from <c>MasterLCModel.xml</c> via an MSBuild task/NVelocity templates whose *output* is
/// also checked in (see docs/plan-motif.md, MOT-3) — the pattern this project mirrors is "generate,
/// then check in," not "generate on every build."</item>
/// </list>
/// <para>
/// Run it with <c>dotnet run --project src/SIL.Motif.Generator -- emit</c> from the repo root after
/// any manifest or <c>MasterLCModel.xml</c> change, and check in whatever it rewrites.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "emit")
            return RunEmit();

        Console.Error.WriteLine("Usage: dotnet run --project src/SIL.Motif.Generator -- emit");
        return 1;
    }

    private static int RunEmit()
    {
        var model = MotifModelLoader.Load();
        var repoRoot = RepoPaths.FindRepoRoot();

        // Three writers, not one: GeneratedCatalogWriter is slice 1 (basic set|clear), frozen at
        // exactly 14 files by GeneratedCatalogWriterTests/GeneratedFilesAreUpToDateTests, both of which
        // predate MOT-4 slice 2 and must not change. Slice2CatalogWriter adds the remaining verbs of
        // the lexical-entry family (LexEntry.LexemeForm, .DialectLabels, .DoNotPublishIn,
        // .DoNotShowMainEntryIn; MoForm.MorphType), frozen at 8 files the same way. Slice3CatalogWriter
        // widens the same three generic shapes (basic set|clear, rel/atomic set|clear, rel/col|seq
        // addRef|removeRef) beyond LexEntry/MoForm to the rest of ADR 0025's parser-first slice, so one
        // `emit` command still regenerates everything.
        var written = GeneratedCatalogWriter.WriteAll(model, repoRoot)
            .Concat(Emit.Slice2CatalogWriter.WriteAll(model, repoRoot))
            .Concat(Emit.Slice3CatalogWriter.WriteAll(model, repoRoot))
            .ToList();

        Console.WriteLine($"Wrote {written.Count} generated file(s):");
        foreach (var file in written.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            Console.WriteLine($"  {file.RelativePath}");

        return 0;
    }
}
