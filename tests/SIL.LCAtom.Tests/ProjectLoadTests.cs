using SIL.LCAtom.Host.LcmUtils;
using SIL.LCAtom.Tests.TestFixtures;
using SIL.LCModel;
using Xunit;

namespace SIL.LCAtom.Tests;

/// <summary>
/// Stage A proof: a real FieldWorks <c>.fwdata</c> loads headless through
/// <see cref="FwDataProjectLoader"/>, and the loaded project exposes a non-empty lexicon via the
/// public <see cref="ILexEntryRepository"/>.
/// </summary>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public class ProjectLoadTests
{
    [Fact]
    public void OpeningRealProject_ReportsProjectNameAndPositiveEntryCount()
    {
        // Never mutate the shared fixture: copy to a temp directory this test owns and cleans up.
        var tempRoot = Path.Combine(Path.GetTempPath(), "SIL.LCAtom.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(tempRoot);

            var loader = new FwDataProjectLoader();
            using var cache = loader.LoadCache(fwDataPath);

            Assert.False(string.IsNullOrWhiteSpace(cache.ProjectId.Name));

            var entryRepo = cache.ServiceLocator.GetInstance<ILexEntryRepository>();
            Assert.True(entryRepo.Count > 0, "Expected the real TestLangProj fixture to contain lexical entries.");
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
