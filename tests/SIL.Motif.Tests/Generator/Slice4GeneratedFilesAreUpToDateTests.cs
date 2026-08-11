using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The slice 4 analogue of <c>GeneratedFilesAreUpToDateTests</c>/<c>Slice3GeneratedFilesAreUpToDateTests</c>:
/// the checked-in <c>Operations/Generated4</c>, <c>Snapshotting/Generated4</c>, and
/// <c>SnapshotFields.Generated4.g.cs</c> files must be exactly what <see cref="Slice4CatalogWriter.WriteAll"/>
/// produces from today's model and manifest.
/// </summary>
/// <remarks>
/// When this fails, the fix is the same as its slice-1/2/3 counterparts': re-run
/// <c>dotnet run --project src/SIL.Motif.Generator -- emit</c> and commit the result, not edit the
/// expectation.
/// </remarks>
public class Slice4GeneratedFilesAreUpToDateTests
{
    [Fact]
    public void EveryCheckedInSlice4GeneratedFile_IsByteIdenticalToWhatTheGeneratorProducesNow()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-drift-guard-slice4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var model = MotifModelLoader.Load();
            var written = Slice4CatalogWriter.WriteAll(model, tempRoot);

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

                if (Normalise(freshlyGenerated) != Normalise(checkedIn))
                    stale.Add(file.RelativePath);
            }

            Assert.True(
                missing.Count == 0,
                $"The slice-4 generator produces {missing.Count} file(s) that are not checked in — run " +
                $"`dotnet run --project src/SIL.Motif.Generator -- emit` and commit:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", missing));

            Assert.True(
                stale.Count == 0,
                $"{stale.Count} checked-in slice-4 generated file(s) differ from what the generator produces " +
                $"now. Re-run `dotnet run --project src/SIL.Motif.Generator -- emit` and commit the result -- " +
                $"do not edit the file by hand:{Environment.NewLine}  " +
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
