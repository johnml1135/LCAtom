using System;
using System.IO;
using System.Linq;
using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>
/// <see cref="CorpusStoreMigration"/>: a corpus already on disk under <see cref="FileCorpusStore"/> stays
/// readable there, and moving it into <see cref="SqliteCorpusStore"/> is one deliberate, re-runnable step
/// rather than something either store does implicitly.
/// </summary>
public sealed class CorpusStoreMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-corpus-migration-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CorpusProvenance Provenance() => new(
        new CorpusOrigin("eBible, Testlang", null, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero), "CC-BY-SA-4.0"),
        new TokenisationRecord("whitespace-and-punctuation", "1", ""));

    [Fact]
    public void EveryCorpusInTheFileStoreArrivesInTheDatabase_UnchangedAndStillOnDiskAfterwards()
    {
        var fileStore = new FileCorpusStore(Path.Combine(_root, "corpora"));
        fileStore.Save(StoredCorpus.Create("a-corpus", Provenance()).With(
            new CorpusDocument("d1", "First", new DocumentSource.File("a.txt"), "mbali mbali\n",
                "sha256-of-a", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero))));
        fileStore.Save(StoredCorpus.Create("b-corpus", Provenance()));

        var sqliteStore = new SqliteCorpusStore(Path.Combine(_root, "motif.db"));
        var imported = CorpusStoreMigration.ImportInto(fileStore, sqliteStore);

        Assert.Equal(new[] { "a-corpus", "b-corpus" }, imported.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "a-corpus", "b-corpus" }, sqliteStore.List());

        var migrated = sqliteStore.Load("a-corpus")!;
        Assert.Single(migrated.Documents);
        Assert.Equal("mbali mbali\n", migrated.Documents[0].Text);
        Assert.Equal("sha256-of-a", migrated.Documents[0].ContentSha256);

        // The file store is untouched: still there, still readable the same way it always was.
        Assert.True(Directory.Exists(Path.Combine(_root, "corpora")));
        Assert.NotNull(fileStore.Load("a-corpus"));
        Assert.Equal("mbali mbali\n", fileStore.Load("a-corpus")!.Documents[0].Text);
    }

    [Fact]
    public void ReRunningTheImportDoesNotClobberACorpusAlreadyAtTheDestination()
    {
        var fileStore = new FileCorpusStore(Path.Combine(_root, "corpora"));
        fileStore.Save(StoredCorpus.Create("c", Provenance()));

        var sqliteStore = new SqliteCorpusStore(Path.Combine(_root, "motif.db"));
        CorpusStoreMigration.ImportInto(fileStore, sqliteStore);

        // The destination has since moved on independently of the file store.
        sqliteStore.Save(sqliteStore.Load("c")!.With(
            new CorpusDocument("newer", "Newer", new DocumentSource.File("n.txt"), "added after migration\n",
                "sha256-of-newer", new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero))));

        var secondImport = CorpusStoreMigration.ImportInto(fileStore, sqliteStore);

        Assert.Empty(secondImport);   // "c" already exists at the destination, so it is left alone
        Assert.Single(sqliteStore.Load("c")!.Documents);
        Assert.Equal("newer", sqliteStore.Load("c")!.Documents[0].DocumentId);
    }
}
