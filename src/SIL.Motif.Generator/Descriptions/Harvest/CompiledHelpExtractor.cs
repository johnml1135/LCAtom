using System.Diagnostics;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// Opens FieldWorks' compiled help file so <see cref="FieldWorksHelpHarvester"/> can read pages out of it.
/// Windows-only, and deliberately confined to the dev-time <c>harvest-help</c> command: its output is the
/// checked-in <c>manifest/fieldworks-help-descriptions.tsv</c>, and nothing in the build, the tests, or the
/// runtime ever calls this.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tool is <c>hh.exe -decompile</c></b>, which ships with Windows. An earlier research pass reported
/// the <c>.chm</c> as unextractable "with tools available this session" and recommended un-shallowing the
/// <c>FwHelps</c> submodule instead; that was wrong, and the correction is worth keeping visible — the file
/// was sitting in the FieldWorks checkout and this command opens it in seconds.
/// </para>
/// <para>
/// <b>Why it polls.</b> <c>hh.exe</c> hands the work to the HTML Help runtime and exits immediately, so its
/// exit code says nothing about whether the extraction finished. This waits for the pages the map actually
/// needs, then stops — the alternative, sleeping a fixed number of seconds, is either slower than necessary
/// or a flake.
/// </para>
/// </remarks>
public static class CompiledHelpExtractor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static string Decompile(string chmPath, string destinationDirectory, TimeSpan? timeout = null)
    {
        if (!File.Exists(chmPath))
        {
            throw new GeneratorException(
                $"Compiled help file '{chmPath}' does not exist. It ships inside a FieldWorks checkout at " +
                "DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm.");
        }

        Directory.CreateDirectory(destinationDirectory);

        try
        {
            using var process = Process.Start(new ProcessStartInfo("hh.exe")
            {
                ArgumentList = { "-decompile", destinationDirectory, chmPath },
                UseShellExecute = false,
            });
            process?.WaitForExit();
        }
        catch (Exception ex) when (ex is not GeneratorException)
        {
            throw new GeneratorException(
                $"Could not run 'hh.exe -decompile' ({ex.Message}). This command is Windows-only; on another " +
                "platform, extract the help file elsewhere and pass the directory: " +
                "`dotnet run --project src/SIL.Motif.Generator -- harvest-help <extracted-help-root>`.", ex);
        }

        WaitForMappedPages(destinationDirectory, chmPath, timeout ?? TimeSpan.FromMinutes(2));
        return destinationDirectory;
    }

    private static void WaitForMappedPages(string destinationDirectory, string chmPath, TimeSpan timeout)
    {
        var expected = FieldWorksHelpFieldMap.Entries
            .Select(e => Path.Combine(destinationDirectory, e.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (expected.TrueForAll(File.Exists)) return;
            Thread.Sleep(PollInterval);
        }

        var stillMissing = expected.Where(p => !File.Exists(p)).ToList();
        throw new GeneratorException(
            $"Waited {timeout.TotalSeconds:0}s after 'hh.exe -decompile' and {stillMissing.Count} mapped " +
            $"page(s) never appeared under '{destinationDirectory}'. Run the extraction by hand to see what " +
            $"it did — `hh.exe -decompile <dir> \"{chmPath}\"` — then pass the directory to " +
            $"`harvest-help <extracted-help-root>`:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", stillMissing));
    }
}
