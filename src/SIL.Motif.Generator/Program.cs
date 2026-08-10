using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using SIL.Motif.Generator.Emit;
using SIL.Motif.Generator.ModelSource;

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

        if (args.Length >= 1 && args[0] == "refresh-descriptions")
            return RunRefreshDescriptions(args.Contains("--accept-source-move"));

        if (args.Length >= 1 && args[0] == "harvest-help")
            return RunHarvestHelp(args.Length > 1 ? args[1] : null);

        Console.Error.WriteLine(
            "Usage: dotnet run --project src/SIL.Motif.Generator -- <command>" + Environment.NewLine +
            "  emit                                        regenerate the checked-in operation/snapshot files" + Environment.NewLine +
            "  refresh-descriptions [--accept-source-move] re-attach provenance to manifest/kind-descriptions.tsv" + Environment.NewLine +
            "  harvest-help [extracted-help-root]          re-read FieldWorks' compiled help (Windows-only)");
        return 1;
    }

    private static int RunEmit()
    {
        var model = MotifModelLoader.Load();
        var repoRoot = RepoPaths.FindRepoRoot();

        // Four writers, not one: GeneratedCatalogWriter is slice 1 (basic set|clear), frozen at
        // exactly 14 files by GeneratedCatalogWriterTests/GeneratedFilesAreUpToDateTests, both of which
        // predate MOT-4 slice 2 and must not change. Slice2CatalogWriter adds the remaining verbs of
        // the lexical-entry family (LexEntry.LexemeForm, .DialectLabels, .DoNotPublishIn,
        // .DoNotShowMainEntryIn; MoForm.MorphType), frozen at 8 files the same way. Slice3CatalogWriter
        // widens the same three generic shapes (basic set|clear, rel/atomic set|clear, rel/col|seq
        // addRef|removeRef) beyond LexEntry/MoForm to the rest of ADR 0025's parser-first slice.
        // Slice4CatalogWriter (MOT-22) is the fourth shape: basic Integer standing in for a small closed
        // enum, derived set|clear like every other basic field, but with a range check the other basic
        // templates have no reason to emit. One `emit` command still regenerates everything.
        var written = GeneratedCatalogWriter.WriteAll(model, repoRoot)
            .Concat(Emit.Slice2CatalogWriter.WriteAll(model, repoRoot))
            .Concat(Emit.Slice3CatalogWriter.WriteAll(model, repoRoot))
            .Concat(Emit.Slice4CatalogWriter.WriteAll(model, repoRoot))
            .ToList();

        Console.WriteLine($"Wrote {written.Count} generated file(s):");
        foreach (var file in written.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            Console.WriteLine($"  {file.RelativePath}");

        return 0;
    }

    /// <summary>
    /// Stage 1 of the two-stage description pipeline docs/issues.md D8 asks for (Stage 2 is
    /// <see cref="KindDescriptionTsvParser"/> plus <see cref="Checks.DescriptionCheck"/>, unchanged). Rewrites
    /// <c>manifest/kind-descriptions.tsv</c> in place, attaching a citation to every row that has one and
    /// preserving the hand-corrected <c>ProdRestrict</c> family untouched.
    /// </summary>
    /// <remarks>
    /// Run it with <c>dotnet run --project src/SIL.Motif.Generator -- refresh-descriptions</c> after a
    /// liblcm or FieldWorks bump, and check in whatever it rewrites — the same convention <c>emit</c> uses.
    /// Needs a FieldWorks checkout (<c>MOTIF_FIELDWORKS_CHECKOUT</c>, or the conventional sibling-checkout
    /// layout); the liblcm side resolves the same way <c>emit</c> already does, package cache first.
    /// <para>
    /// It refuses to run when a source has moved off its pin, exiting <c>2</c> with the two releases named.
    /// <c>--accept-source-move</c> is how you say "yes, upgrade to that release": it re-pins and reports
    /// every description whose upstream sentence changed. The compiled-help pages are not read here at all —
    /// they come from the checked-in harvest, so this command needs no <c>.chm</c> and no Windows.
    /// </para>
    /// </remarks>
    private static int RunRefreshDescriptions(bool acceptSourceMove)
    {
        var descriptionsPath = RepoPaths.DefaultDescriptionsPath();
        var pinsPath = RepoPaths.DefaultSourcePinsPath();
        var helpDescriptionsPath = RepoPaths.DefaultHelpDescriptionsPath();

        var model = ModelPathResolver.Resolve();
        var contextHelpPath = FieldWorksPathResolver.ResolveContextHelpPath();
        var fieldWorksCheckout = FieldWorksPathResolver.ResolveCheckoutRoot();

        var current = ReadCurrentSourceReleases(model, fieldWorksCheckout);
        var moves = SourcePins.Compare(SourcePins.Read(pinsPath), current);
        if (moves.Count > 0 && !acceptSourceMove)
        {
            Console.Error.WriteLine(SourcePins.DescribeMoves(moves, "manifest/source-pins.tsv"));
            return 2;
        }

        var existingRows = KindDescriptionTsvParser.Parse(descriptionsPath);
        var libLcmComments = LibLcmCommentHarvester.Harvest(model.Path);
        var contextHelpEntries = FieldWorksContextHelpHarvester.Harvest(contextHelpPath);
        var helpPages = FieldWorksHelpDescriptionTsv.ByField(FieldWorksHelpDescriptionTsv.Read(helpDescriptionsPath));

        var result = KindDescriptionRefresher.Refresh(
            existingRows, libLcmComments, contextHelpEntries, helpPages);
        KindDescriptionTsvWriter.Write(descriptionsPath, result.Rows);

        if (moves.Count > 0)
        {
            SourcePins.Write(pinsPath, current);
            Console.WriteLine($"Re-pinned {moves.Count} moved source(s) -> {pinsPath}");
            foreach (var move in moves)
            {
                Console.WriteLine(move.PinnedRelease is null
                    ? $"  {move.Source}: newly pinned at {move.Current.Describe()}"
                    : $"  {move.Source}: {move.PinnedRelease.Describe()} -> {move.Current.Describe()}");
            }
        }

        Console.WriteLine($"Refreshed {result.Rows.Count} description row(s) -> {descriptionsPath}");
        Console.WriteLine($"  liblcm     : {model.Path}");
        Console.WriteLine($"  FieldWorks : {contextHelpPath}");
        Console.WriteLine($"  help pages : {helpDescriptionsPath}");
        Console.WriteLine($"  hand-corrected (preserved)        : {result.HandCorrected.Count}");
        Console.WriteLine($"  sourced from liblcm               : {result.SourcedFromLibLcm.Count}");
        Console.WriteLine($"  sourced from FieldWorks ContextHelp: {result.SourcedFromFieldWorks.Count}");
        Console.WriteLine($"  sourced from FieldWorks help pages : {result.SourcedFromHelp.Count}");
        Console.WriteLine($"  exempt (no source exists)          : {result.Exempt.Count}");
        Console.WriteLine($"  unsourced (still open)             : {result.Unsourced.Count}");
        WriteKeys("unsourced keys", result.Unsourced);

        // The drift report. This is the reason the sources are pinned at all: an upstream sentence that was
        // reworded still reads fluently, so nothing downstream would notice it changed.
        if (result.Drifted.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {result.Drifted.Count} description(s) DRIFTED — the upstream text changed:");
            foreach (var drift in result.Drifted)
            {
                Console.WriteLine($"    {drift.Key}  ({drift.Source})");
                Console.WriteLine($"      was: {drift.PreviousText}");
                Console.WriteLine($"      now: {drift.CurrentText}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Re-reads FieldWorks' compiled help and rewrites <c>manifest/fieldworks-help-descriptions.tsv</c>.
    /// Windows-only and run by hand, which is the whole point of the file existing: the <c>.chm</c> is a
    /// personal-machine dependency, and the harvested TSV is what the build, the tests and
    /// <c>refresh-descriptions</c> read.
    /// </summary>
    /// <param name="extractedHelpRoot">
    /// A help tree already decompiled elsewhere. Omit it on Windows and this runs
    /// <c>hh.exe -decompile</c> itself into a temp directory.
    /// </param>
    private static int RunHarvestHelp(string? extractedHelpRoot)
    {
        var outputPath = RepoPaths.DefaultHelpDescriptionsPath();

        string helpRoot;
        if (extractedHelpRoot is not null)
        {
            helpRoot = extractedHelpRoot;
        }
        else
        {
            var chmPath = FieldWorksPathResolver.ResolveCompiledHelpPath();
            helpRoot = Path.Combine(Path.GetTempPath(), "motif-fieldworks-help", Guid.NewGuid().ToString("N"));
            Console.WriteLine($"Decompiling {chmPath}{Environment.NewLine}  -> {helpRoot}");
            CompiledHelpExtractor.Decompile(chmPath, helpRoot);
        }

        var harvested = FieldWorksHelpHarvester.Harvest(helpRoot);
        FieldWorksHelpDescriptionTsv.Write(outputPath, harvested);

        Console.WriteLine($"Harvested {harvested.Count} help page(s) -> {outputPath}");
        foreach (var row in harvested)
            Console.WriteLine($"  {row.Key} ({row.Confidence}) <- {row.HelpPage}");

        Console.WriteLine(
            "Now run `refresh-descriptions` to fold these into manifest/kind-descriptions.tsv, and check " +
            "in both files.");
        return 0;
    }

    /// <summary>
    /// The current release of each source the descriptions are copied from. liblcm normally resolves out of
    /// the NuGet package cache rather than a checkout, so its "release" is the pinned package version —
    /// which <c>SIL.Motif.Generator.csproj</c> already owns — and only the checkout case has a commit.
    /// </summary>
    private static IReadOnlyList<SourceRelease> ReadCurrentSourceReleases(
        ModelSource.ModelPathResult model, string fieldWorksCheckout)
    {
        var now = DateTime.UtcNow;

        var libLcm = model.Source == ModelSource.ModelPathSource.NuGetPackageCache
            ? new SourceRelease(
                "liblcm", SourceRelease.NuGetPackageKind, ModelPathResolver.ReadPinnedPackageVersion(), "",
                now.ToString("yyyy-MM-ddTHH:mm:ssZ"))
            : GitRelease.Read("liblcm", LibLcmCheckoutRootOf(model.Path), now);

        return [libLcm, GitRelease.Read("FieldWorks", fieldWorksCheckout, now)];
    }

    /// <summary>
    /// <c>{root}/src/SIL.LCModel/MasterLCModel.xml</c> back to <c>{root}</c> — the layout
    /// <see cref="ModelPathResolver"/>'s checkout fallback already assumes.
    /// </summary>
    private static string LibLcmCheckoutRootOf(string modelFilePath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modelFilePath)!, "..", ".."));

    private static void WriteKeys(string label, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return;

        Console.WriteLine($"  {label}:");
        foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            Console.WriteLine($"    {key}");
    }
}
