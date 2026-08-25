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
    public async Task RunAsync_ParsesTheReportProducedAgainstJustTheExportedDirectory()
    {
        var exported = Path.Combine(_root, "exported");
        Directory.CreateDirectory(exported);
        File.WriteAllText(Path.Combine(exported, "candidate.fwdata"), "RunAsync only needs this file to exist.");

        var scriptsDir = Path.Combine(_root, "fake-exe");
        Directory.CreateDirectory(scriptsDir);
        var executable = WriteFakeSuccessExecutable(scriptsDir, BuildFakeReportJson());

        var process = new PanGlossAssessmentProcess(executable);
        var report = await process.RunAsync(exported, CancellationToken.None);

        Assert.Equal("foma-confirm", report.Pipeline);
        Assert.Single(report.Words);
        Assert.Equal("motifa", report.Words[0].Word);
    }

    [Fact]
    public async Task RunAsync_CancellationKillsTheProcess()
    {
        var exported = Path.Combine(_root, "exported-slow");
        Directory.CreateDirectory(exported);
        File.WriteAllText(Path.Combine(exported, "candidate.fwdata"), "irrelevant to the fake executable.");

        // Outside RunAsync's own ephemeral scratch, so it survives that scratch's post-run cleanup.
        var heartbeatPath = Path.Combine(_root, "heartbeat.txt");
        var scriptsDir = Path.Combine(_root, "fake-exe-slow");
        Directory.CreateDirectory(scriptsDir);
        var executable = WriteFakeSlowExecutable(scriptsDir, heartbeatPath);

        var process = new PanGlossAssessmentProcess(executable);
        using var cts = new CancellationTokenSource();
        var runTask = process.RunAsync(exported, cts.Token);

        for (var i = 0; i < 100 && !File.Exists(heartbeatPath); i++)
            await Task.Delay(50);
        Assert.True(File.Exists(heartbeatPath), "the fake process never started ticking.");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        // If the OS process were merely abandoned rather than killed, the loop below would keep ticking.
        var afterCancel = File.ReadAllText(heartbeatPath).Trim();
        await Task.Delay(1500);
        var afterWaiting = File.ReadAllText(heartbeatPath).Trim();
        Assert.Equal(afterCancel, afterWaiting);
    }

    private static string BuildFakeReportJson() => """
        {
          "keyTable": ["11111111-1111-1111-1111-111111111111"],
          "cases": [
            {
              "input": "motifa",
              "outcome": "complete",
              "analyses": [
                { "identity": { "morphemes": [0], "rootIndex": 0 }, "identityDigest": "digest-1" }
              ]
            }
          ],
          "outcomeDigest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "semanticDigest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "provenance": {
            "sourceSha256": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            "modelFingerprint": "fp-1"
          },
          "execution": { "pipeline": "foma-confirm" },
          "diagnostics": []
        }
        """;

    // Routes the JSON through an environment variable so it never has to survive batch's own quoting rules.
    private static string WriteFakeSuccessExecutable(string directory, string reportJson)
    {
        var envVarName = "PG_FAKE_REPORT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVarName, reportJson);
        var scriptPath = Path.Combine(directory, "fake-pangloss.cmd");
        File.WriteAllText(scriptPath,
            "@echo off\r\n" +
            $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"[System.IO.File]::WriteAllText('%~4', $env:{envVarName})\"\r\n");
        return scriptPath;
    }

    // Ticks a counter into heartbeatPath forever, so a test can prove cancellation stops the ticking.
    private static string WriteFakeSlowExecutable(string directory, string heartbeatPath)
    {
        var scriptPath = Path.Combine(directory, "fake-pangloss-slow.cmd");
        File.WriteAllText(scriptPath,
            "@echo off\r\n" +
            "set counter=0\r\n" +
            ":loop\r\n" +
            "set /A counter=counter+1\r\n" +
            $"echo %counter% > \"{heartbeatPath}\"\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "if %counter% LSS 600 goto loop\r\n");
        return scriptPath;
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
