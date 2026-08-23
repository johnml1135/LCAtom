using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.LcmUtils;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Model.Snapshot;
using SIL.Motif.Runner.Snapshotting;
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
    private readonly SeededProject _seed;

    public BaselineBundleTests()
    {
        Directory.CreateDirectory(_root);
        _cache = NewLangProjFixture.CreateCache(_root);
        _seed = SeededProject.Seed(_cache);

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
        var expectedNames = new[] { NewLangProjFixture.ProjectName + ".fwdata" }
            .Concat(Directory.EnumerateFiles(Path.Combine(projectFolder, "WritingSystemStore"), "*.ldml")
                .Select(path => "WritingSystemStore/" + Path.GetFileName(path)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames, names);
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
        Assert.Equal(BaselineSemanticDigest.Compute(_cache), BaselineSemanticDigest.Compute(scratch));
        Assert.Equal(ModelIdentitySet(_cache), ModelIdentitySet(scratch));
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
    public void SemanticDigest_HashesTheRfc8785CanonicalAggregate()
    {
        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
        {
            var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(_seed.FirstEntryId);
            entry.CitationForm.set_String(_cache.DefaultVernWs, "control\u000fcharacter");
        });

        var snapshots = ProjectSnapshots(_cache);
        var expectedJson = ObjectSnapshotJsonWriter.WriteJson(snapshots);
        var expectedCanonical = CanonicalJson.CanonicalizeToUtf8(expectedJson);
        var expectedDigest = Digest(expectedCanonical);

        Assert.Equal(expectedDigest, BaselineSemanticDigest.Compute(_cache));
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

    [Fact]
    public void SemanticDigest_ObservesCancellationDuringProjection()
    {
        using var cancellation = new CancellationTokenSource();
        var projected = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => BaselineSemanticDigest.Compute(
            _cache,
            cancellation.Token,
            _ =>
            {
                if (++projected == 2) cancellation.Cancel();
            }));
        Assert.Equal(2, projected);
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

    private static IReadOnlyList<ObjectSnapshot> ProjectSnapshots(LcmCache cache)
    {
        var methods = typeof(LexEntrySnapshotter).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.Name == "Snapshot" && method.ReturnType == typeof(ObjectSnapshot))
            .Where(method => method.GetParameters() is { Length: 2 } parameters &&
                parameters[0].ParameterType == typeof(LcmCache))
            .ToArray();
        var snapshots = new List<ObjectSnapshot>();
        foreach (var value in cache.ServiceLocator.GetInstance<ICmObjectRepository>().AllInstances())
        {
            var fields = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            var applicable = methods.Where(candidate =>
                candidate.GetParameters()[1].ParameterType.IsInstanceOfType(value)).ToArray();
            foreach (var method in applicable)
            {
                var part = (ObjectSnapshot)method.Invoke(null, new object[] { cache, value })!;
                foreach (var field in part.AlternativesFields) fields.Add(field.Key, field.Value);
            }
            if (applicable.Length > 0)
                snapshots.Add(new ObjectSnapshot(CanonicalId.FromGuid(value.Guid), fields));
        }
        return snapshots.OrderBy(snapshot => snapshot.CanonicalId.Value, StringComparer.Ordinal).ToArray();
    }

    private static string[] ModelIdentitySet(LcmCache cache) =>
        cache.ServiceLocator.GetInstance<ICmObjectRepository>().AllInstances()
            .Select(value => CanonicalId.FromGuid(value.Guid).Value + ":" + value.ClassID)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

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
