using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Host.Parser;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Tests.TestFixtures;
using Xunit;

namespace SIL.Motif.Tests.Parser;

/// <summary>
/// Proves the export/execute seam's central hazard is closed: <see cref="PanGlossCandidateExporter"/>
/// saves and copies a candidate only when its backing project already lives inside the declared writable
/// scratch root, and refuses outright — before writing anything — when it does not.
/// </summary>
/// <remarks>
/// A candidate opened in place from a published Baseline directory
/// (<c>BaselineScratchFactory.OpenSingleUse</c>) is exactly the case that must be refused: saving it would
/// write back into a directory that must remain byte-for-byte immutable.
/// Pinned by `ExportAsync_RefusesACandidateBackedByAPublishedBaselineDirectory_AndLeavesItByteForByteUnchanged`.
/// </remarks>
[Collection(LcmCacheTestCollection.Name)]
public sealed class PanGlossCandidateExportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.PanGlossCandidateExportTests", Guid.NewGuid().ToString("N"));

    public PanGlossCandidateExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort: a locked native handle should not fail the test */ }
    }

    [Fact]
    public async Task ExportAsync_SavesAndCopiesTheCandidate_WhenItIsWithinTheWritableRoot()
    {
        var writableRoot = Path.Combine(_root, "writable");
        Directory.CreateDirectory(writableRoot);
        var candidate = NewLangProjFixture.CreateCache(writableRoot);
        try
        {
            var seed = SeededProject.Seed(candidate);
            var destination = Path.Combine(_root, "dest-ok");
            Directory.CreateDirectory(destination);
            var loader = new CountingFwDataProjectLoader();
            var exporter = new PanGlossCandidateExporter(writableRoot, loader);

            await exporter.ExportAsync(candidate, destination, CancellationToken.None);

            Assert.Equal(1, loader.SaveCount);
            var exportedFwData = Path.Combine(destination, NewLangProjFixture.ProjectName + ".fwdata");
            Assert.True(File.Exists(exportedFwData));

            using var reopened = new FwDataProjectLoader().LoadScratchCache(exportedFwData);
            var sense = reopened.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(seed.FirstSenseId);
            Assert.Equal(SeededProject.FirstGloss, sense.Gloss.get_String(reopened.DefaultAnalWs).Text);
        }
        finally
        {
            candidate.Dispose();
        }
    }

    [Fact]
    public async Task ExportAsync_RefusesANonEmptyDestination()
    {
        var writableRoot = Path.Combine(_root, "writable-nonempty");
        Directory.CreateDirectory(writableRoot);
        var candidate = NewLangProjFixture.CreateCache(writableRoot);
        try
        {
            var destination = Path.Combine(_root, "dest-nonempty");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "stray.txt"), "already here");
            var loader = new CountingFwDataProjectLoader();
            var exporter = new PanGlossCandidateExporter(writableRoot, loader);

            await Assert.ThrowsAsync<ArgumentException>(
                () => exporter.ExportAsync(candidate, destination, CancellationToken.None));

            Assert.Equal(0, loader.SaveCount);
        }
        finally
        {
            candidate.Dispose();
        }
    }

    [Fact]
    public async Task ExportAsync_ExportsAnewOnEachAttempt_NeverReusingAPriorExport()
    {
        var writableRoot = Path.Combine(_root, "writable-anew");
        Directory.CreateDirectory(writableRoot);
        var candidate = NewLangProjFixture.CreateCache(writableRoot);
        try
        {
            var seed = SeededProject.Seed(candidate);
            var destinationA = Path.Combine(_root, "dest-a");
            var destinationB = Path.Combine(_root, "dest-b");
            Directory.CreateDirectory(destinationA);
            Directory.CreateDirectory(destinationB);
            var loader = new CountingFwDataProjectLoader();
            var exporter = new PanGlossCandidateExporter(writableRoot, loader);

            await exporter.ExportAsync(candidate, destinationA, CancellationToken.None);

            NonUndoableUnitOfWorkHelper.Do(candidate.ActionHandlerAccessor, () =>
            {
                var sense = candidate.ServiceLocator.GetInstance<ILexSenseRepository>().GetObject(seed.FirstSenseId);
                sense.Gloss.set_String(candidate.DefaultAnalWs, "mutated after the first export");
            });

            await exporter.ExportAsync(candidate, destinationB, CancellationToken.None);

            Assert.Equal(2, loader.SaveCount);
            var fwDataA = Path.Combine(destinationA, NewLangProjFixture.ProjectName + ".fwdata");
            var fwDataB = Path.Combine(destinationB, NewLangProjFixture.ProjectName + ".fwdata");
            Assert.NotEqual(Sha256Of(fwDataA), Sha256Of(fwDataB));
        }
        finally
        {
            candidate.Dispose();
        }
    }

    [Fact]
    public async Task ExportAsync_RefusesACandidateBackedByAPublishedBaselineDirectory_AndLeavesItByteForByteUnchanged()
    {
        var publishedRoot = Path.Combine(_root, "published");
        Directory.CreateDirectory(publishedRoot);
        var masterRoot = Path.Combine(_root, "master");
        Directory.CreateDirectory(masterRoot);
        var master = NewLangProjFixture.CreateCache(masterRoot);
        try
        {
            SeededProject.Seed(master);
            new FwDataProjectLoader().Save(master);

            using (var bundle = new MemoryStream())
            {
                await new BaselineBundleWriter().WriteAsync(master, bundle, CancellationToken.None);
                using var archive = new ZipArchive(new MemoryStream(bundle.ToArray()), ZipArchiveMode.Read);
                archive.ExtractToDirectory(publishedRoot);
            }
            // Mirrors what a real Baseline publication pre-creates, so this fixture is shaped like one.
            Directory.CreateDirectory(Path.Combine(publishedRoot, "WritingSystemStore"));
            Directory.CreateDirectory(Path.Combine(publishedRoot, "SharedSettings"));
        }
        finally
        {
            master.Dispose();
        }
        Directory.Delete(masterRoot, recursive: true);

        var publishedFwData = Path.Combine(publishedRoot, NewLangProjFixture.ProjectName + ".fwdata");
        var directoriesBefore = DirectoriesUnder(publishedRoot);
        var manifestBefore = ManifestOf(publishedRoot);

        // The hazard itself: a candidate opened in place from inside the immutable published directory.
        var candidate = new FwDataProjectLoader().LoadScratchCache(publishedFwData);
        try
        {
            var writableRoot = Path.Combine(_root, "writable-refused");
            Directory.CreateDirectory(writableRoot);
            var destination = Path.Combine(_root, "dest-refused");
            Directory.CreateDirectory(destination);
            var loader = new CountingFwDataProjectLoader();
            var exporter = new PanGlossCandidateExporter(writableRoot, loader);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => exporter.ExportAsync(candidate, destination, CancellationToken.None));
            Assert.Contains("Baseline", exception.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(0, loader.SaveCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally
        {
            candidate.Dispose();
        }

        Assert.Equal(directoriesBefore, DirectoriesUnder(publishedRoot));
        Assert.Equal(manifestBefore, ManifestOf(publishedRoot));
    }

    private static string[] DirectoriesUnder(string root) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string ManifestOf(string root) =>
        string.Join(
            "\n",
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{Sha256Of(path)}"));

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // Counts real saves so a refusal test can prove the exporter's loader was never touched.
    private sealed class CountingFwDataProjectLoader : FwDataProjectLoader
    {
        public int SaveCount { get; private set; }

        public override void Save(LcmCache cache)
        {
            SaveCount++;
            base.Save(cache);
        }
    }
}

/// <summary>
/// Proves <see cref="PanGlossAssessmentProcess"/> needs nothing beyond an exported directory: it is
/// exercised here against a fake executable and a directory holding a bare <c>.fwdata</c> placeholder,
/// with no <see cref="LcmCache"/>, scratch, or Baseline in the picture at any point.
/// </summary>
public sealed class PanGlossAssessmentProcessTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.PanGlossAssessmentProcessTests", Guid.NewGuid().ToString("N"));

    public PanGlossAssessmentProcessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task RunAsyncRefusesADirectoryThatDoesNotExist()
    {
        var absent = Path.Combine(_root, "never-exported");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new PanGlossAssessmentProcess(FakeParser.ExecutablePath).RunAsync(absent, CancellationToken.None));
    }
}

/// <summary>
/// Proves the export/execute seam carries no engine or cache-identity surface: reflection over the public
/// API of the seam's three new types finds no member or parameter naming an engine or a cache key.
/// </summary>
public sealed class PanGlossCandidateExportSeamSurfaceTests
{
    [Fact]
    public void SeamTypesExposeNoEngineOrCacheKeySurface()
    {
        var seamTypes = new[]
        {
            typeof(IPanGlossCandidateExporter),
            typeof(PanGlossCandidateExporter),
            typeof(PanGlossAssessmentProcess),
        };
        var forbidden = new[] { "engine", "cachekey", "cache_key" };
        var offenders = new List<string>();

        foreach (var type in seamTypes)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(flags))
            {
                if (ContainsForbidden(member.Name, forbidden))
                    offenders.Add($"{type.Name}.{member.Name}");

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters())
                        if (ContainsForbidden(parameter.Name ?? string.Empty, forbidden))
                            offenders.Add($"{type.Name}.{member.Name}({parameter.Name})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Found engine/cache-key surface on the export seam: " + string.Join(", ", offenders));
    }

    private static bool ContainsForbidden(string name, IEnumerable<string> forbidden) =>
        forbidden.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
}
