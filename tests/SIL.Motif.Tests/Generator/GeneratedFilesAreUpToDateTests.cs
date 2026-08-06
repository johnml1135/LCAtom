using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The drift guard for checked-in generated code: **the <c>.g.cs</c> files in the repository must be exactly
/// what the generator produces from today's model and manifest.**
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GeneratedCatalogWriterTests"/> deliberately writes to a temp directory, so it proves the writer
/// works and is idempotent without risking the checked-in output. That leaves a hole this test closes: nothing
/// otherwise notices when a <c>.g.cs</c> is hand-edited, or when the manifest changes and nobody re-runs
/// <c>dotnet run --project src/SIL.Motif.Generator -- emit</c>. Both are silent today and both defeat the point
/// of generating — the checked-in file would stop being a projection of its source and become a second,
/// divergent source of truth.
/// </para>
/// <para>
/// It is the same shape as the byte-equality conformance test <c>MOT-15</c> plans between the C# snapshot
/// producer and <c>pg-fwdata</c>, and for the same reason: where two artifacts must agree by construction,
/// assert it rather than assume it.
/// </para>
/// <para>
/// When this fails, the fix is to re-run the emitter and commit the result — not to edit the expectation.
/// </para>
/// </remarks>
public class GeneratedFilesAreUpToDateTests
{
    [Fact]
    public void EveryCheckedInGeneratedFile_IsByteIdenticalToWhatTheGeneratorProducesNow()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-drift-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var model = MotifModelLoader.Load();
            var written = GeneratedCatalogWriter.WriteAll(model, tempRoot);

            Assert.NotEmpty(written);

            var stale = new List<string>();
            var missing = new List<string>();

            foreach (var file in written)
            {
                var checkedInPath = Path.Combine(repoRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(checkedInPath))
                {
                    missing.Add(file.RelativePath);
                    continue;
                }

                var freshlyGenerated = File.ReadAllText(Path.Combine(tempRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                var checkedIn = File.ReadAllText(checkedInPath);

                // The writer normalises to LF on write (GeneratedCatalogWriter), but git may check out CRLF on
                // Windows, so compare with line endings normalised. Content drift is what matters here; line
                // endings are the checkout's business.
                if (Normalise(freshlyGenerated) != Normalise(checkedIn))
                    stale.Add(file.RelativePath);
            }

            Assert.True(
                missing.Count == 0,
                $"The generator produces {missing.Count} file(s) that are not checked in — run " +
                $"`dotnet run --project src/SIL.Motif.Generator -- emit` and commit:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", missing));

            Assert.True(
                stale.Count == 0,
                $"{stale.Count} checked-in generated file(s) differ from what the generator produces now. " +
                $"Either a .g.cs was hand-edited, or the manifest/model changed without re-running the " +
                $"emitter. Re-run `dotnet run --project src/SIL.Motif.Generator -- emit` and commit the " +
                $"result — do not edit the file by hand:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", stale));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch { /* best effort: a locked handle should not fail the test */ }
        }
    }

    private static string Normalise(string content) => content.Replace("\r\n", "\n");
}
