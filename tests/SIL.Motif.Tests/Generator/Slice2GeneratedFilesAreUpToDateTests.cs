using SIL.Motif.Generator;
using SIL.Motif.Generator.Emit;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The slice 2 analogue of <c>GeneratedFilesAreUpToDateTests</c>: the checked-in
/// <c>Operations/Generated2</c>, <c>Snapshotting/Generated2</c>, and
/// <c>SnapshotFields.Generated2.g.cs</c> files must be exactly what <see cref="Slice2CatalogWriter.WriteAll"/>
/// produces from today's model and manifest. A separate test from slice 1's rather than a widening of
/// it, matching <see cref="Slice2CatalogWriter"/> being a separate writer — see that type's remarks for
/// why (<c>GeneratedCatalogWriterTests</c> hard-asserts an exact file count this test must not touch).
/// </summary>
/// <remarks>
/// When this fails, the fix is the same as its slice-1 counterpart's: re-run
/// <c>dotnet run --project src/SIL.Motif.Generator -- emit</c> and commit the result, not edit the
/// expectation.
/// </remarks>
public class Slice2GeneratedFilesAreUpToDateTests
{
    [Fact]
    public void EveryCheckedInSlice2GeneratedFile_IsByteIdenticalToWhatTheGeneratorProducesNow()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "motif-drift-guard-slice2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var model = MotifModelLoader.Load();
            var written = Slice2CatalogWriter.WriteAll(model, tempRoot);

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
                $"The slice-2 generator produces {missing.Count} file(s) that are not checked in — run " +
                $"`dotnet run --project src/SIL.Motif.Generator -- emit` and commit:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", missing));

            Assert.True(
                stale.Count == 0,
                $"{stale.Count} checked-in slice-2 generated file(s) differ from what the generator produces " +
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
