using System.IO.Compression;
using System.Security.Cryptography;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Worker.Baselines;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class BaselineBundleReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SIL.Motif.BaselineReceiverTests",
        Guid.NewGuid().ToString("N"));

    public BaselineBundleReceiverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PublishVerifiedAsync_PublishesAllowedLayoutAndDeletesTransport()
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        var target = Target();
        var token = Token(transfer.Sha256);

        var publication = await new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, token, target, CancellationToken.None);

        Assert.False(File.Exists(transfer.TemporaryPath));
        Assert.True(publication.Created);
        Assert.Equal(Path.Combine(publication.RootDirectory, "project.fwdata"), publication.FwDataPath);
        Assert.Equal("model", File.ReadAllText(publication.FwDataPath));
        Assert.Equal("<ldml/>", File.ReadAllText(Path.Combine(
            publication.RootDirectory, "WritingSystemStore", "en.ldml")));
    }

    [Fact]
    public async Task PublishVerifiedAsync_IsIdempotentForTheSameBundleDigest()
    {
        var first = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        var target = Target();
        var receiver = new BaselineBundleReceiver();
        var token = Token(first.Sha256);
        var published = await receiver.PublishVerifiedAsync(first, token, target, CancellationToken.None);
        var second = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));

        var retried = await receiver.PublishVerifiedAsync(second, token, target, CancellationToken.None);

        Assert.Equal(published.RootDirectory, retried.RootDirectory);
        Assert.False(retried.Created);
        Assert.False(File.Exists(second.TemporaryPath));
    }

    [Theory]
    [InlineData("../escape.fwdata")]
    [InlineData("LinkedFiles/media.wav")]
    [InlineData("project.motif.db")]
    [InlineData("project.fwbackup")]
    [InlineData("unrelated.txt")]
    [InlineData("WritingSystemStore/nested/en.ldml")]
    [InlineData("/absolute.fwdata")]
    [InlineData("C:/absolute.fwdata")]
    [InlineData("WritingSystemStore\\en.ldml")]
    public async Task PublishVerifiedAsync_RejectsEntriesOutsideTheExactAllowlist(string entry)
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"), (entry, "forbidden"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, Token(transfer.Sha256), Target(),
            CancellationToken.None));

        Assert.False(File.Exists(transfer.TemporaryPath));
        Assert.False(File.Exists(Path.Combine(_root, "escape.fwdata")));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsCaseCollidingEntries()
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "one"), ("WritingSystemStore/EN.LDML", "two"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, Token(transfer.Sha256), Target(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsArchiveLinkMetadata()
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        using (var stream = new FileStream(transfer.TemporaryPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
            archive.GetEntry("WritingSystemStore/en.ldml")!.ExternalAttributes = 0xA1FF << 16;
        transfer = VerifiedTransfer(transfer.TemporaryPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, Token(transfer.Sha256), Target(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_EnforcesEntryAndExpansionBounds()
    {
        var tooMany = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "one"), ("WritingSystemStore/fr.ldml", "two"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver(2).PublishVerifiedAsync(
            tooMany, Token(tooMany.Sha256), Target(), CancellationToken.None));

        var tooLarge = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver(
            maximumExtractedBytes: 8).PublishVerifiedAsync(
            tooLarge, Token(tooLarge.Sha256), Target(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsAChangedExistingPublication()
    {
        var first = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        var receiver = new BaselineBundleReceiver();
        var token = Token(first.Sha256);
        var publication = await receiver.PublishVerifiedAsync(first, token, Target(), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(publication.RootDirectory, "WritingSystemStore", "nested"));
        var retry = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));

        await Assert.ThrowsAsync<InvalidDataException>(() => receiver.PublishVerifiedAsync(
            retry, token, Target(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsAReparsePointInAnExistingPublication()
    {
        var first = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        var receiver = new BaselineBundleReceiver();
        var token = Token(first.Sha256);
        var publication = await receiver.PublishVerifiedAsync(first, token, Target(), CancellationToken.None);
        var writingSystems = Path.Combine(publication.RootDirectory, "WritingSystemStore");
        Directory.Delete(writingSystems, true);
        var outside = Path.Combine(_root, "outside-writing-systems");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "en.ldml"), "outside");
        try { Directory.CreateSymbolicLink(writingSystems, outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            return;
        }
        var retry = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));

        await Assert.ThrowsAsync<InvalidDataException>(() => receiver.PublishVerifiedAsync(
            retry, token, Target(), CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(outside, "en.ldml")));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsAnotherProjectIdentity()
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, Token(transfer.Sha256),
            new BaselinePublicationTarget(Path.Combine(_root, "managed"), "other-project"),
            CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RequiresOneFwDataAndAtLeastOneWritingSystem()
    {
        var noWritingSystem = CreateTransfer(("project.fwdata", "model"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            noWritingSystem, Token(noWritingSystem.Sha256),
            Target(), CancellationToken.None));

        var twoProjects = CreateTransfer(("first.fwdata", "one"), ("second.fwdata", "two"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            twoProjects, Token(twoProjects.Sha256),
            Target(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsDeclaredBundleDigestMismatch()
    {
        var transfer = CreateTransfer(("project.fwdata", "model"),
            ("WritingSystemStore/en.ldml", "<ldml/>"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineBundleReceiver().PublishVerifiedAsync(
            transfer, Token(new string('0', 64)),
            Target(), CancellationToken.None));

        Assert.False(File.Exists(transfer.TemporaryPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private VerifiedBinaryTransfer CreateTransfer(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ready");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(item.Content);
            }
        }
        return VerifiedTransfer(path);
    }

    private static VerifiedBinaryTransfer VerifiedTransfer(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new VerifiedBinaryTransfer(Path.GetFileNameWithoutExtension(path), path, bytes.Length, sha256);
    }

    private static BaselineToken Token(string bundleDigest) => new(
        "project-id", "sha256:" + new string('1', 64), "projection-v1", "2026-08-23T00:00:00Z",
        "sha256:" + bundleDigest);

    private BaselinePublicationTarget Target() => new(Path.Combine(_root, "managed"), "project-id");
}
