using System.Diagnostics;
using SIL.LCModel;
using SIL.Motif.Host.LcmUtils;

namespace SIL.Motif.Spikes.ScratchCache;

/// <summary>
/// Spike harness for ADR 0016. Times both candidate scratch strategies and compares each resulting cache
/// against the live one.
/// </summary>
/// <remarks>
/// <para>
/// Usage: <c>dotnet run --project spikes/SIL.Motif.Spikes.ScratchCache -- "&lt;path to .fwdata&gt;"</c>
/// </para>
/// <para>
/// The project is copied to a temp directory first and every measurement runs against the copy, so the
/// source project is never opened, locked, or modified. Point it at
/// <c>../FieldWorks/DistFiles/Projects/Sena 3/Sena 3.fwdata</c> for real scale (152k objects); note that a
/// 50-object stub under %TEMP% reuses the name "Sena 3" and is not a scale fixture.
/// </para>
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: SIL.Motif.Spikes.ScratchCache <path to .fwdata>");
            Console.Error.WriteLine("  e.g. \"../FieldWorks/DistFiles/Projects/Sena 3/Sena 3.fwdata\"");
            return 2;
        }

        var sourceFwData = Path.GetFullPath(args[0]);
        if (!File.Exists(sourceFwData))
        {
            Console.Error.WriteLine($"not found: {sourceFwData}");
            return 2;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Spikes.ScratchCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            return Run(sourceFwData, tempRoot);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static int Run(string sourceFwData, string tempRoot)
    {
        var factory = new ScratchCacheFactory();
        var fileInfo = new FileInfo(sourceFwData);

        Header("scratch cache: cost and equivalence");
        Console.WriteLine($"project : {sourceFwData}");
        Console.WriteLine($"size    : {fileInfo.Length / 1024.0 / 1024.0:N1} MB");
        Console.WriteLine();

        // Work against our own copy throughout. The real project is never opened.
        var workingRoot = Path.Combine(tempRoot, "working");
        Directory.CreateDirectory(workingRoot);
        var (workingFwData, copyToWorkingMs) = Timed(() => CopyProject(sourceFwData, workingRoot));

        var (live, loadMs) = Timed(() => new FwDataProjectLoader().LoadScratchCache(workingFwData));
        try
        {
            var objectCount = live.ServiceLocator.GetInstance<ICmObjectRepository>().Count;
            Console.WriteLine($"objects : {objectCount:N0}");
            Console.WriteLine($"custom fields : {CountCustomFields(live)}");
            Console.WriteLine($"writing systems : {live.ServiceLocator.WritingSystems.AllWritingSystems.Count()}");
            Console.WriteLine();

            var timings = new List<(string Label, double Ms, string Note)>
            {
                ("copy project files to temp", copyToWorkingMs, "control: plain file I/O"),
                ("open the copy (cold load)", loadMs, "the cost every project open already pays"),
            };

            // --- Strategy A, from a COLD cache: only startup-fluffed objects are hot, so it is the best case.
            var (coldScratch, coldCopyMs) = Timed(() => factory.CreateInMemoryCopy(live, "scratch-from-cold"));
            timings.Add(("A: in-memory copy from a COLD live cache", coldCopyMs, "few objects fluffed yet"));

            // --- Strategy A, fanned out: should beat copying from HOT, pristine surrogates holding raw XML.
            var (derivedScratch, derivedCopyMs) = Timed(() => factory.CreateInMemoryCopy(coldScratch, "scratch-derived"));
            timings.Add(("A: derived copy from the PRISTINE scratch", derivedCopyMs, "ADR 0016's cheap fan-out"));

            // --- Strategy A, from a hot cache --------------------------------------------------
            var (fluffed, warmMs) = Timed(() => live.ServiceLocator.GetInstance<ICmObjectRepository>().AllInstances().Count());
            timings.Add(($"(warm the live cache: fluff all {fluffed:N0} objects)", warmMs, "setup, not a candidate"));

            var (hotScratch, hotCopyMs) = Timed(() => factory.CreateInMemoryCopy(live, "scratch-from-hot"));
            timings.Add(("A: in-memory copy from a HOT live cache", hotCopyMs, "worst case: ToXmlString() per object"));

            // --- Strategy B, the proven path ---------------------------------------------------
            var fileScratchRoot = Path.Combine(tempRoot, "file-scratch");
            var (fileScratch, filePathMs) = Timed(() => factory.CreateFromFileCopy(workingFwData, fileScratchRoot));
            timings.Add(("B: file copy + open (the XML path)", filePathMs, "loses unsaved edits; real writing systems"));

            // --- Can a copy of a FILE-loaded scratch keep writing systems, or is the loss inherent to kMemoryOnly?
            var (derivedFromFile, derivedFromFileMs) = Timed(() => factory.CreateInMemoryCopy(fileScratch, "derived-from-file"));
            timings.Add(("A-on-B: in-memory copy of the FILE-loaded scratch", derivedFromFileMs, "can fan-out inherit real writing systems?"));

            PrintTimings(timings);

            var reports = new[]
            {
                ScratchComparison.Compare(live, coldScratch, "A: in-memory copy (cold source)"),
                ScratchComparison.Compare(live, derivedScratch, "A: derived from pristine scratch"),
                ScratchComparison.Compare(live, hotScratch, "A: in-memory copy (hot source)"),
                ScratchComparison.Compare(live, fileScratch, "B: file copy + open"),
                ScratchComparison.Compare(live, derivedFromFile, "A-on-B: in-memory copy of the file-loaded scratch"),
            };

            foreach (var report in reports) PrintReport(report);

            PrintVerdict(timings, reports);

            coldScratch.Dispose();
            derivedScratch.Dispose();
            hotScratch.Dispose();
            derivedFromFile.Dispose();
            fileScratch.Dispose();

            // Non-zero only on text fidelity: degraded writing systems are expected, not a harness failure.
            return reports.All(r => r.IsUsableForTextFidelity) ? 0 : 1;
        }
        finally
        {
            live.Dispose();
        }
    }

    private static void PrintTimings(List<(string Label, double Ms, string Note)> timings)
    {
        Header("Timings");
        foreach (var (label, ms, note) in timings)
            Console.WriteLine($"{ms,10:N0} ms  {label,-46} {note}");
        Console.WriteLine();
    }

    private static void PrintReport(ScratchComparison.ComparisonReport r)
    {
        Header(r.Strategy);
        Console.WriteLine($"  objects        live {r.LiveObjectCount,8:N0}   scratch {r.ScratchObjectCount,8:N0}");
        Console.WriteLine($"  lex entries    live {r.LiveEntryCount,8:N0}   scratch {r.ScratchEntryCount,8:N0}");
        Console.WriteLine($"  writing systems  {r.WritingSystems.ValueEqualCount} of {r.WritingSystems.LiveCount} value-equal, {r.WritingSystems.Missing.Count} missing, {r.WritingSystems.Degraded.Count} degraded");
        Console.WriteLine($"  custom fields    live {r.CustomFields.LiveCount}, scratch {r.CustomFields.ScratchCount}, {r.CustomFields.FlidDrift.Count} flid drift, {r.CustomFields.Missing.Count} missing");
        Console.WriteLine($"  sense text       {r.SensesSampled - r.SenseTextMismatches.Count} of {r.SensesSampled} sampled match");

        if (r.Findings.Count == 0)
        {
            Console.WriteLine("  findings         none");
        }
        else
        {
            Console.WriteLine("  findings:");
            foreach (var f in r.Findings) Console.WriteLine($"    - {f}");
        }

        Console.WriteLine($"  => usable for text-fidelity work (e.g. setGloss) : {(r.IsUsableForTextFidelity ? "yes" : "NO")}");
        Console.WriteLine($"  => usable for writing-system-sensitive work      : {(r.IsUsableForWritingSystemSensitiveWork ? "yes" : "NO")}");
        Console.WriteLine();
    }

    // States ADR 0016's three falsification criteria, so a run either clears them or does not.
    private static void PrintVerdict(List<(string Label, double Ms, string Note)> timings, ScratchComparison.ComparisonReport[] reports)
    {
        double Ms(string fragment) => timings.First(t => t.Label.Contains(fragment, StringComparison.Ordinal)).Ms;

        var cold = Ms("COLD live cache");
        var hot = Ms("HOT live cache");
        var derived = Ms("PRISTINE scratch");
        var filePath = Ms("B: file copy");

        Header("Verdict against ADR 0016's falsification criteria");

        var ratio = derived <= 0 ? double.PositiveInfinity : hot / derived;
        Console.WriteLine($"1. fan-out is cheaper than re-copying from hot : {(ratio > 2 ? "HOLDS" : "FAILS")}  (hot/derived = {ratio:N1}x)");

        // A's cost scales with how hot the live cache is, so report a range and break-even, not one verdict.
        var beatsWhenCold = cold < filePath;
        var beatsWhenHot = hot < filePath;
        var crossover = hot > cold ? (filePath - cold) / (hot - cold) : double.NaN;
        Console.WriteLine($"2. in-memory beats the proven file path        : {(beatsWhenCold && beatsWhenHot ? "HOLDS" : beatsWhenCold ? "DEPENDS" : "FAILS")}");
        Console.WriteLine($"     cold {cold:N0} ms vs file {filePath:N0} ms -> {(beatsWhenCold ? "in-memory wins" : "file wins")}");
        Console.WriteLine($"     hot  {hot:N0} ms vs file {filePath:N0} ms -> {(beatsWhenHot ? "in-memory wins" : "file wins")}");
        if (!double.IsNaN(crossover) && crossover is > 0 and < 1)
            Console.WriteLine($"     break-even at ~{crossover:P0} of objects fluffed; above that, the file path is cheaper");

        var textOk = reports.Where(IsStrategyA).All(r => r.IsUsableForTextFidelity);
        var wsOk = reports.Where(IsStrategyA).All(r => r.IsUsableForWritingSystemSensitiveWork);
        Console.WriteLine($"3. the in-memory scratch is equivalent to live : {(textOk && wsOk ? "HOLDS" : textOk ? "PARTIAL" : "FAILS")}");
        Console.WriteLine($"     text fidelity            : {(textOk ? "equivalent" : "NOT equivalent")}");
        Console.WriteLine($"     writing-system behaviour : {(wsOk ? "equivalent" : "NOT equivalent")}");
        Console.WriteLine();
        Console.WriteLine("Criterion 3 is the one that would change the architecture rather than its parameters.");
        Console.WriteLine("A 'FAILS' on 1 or 2 argues for the XML path; a 'FAILS' on 3 argues for it much harder.");
        Console.WriteLine();

        // The decisive question for "can there be one canonical path?"
        var onB = reports.FirstOrDefault(r => r.Strategy.StartsWith("A-on-B", StringComparison.Ordinal));
        if (onB is not null)
        {
            Header("Can the cheap fan-out keep real writing systems?");
            Console.WriteLine(onB.IsUsableForWritingSystemSensitiveWork
                ? "YES — a memory-only copy of a file-loaded scratch kept its writing systems. The fan-out and\n" +
                  "lossless writing systems are compatible, so one canonical path can have both."
                : "NO — the writing-system loss is a property of every kMemoryOnly cache, not of the source it was\n" +
                  "copied from. Choosing a better source does not fix it: the fan-out is inherently lossy, so\n" +
                  "'cheap fan-out' and 'lossless' cannot both be had from CreateCacheCopy.");
        }

        static bool IsStrategyA(ScratchComparison.ComparisonReport r) => r.Strategy.StartsWith("A:", StringComparison.Ordinal);
    }

    private static int CountCustomFields(LcmCache cache)
    {
        var mdc = (SIL.LCModel.Infrastructure.IFwMetaDataCacheManaged)cache.MetaDataCacheAccessor;
        return mdc.GetFieldIds().Count(mdc.IsCustom);
    }

    private static string CopyProject(string sourceFwData, string destRoot)
    {
        var sourceFolder = Path.GetDirectoryName(Path.GetFullPath(sourceFwData))!;
        var projectName = Path.GetFileNameWithoutExtension(sourceFwData);
        var destFolder = Path.Combine(destRoot, projectName);
        CopyDirectory(sourceFolder, destFolder);
        return Path.Combine(destFolder, Path.GetFileName(sourceFwData));
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(sourceDir))
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
    }

    private static (T Value, double Ms) Timed<T>(Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var value = action();
        sw.Stop();
        return (value, sw.Elapsed.TotalMilliseconds);
    }

    private static void Header(string text)
    {
        Console.WriteLine(text);
        Console.WriteLine(new string('-', Math.Min(text.Length, 78)));
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort: a locked native handle should not fail the run */ }
    }
}
