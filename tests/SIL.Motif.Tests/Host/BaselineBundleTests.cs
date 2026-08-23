using System.IO.Compression;
using System.Security.Cryptography;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Tests.TestFixtures;
using SIL.WritingSystems;
using Xunit;

namespace SIL.Motif.Tests.Host;

[Collection(LcmCacheTestCollection.Name)]
public sealed class BaselineBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SIL.Motif.BaselineBundleTests", Guid.NewGuid().ToString("N"));
    private readonly LcmCache _cache;
    private readonly FwDataProjectLoader _loader = new();

    public BaselineBundleTests()
    {
        Directory.CreateDirectory(_root);
        _cache = NewLangProjFixture.CreateCache(_root);
        SeededProject.Seed(_cache);

        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var vernacular = _cache.ServiceLocator.WritingSystems.DefaultVernacularWritingSystem;
            vernacular.DefaultCollation = new IcuRulesCollationDefinition("standard") { IcuRules = "&z < a" };
            vernacular.CharacterSets.Clear();
            vernacular.CharacterSets.Add(new CharacterSetDefinition("main") { Characters = { "a", "z", "'" } });
        });

        _cache.ServiceLocator.WritingSystemManager.Save();
        _loader.Save(_cache);
    }

    [Fact]
    public async Task WriteAsync_StreamsOnlyFwDataAndProjectWritingSystems()
    {
        var projectFolder = Path.GetDirectoryName(_cache.ProjectId.Path)!;
        Directory.CreateDirectory(Path.Combine(projectFolder, "LinkedFiles", "AudioVisual"));
        File.WriteAllBytes(Path.Combine(projectFolder, "LinkedFiles", "AudioVisual", "large.wav"), new byte[2_000_000]);
        File.WriteAllText(Path.Combine(projectFolder, "project.motif.db"), "not canonical project data");
        File.WriteAllText(Path.Combine(projectFolder, "project.fwbackup"), "backup");
        File.WriteAllText(Path.Combine(projectFolder, "unrelated.txt"), "unrelated");
        Directory.CreateDirectory(Path.Combine(projectFolder, "OtherRepositories"));
        File.WriteAllText(Path.Combine(projectFolder, "OtherRepositories", "repository.bin"), "repository");

        using var bytes = new MemoryStream();
        using var throttled = new MaximumWriteSizeStream(bytes, 32 * 1024);
        var token = await new BaselineBundleWriter().WriteAsync(_cache, throttled, CancellationToken.None);

        Assert.True(throttled.MaximumObservedWrite <= 32 * 1024);
        var archiveBytes = bytes.ToArray();
        Assert.Equal(Digest(archiveBytes), token.BundleDigest);

        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Contains(NewLangProjFixture.ProjectName + ".fwdata", names);
        Assert.Contains(names, name => name.StartsWith("WritingSystemStore/", StringComparison.Ordinal) && name.EndsWith(".ldml", StringComparison.Ordinal));
        Assert.All(names, name => Assert.True(
            name == NewLangProjFixture.ProjectName + ".fwdata" ||
            name.StartsWith("WritingSystemStore/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WriteAsync_ReloadsWithEquivalentModelAndWritingSystems()
    {
        using var bytes = new MemoryStream();
        await new BaselineBundleWriter().WriteAsync(_cache, bytes, CancellationToken.None);

        var extractedRoot = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extractedRoot);
        using (var archive = new ZipArchive(new MemoryStream(bytes.ToArray()), ZipArchiveMode.Read))
            archive.ExtractToDirectory(extractedRoot);

        using var scratch = _loader.LoadScratchCache(Path.Combine(extractedRoot, NewLangProjFixture.ProjectName + ".fwdata"));
        Assert.Equal(
            _cache.ServiceLocator.GetInstance<ILexEntryRepository>().Count,
            scratch.ServiceLocator.GetInstance<ILexEntryRepository>().Count);
        var expected = _cache.ServiceLocator.WritingSystems.AllWritingSystems.OrderBy(ws => ws.LanguageTag).ToArray();
        var actual = scratch.ServiceLocator.WritingSystems.AllWritingSystems.OrderBy(ws => ws.LanguageTag).ToArray();
        Assert.Equal(expected.Select(ws => ws.LanguageTag), actual.Select(ws => ws.LanguageTag));
        var vernacular = scratch.ServiceLocator.WritingSystemManager.Get(NewLangProjFixture.VernacularTag);
        Assert.Equal("&z < a", Assert.IsType<IcuRulesCollationDefinition>(vernacular.DefaultCollation).IcuRules);
        Assert.Equal(
            new[] { "'", "a", "z" },
            vernacular.CharacterSets.Single().Characters.OrderBy(character => character, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WriteAsync_SemanticDigestIsIndependentOfArchiveMetadata()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        var firstToken = await new BaselineBundleWriter().WriteAsync(_cache, first, CancellationToken.None);
        File.SetLastWriteTimeUtc(_cache.ProjectId.Path, DateTime.UtcNow.AddMinutes(-10));
        var secondToken = await new BaselineBundleWriter().WriteAsync(_cache, second, CancellationToken.None);

        Assert.Equal(firstToken.SemanticSnapshotDigest, secondToken.SemanticSnapshotDigest);
        Assert.NotEqual(firstToken.BundleDigest, secondToken.BundleDigest);
        Assert.Equal(BaselineSemanticDigest.ProjectionVersion, firstToken.ProjectionVersion);
    }

    [Fact]
    public async Task WriteAsync_ObservesCancellationWithoutCompletingTheArchive()
    {
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BaselineBundleWriter().WriteAsync(_cache, destination, cancellation.Token));
    }

    public void Dispose()
    {
        _cache.Dispose();
        Directory.Delete(_root, true);
    }

    private static string Digest(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return "sha256:" + string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
    }

    private sealed class MaximumWriteSizeStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maximumAllowedWrite;

        public MaximumWriteSizeStream(Stream inner, int maximumAllowedWrite)
        {
            _inner = inner;
            _maximumAllowedWrite = maximumAllowedWrite;
        }

        public int MaximumObservedWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Observe(count);
            _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Observe(count);
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Flush();
            base.Dispose(disposing);
        }

        private void Observe(int count)
        {
            MaximumObservedWrite = Math.Max(MaximumObservedWrite, count);
            if (count > _maximumAllowedWrite)
                throw new InvalidOperationException($"A single write buffered {count} bytes.");
        }
    }
}
