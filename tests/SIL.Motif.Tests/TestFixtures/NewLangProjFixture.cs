using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.LcmUtils;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// A real, empty LibLCM project created through LibLCM's own API, so a live
/// <see cref="LcmCache"/> costs no FieldWorks checkout and no vendored sample data.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LcmCache.CreateCacheWithNewBlankLangProj"/> builds the project outright rather than
/// copying anything: writing systems, styles and the standard lists come from the package already
/// pinned for everything else. What it does not build is lexical content — <b>no entries and no
/// senses</b> — so a test needing something to operate on creates it. That is what
/// <see cref="SeededProject"/> is for.
/// </para>
/// <para>
/// The emptiness is the point rather than a limitation to work around. A test reading whichever entry
/// happened to sort first in a 43 MB sample project asserts against data nobody in this repository
/// controls, and can fail for reasons having nothing to do with the code under test.
/// </para>
/// </remarks>
public static class NewLangProjFixture
{
    /// <summary>LibLCM expects the project folder and the <c>.fwdata</c> file to share a name.</summary>
    public const string ProjectName = "MotifTestProj";

    /// <summary>The vernacular writing system every seeded form is written in, and the default (first).</summary>
    public const string VernacularTag = "fr";

    /// <summary>
    /// A second vernacular writing system, added after the blank project is created. Exists so a test
    /// exercising vernacular-order behaviour (reordering, "first" vs. "only") has something to reorder;
    /// see <see cref="SIL.Motif.Tests.WritingSystems.WritingSystemInventoryTests"/>.
    /// </summary>
    public const string SecondVernacularTag = "es";

    /// <summary>The analysis writing system every seeded gloss is written in.</summary>
    public const string AnalysisTag = "en";

    /// <summary>
    /// Creates a blank project under <paramref name="tempRoot"/> and opens it. The caller owns
    /// <paramref name="tempRoot"/> and both disposes the cache and deletes the directory.
    /// </summary>
    public static LcmCache CreateCache(string tempRoot)
    {
        FwDataProjectLoader.Init();

        var projectFolder = Path.Combine(tempRoot, ProjectName);
        Directory.CreateDirectory(projectFolder);

        var templatesFolder = Path.Combine(tempRoot, "Templates");
        Directory.CreateDirectory(templatesFolder);

        var fwDataPath = Path.Combine(projectFolder, ProjectName + ".fwdata");
        var progress = new LcmThreadedProgress();

        var cache = LcmCache.CreateCacheWithNewBlankLangProj(
            new LocalProjectId(fwDataPath),
            AnalysisTag,
            VernacularTag,
            AnalysisTag,
            new HeadlessLcmUi(progress.SynchronizeInvoke),
            new LcmDirectories(tempRoot, templatesFolder),
            new LcmSettings());

        NonUndoableUnitOfWorkHelper.Do(cache.ActionHandlerAccessor, () =>
        {
            cache.ServiceLocator.WritingSystemManager.GetOrSet(SecondVernacularTag, out var secondVernWs);
            var writingSystems = cache.ServiceLocator.WritingSystems;
            writingSystems.VernacularWritingSystems.Add(secondVernWs);
            writingSystems.CurrentVernacularWritingSystems.Add(secondVernWs);
        });

        return cache;
    }

    /// <summary>The <c>.fwdata</c> path a cache from <see cref="CreateCache"/> is backed by.</summary>
    public static string FwDataPath(string tempRoot)
        => Path.Combine(tempRoot, ProjectName, ProjectName + ".fwdata");
}
