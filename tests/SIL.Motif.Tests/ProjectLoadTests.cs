using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.Motif.Tests;

/// <summary>
/// Stage A proof: a real <c>.fwdata</c> on disk loads headless through
/// <see cref="FwDataProjectLoader"/>, and the loaded project exposes a non-empty lexicon via the
/// public <see cref="ILexEntryRepository"/>.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public class ProjectLoadTests
{
    [Fact]
    public void OpeningRealProject_ReportsProjectNameAndPositiveEntryCount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new FwDataProjectLoader();

            using (var cache = NewLangProjFixture.CreateCache(tempRoot))
            {
                SeededProject.Seed(cache);
                loader.Save(cache);
            }

            // Reopen from disk: proves LoadCache itself, not just the in-process cache that wrote the file.
            using var reopened = loader.LoadCache(NewLangProjFixture.FwDataPath(tempRoot));

            Assert.False(string.IsNullOrWhiteSpace(reopened.ProjectId.Name));

            var entryRepo = reopened.ServiceLocator.GetInstance<ILexEntryRepository>();
            Assert.True(entryRepo.Count > 0, "Expected the reopened project to contain lexical entries.");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup; a locked native handle should not fail the test
            }
        }
    }
}
