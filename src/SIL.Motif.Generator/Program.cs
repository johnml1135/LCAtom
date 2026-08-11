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

        if (args.Length == 1 && args[0] == "harvest-ordering-evidence")
            return RunHarvestOrderingEvidence();

        if (args.Length >= 1 && args[0] == "harvest-help")
            return RunHarvestHelp(args.Length > 1 ? args[1] : null);

        Console.Error.WriteLine(
            "Usage: dotnet run --project src/SIL.Motif.Generator -- <command>" + Environment.NewLine +
            "  emit                                        regenerate the checked-in operation/snapshot files" + Environment.NewLine +
            "  refresh-descriptions [--accept-source-move] re-attach provenance to manifest/kind-descriptions.tsv" + Environment.NewLine +
            "  harvest-help [extracted-help-root]          re-read FieldWorks' compiled help (Windows-only)" + Environment.NewLine +
            "  harvest-ordering-evidence                   re-read what the model says about field ordering");
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

        var current = ReadCurrentSourceArtifacts(model, fieldWorksCheckout);
        var moves = SourcePins.Compare(SourcePins.Read(pinsPath), current);
        var contentChanges = SourcePins.ContentChanges(moves);
        if (contentChanges.Count > 0 && !acceptSourceMove)
        {
            Console.Error.WriteLine(SourcePins.DescribeContentChanges(contentChanges, "manifest/source-pins.tsv"));
            return 2;
        }

        var existingRows = KindDescriptionTsvParser.Parse(descriptionsPath);
        var libLcmComments = LibLcmCommentHarvester.Harvest(model.Path);
        var contextHelpEntries = FieldWorksContextHelpHarvester.Harvest(contextHelpPath);
        var helpPages = FieldWorksHelpDescriptionTsv.ByField(FieldWorksHelpDescriptionTsv.Read(helpDescriptionsPath));

        var result = KindDescriptionRefresher.Refresh(
            existingRows, libLcmComments, contextHelpEntries, helpPages);
        KindDescriptionTsvWriter.Write(descriptionsPath, result.Rows);

        // A release that moved over byte-identical files is re-pinned silently-ish -- reported, not gated.
        // Only a changed file needed the operator's consent, and that was taken above.
        if (moves.Count > 0)
        {
            SourcePins.Write(pinsPath, current);
            foreach (var change in contentChanges)
            {
                Console.WriteLine(change.Pinned is null
                    ? $"  {change.Source}: {change.Artifact} newly pinned"
                    : $"  {change.Source}: {change.Artifact} re-pinned at {change.Current.DescribeRelease()}");
            }

            var releaseOnly = SourcePins.DescribeReleaseOnlyMoves(moves);
            if (releaseOnly.Length > 0) Console.WriteLine(releaseOnly);
        }

        Console.WriteLine($"Refreshed {result.Rows.Count} description row(s) -> {descriptionsPath}");
        Console.WriteLine($"  liblcm     : {model.Path}");
        Console.WriteLine($"  FieldWorks : {contextHelpPath}");
        Console.WriteLine($"  help pages : {helpDescriptionsPath}");
        Console.WriteLine($"  hand-corrected (preserved)        : {result.HandCorrected.Count}");
        Console.WriteLine($"  sourced from liblcm               : {result.SourcedFromLibLcm.Count}");
        Console.WriteLine($"  sourced from FieldWorks ContextHelp: {result.SourcedFromFieldWorks.Count}");
        Console.WriteLine($"  sourced from FieldWorks help pages : {result.SourcedFromHelp.Count}");
        Console.WriteLine($"  adapted from a sibling's source    : {result.Adapted.Count}");
        Console.WriteLine($"  exempt (no source exists)          : {result.Exempt.Count}");
        Console.WriteLine($"  unsourced (still open)             : {result.Unsourced.Count}");
        WriteKeys("unsourced keys", result.Unsourced);

        // The drift report. This is the reason the sources are pinned at all: an upstream sentence that was
        // reworded still reads fluently, so nothing downstream would notice it changed.
        if (result.Drifted.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {result.Drifted.Count} description(s) DRIFTED — the upstream fragment changed:");
            foreach (var drift in result.Drifted)
            {
                Console.WriteLine(
                    $"    {drift.Key}  ({drift.Source})" +
                    (drift.OurTextDiffersFromSource
                        ? "  [our text differs from this source by design — re-read it against the new wording]"
                        : ""));
                if (drift.PreviousText is { } previous) Console.WriteLine($"      was: {previous}");
                Console.WriteLine($"      now: {drift.CurrentSourceText}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Rewrites <c>manifest/ordering-evidence.tsv</c>: for every in-scope row claiming order carries
    /// meaning, whatever <c>MasterLCModel.xml</c> says about ordering, quoted and cited.
    /// </summary>
    /// <remarks>
    /// Reads only the model, so it needs no FieldWorks checkout. It answers a review question, not a build
    /// one — nothing derives from this file (ADR 0022 keeps <c>ComparisonClass</c> a function of
    /// <c>Card</c>), and its value is that the 2026-08-03 audit's recommended row-by-row review arrives with
    /// the evidence already gathered rather than needing to be re-found.
    /// </remarks>
    private static int RunHarvestOrderingEvidence()
    {
        var outputPath = RepoPaths.DefaultOrderingEvidencePath();
        var model = MotifModelLoader.Load();
        var comments = LibLcmCommentHarvester.Harvest(ModelPathResolver.Resolve().Path);

        var harvested = Ordering.OrderingEvidenceHarvester.Harvest(model.Rows, comments);
        Ordering.OrderingEvidenceTsv.Write(outputPath, harvested);

        var withStatement = harvested.Count(e => e.HasStatement);
        Console.WriteLine($"Harvested {harvested.Count} ordering claim(s) -> {outputPath}");
        Console.WriteLine($"  the model says something about order : {withStatement}");
        Console.WriteLine($"  the model says nothing               : {harvested.Count - withStatement}");
        Console.WriteLine();
        Console.WriteLine("  Rows resting on card=seq alone, needing a human (audit of 2026-08-03, §5):");
        foreach (var row in harvested.Where(e => !e.HasStatement))
            Console.WriteLine($"    {row.Key} ({row.ComparisonClass})");

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
    /// The three files the descriptions are copied out of, each with its content digest. liblcm normally
    /// resolves out of the NuGet package cache rather than a checkout, so its "release" is the pinned package
    /// version — which <c>SIL.Motif.Generator.csproj</c> already owns — and only the checkout case has a
    /// commit. The digest is what the check turns on either way.
    /// </summary>
    private static IReadOnlyList<SourceArtifact> ReadCurrentSourceArtifacts(
        ModelSource.ModelPathResult model, string fieldWorksCheckout)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var (libLcmRelease, libLcmCommit) = model.Source == ModelSource.ModelPathSource.NuGetPackageCache
            ? (ModelPathResolver.ReadPinnedPackageVersion(), "")
            : GitRelease.Read(LibLcmCheckoutRootOf(model.Path));

        var libLcmKind = model.Source == ModelSource.ModelPathSource.NuGetPackageCache
            ? SourceArtifact.NuGetPackageKind
            : SourceArtifact.GitCheckoutKind;

        var (fwRelease, fwCommit) = GitRelease.Read(fieldWorksCheckout);
        var contextHelp = Path.Combine("DistFiles", "Language Explorer", "Configuration", "ContextHelp.xml");
        var compiledHelp = Path.Combine("DistFiles", "Helps", "FieldWorks_Language_Explorer_Help.chm");

        return
        [
            new SourceArtifact(
                "liblcm", libLcmKind, libLcmRelease, libLcmCommit, "MasterLCModel.xml",
                SourceDigest.OfFile(model.Path), now),

            new SourceArtifact(
                "FieldWorks", SourceArtifact.GitCheckoutKind, fwRelease, fwCommit,
                contextHelp.Replace(Path.DirectorySeparatorChar, '/'),
                SourceDigest.OfFile(Path.Combine(fieldWorksCheckout, contextHelp)), now),

            // Pinned although refresh-descriptions never opens it: the checked-in help harvest is derived
            // from this file, so a changed digest here is the only thing that can say "re-run harvest-help".
            new SourceArtifact(
                "FieldWorks", SourceArtifact.GitCheckoutKind, fwRelease, fwCommit,
                compiledHelp.Replace(Path.DirectorySeparatorChar, '/'),
                SourceDigest.OfFile(Path.Combine(fieldWorksCheckout, compiledHelp)), now),
        ];
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
