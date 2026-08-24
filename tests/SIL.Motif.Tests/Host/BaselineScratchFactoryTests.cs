using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SIL.LCModel;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Host.Baselines;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Runner.DryRun;
using SIL.Motif.Runner.Operations;
using SIL.Motif.Tests.TestFixtures;
using Xunit;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Tests.Host;

/// <summary>
/// Proves the plan's core performance promise: <see cref="BaselineScratchFactory"/> opens the
/// <c>.fwdata</c> recorded inside an already-published Baseline directory directly — never copying
/// it — and that directory survives any number of Dry Runs byte-for-byte unchanged.
/// </summary>
[Collection(LcmCacheTestCollection.Name)]
public sealed class BaselineScratchFactoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "SIL.Motif.BaselineScratchFactoryTests", Guid.NewGuid().ToString("N"));
    private readonly string _publishedRoot;
    private readonly string _publishedFwDataPath;
    private readonly SeededProject _seed;

    public BaselineScratchFactoryTests()
    {
        Directory.CreateDirectory(_root);

        var masterRoot = Path.Combine(_root, "master");
        Directory.CreateDirectory(masterRoot);
        var master = NewLangProjFixture.CreateCache(masterRoot);
        try
        {
            _seed = SeededProject.Seed(master);
            new FwDataProjectLoader().Save(master);

            _publishedRoot = Path.Combine(_root, "published");
            Directory.CreateDirectory(_publishedRoot);
            using (var bundle = new MemoryStream())
            {
                new BaselineBundleWriter().WriteAsync(master, bundle, CancellationToken.None)
                    .GetAwaiter().GetResult();
                using var archive = new ZipArchive(new MemoryStream(bundle.ToArray()), ZipArchiveMode.Read);
                archive.ExtractToDirectory(_publishedRoot);
            }

            // Mirror the published layout: these are pre-created there so a project open is a pure read.
            Directory.CreateDirectory(Path.Combine(_publishedRoot, "WritingSystemStore"));
            Directory.CreateDirectory(Path.Combine(_publishedRoot, "SharedSettings"));
        }
        finally
        {
            master.Dispose();
        }

        Directory.Delete(masterRoot, recursive: true);
        _publishedFwDataPath = Path.Combine(_publishedRoot, NewLangProjFixture.ProjectName + ".fwdata");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort: a locked native handle should not fail the test */ }
    }

    [Fact]
    public void OpenSingleUse_PublishedDirectoryIsUnchangedAfterADryRunAndDispose()
    {
        var before = ManifestOf(_publishedRoot);

        var factory = new BaselineScratchFactory();
        using (var scratch = factory.OpenSingleUse(_publishedFwDataPath))
        {
            ProposalDryRunner.Run(scratch, BuildSetGlossProposal("mutated for assertion 1"));
        }

        var after = ManifestOf(_publishedRoot);
        Assert.Equal(before, after);
    }

    [Fact]
    public void OpenSingleUse_OpensInPlace_WithNoSiblingOrTempCopyOfTheProjectDirectory()
    {
        var siblingsBefore = TopLevelEntries(_root);

        var factory = new BaselineScratchFactory();
        using (var scratch = factory.OpenSingleUse(_publishedFwDataPath))
        {
            // The only public evidence of where the scratch's cache actually lives (ADR 0016).
            Assert.Contains(_publishedRoot, scratch.Provenance);
            ProposalDryRunner.Run(scratch, BuildSetGlossProposal("mutated for assertion 2"));
        }

        var siblingsAfter = TopLevelEntries(_root);
        Assert.Equal(siblingsBefore, siblingsAfter);
    }

    [Fact]
    public void OpenSingleUse_TheReturnedScratchIsStillSingleUse()
    {
        var factory = new BaselineScratchFactory();
        using var scratch = factory.OpenSingleUse(_publishedFwDataPath);
        var proposal = BuildSetGlossProposal("first run");

        ProposalDryRunner.Run(scratch, proposal);

        var reuse = Assert.Throws<InvalidOperationException>(() => ProposalDryRunner.Run(scratch, proposal));
        Assert.Contains("single-use", reuse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenSingleUse_TwentyReusesFromOnePublishedBaseline_ProduceIdenticalDigests()
    {
        var proposal = BuildSetGlossProposal("same proposal every time");
        var factory = new BaselineScratchFactory();

        string? firstEffectDigest = null;
        string? firstFootprintDigest = null;

        for (var iteration = 0; iteration < 20; iteration++)
        {
            DryRunModel dryRun;
            using (var scratch = factory.OpenSingleUse(_publishedFwDataPath))
                dryRun = ProposalDryRunner.Run(scratch, proposal);

            firstEffectDigest ??= dryRun.EffectDigest;
            firstFootprintDigest ??= dryRun.Anchor.FootprintDigest;

            Assert.Equal(firstEffectDigest, dryRun.EffectDigest);
            Assert.Equal(firstFootprintDigest, dryRun.Anchor.FootprintDigest);
        }
    }

    [Fact]
    public void OpenSingleUse_TwentyReusesFromOnePublishedBaseline_LeavePublishedDirectoryUnchanged()
    {
        var before = ManifestOf(_publishedRoot);
        var proposal = BuildSetGlossProposal("same proposal every time");
        var factory = new BaselineScratchFactory();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var scratch = factory.OpenSingleUse(_publishedFwDataPath);
            ProposalDryRunner.Run(scratch, proposal);
        }

        Assert.Equal(before, ManifestOf(_publishedRoot));
    }

    [Fact]
    public void OpenSingleUse_BlankPath_Throws()
    {
        var factory = new BaselineScratchFactory();
        var ex = Assert.Throws<ArgumentException>(() => factory.OpenSingleUse("   "));
        Assert.Equal("publishedFwDataPath", ex.ParamName);
    }

    [Fact]
    public void OpenSingleUse_MissingFile_Throws()
    {
        var factory = new BaselineScratchFactory();
        var missingPath = Path.Combine(_publishedRoot, "does-not-exist.fwdata");
        var ex = Assert.Throws<FileNotFoundException>(() => factory.OpenSingleUse(missingPath));
        Assert.Equal(missingPath, ex.FileName);
    }

    private Proposal BuildSetGlossProposal(string text)
    {
        var afterJson = JsonSerializer.Serialize(new { ws = NewLangProjFixture.AnalysisTag, text });
        using var afterDocument = JsonDocument.Parse(afterJson);

        var operation = new OperationEnvelope(
            operationId: CanonicalId.Mint(),
            kind: LexicalSenseOperationKinds.SetGloss,
            target: CanonicalId.FromGuid(_seed.FirstSenseId),
            after: afterDocument.RootElement.Clone());

        return new Proposal(
            contractVersions: new Dictionary<string, string> { ["lexical"] = "1.0" },
            proposalId: CanonicalId.Mint(),
            requires: null,
            operations: new[] { operation });
    }

    private static string[] TopLevelEntries(string root) =>
        Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

    private static string ManifestOf(string root)
    {
        // Directories are listed too: an in-place open must not even create an empty one.
        var directoryLines = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'));
        var fileLines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{Sha256Of(path)}");
        return string.Join("\n", directoryLines.Concat(fileLines).OrderBy(line => line, StringComparer.Ordinal));
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
