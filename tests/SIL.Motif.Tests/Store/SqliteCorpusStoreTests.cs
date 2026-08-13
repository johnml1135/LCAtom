using System;
using System.IO;
using System.Linq;
using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Store;

/// <summary>
/// <see cref="SqliteCorpusStore"/> round-trips a Corpus through the embedded database ADR 0036 decision 6
/// assigns it to, and its aggregate reads (<see cref="ICorpusStore.List"/>, <see cref="ICorpusStore.Exists"/>)
/// touch a table that carries no document text at all.
/// </summary>
public sealed class SqliteCorpusStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-sqlite-corpus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SqliteCorpusStore Store() => new(Path.Combine(_root, "motif.db"));

    private static CorpusProvenance Provenance() => new(
        new CorpusOrigin(
            Description: "eBible, Testlang",
            Uri: "https://github.com/BibleNLP/ebible",
            RetrievedUtc: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            Licence: "CC-BY-SA-4.0",
            Capabilities: new LicenceCapabilities(true, true, false, true, "eBible licences.tsv")),
        new TokenisationRecord("whitespace-and-punctuation", "1", "Splits on whitespace."),
        new CorpusQualification(true, true, "A. Linguist", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero), "Hand-checked."));

    private static CorpusDocument Document(string id, string text) => new(
        DocumentId: id,
        Title: "Testlang " + id,
        Source: new DocumentSource.File(@"C:\incoming\" + id + ".txt"),
        Text: text,
        ContentSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
        IngestedUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
        Licence: "CC-BY-SA-4.0",
        Capabilities: new LicenceCapabilities(true, false, false, true, "eBible licences.tsv"),
        Attributes: new System.Collections.Generic.Dictionary<string, string> { ["isoCode"] = "tst" });

    [Fact]
    public void ACorpusRoundTripsThroughTheDatabase_EveryFieldIntact()
    {
        var store = Store();
        var corpus = StoredCorpus.Create("ebible-tst", Provenance())
            .With(Document("d1", "mbali mbali nyumba\nmbali\n"))
            .With(Document("d2", "nyumba kubwa\n"));

        store.Save(corpus);
        var loaded = store.Load("ebible-tst");

        Assert.NotNull(loaded);
        Assert.Equal(corpus.CorpusId, loaded!.CorpusId);
        Assert.Equal(corpus.Provenance.Origin.Description, loaded.Provenance.Origin.Description);
        Assert.Equal(corpus.Provenance.Origin.Capabilities!.Basis, loaded.Provenance.Origin.Capabilities!.Basis);
        Assert.Equal(corpus.Provenance.Qualification!.Attestor, loaded.Provenance.Qualification!.Attestor);
        Assert.True(loaded.Provenance.SupportsAccuracyClaims);

        Assert.Equal(2, loaded.Documents.Count);
        Assert.Equal(new[] { "d1", "d2" }, loaded.Documents.Select(d => d.DocumentId));

        // Order and repetition of the running text must survive storage, exactly as FileCorpusStore preserves them.
        Assert.Equal("mbali mbali nyumba\nmbali\n", loaded.Documents[0].Text);
        Assert.Equal(3, loaded.Documents[0].Text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Count(w => w == "mbali"));

        Assert.Equal(corpus.Documents[0].ContentSha256, loaded.Documents[0].ContentSha256);
        Assert.Equal(corpus.Documents[0].IngestedUtc, loaded.Documents[0].IngestedUtc);
        Assert.Equal("tst", loaded.Documents[0].Attributes!["isoCode"]);
        Assert.False(loaded.Documents[0].EffectiveCapabilities(loaded.Provenance.Origin).MayDerive);
    }

    [Fact]
    public void ListingAndExistenceReadOnlyTheCorporaTable_NeverTheDocumentText()
    {
        var store = Store();
        // A half-megabyte document: List()/Exists() must stay cheap without reading CorpusDocuments at all.
        var corpus = StoredCorpus.Create("c", Provenance()).With(Document("d", new string('x', 500_000)));
        store.Save(corpus);

        Assert.Equal(new[] { "c" }, store.List());
        Assert.True(store.Exists("c"));
        Assert.False(store.Exists("no-such-corpus"));
    }

    [Fact]
    public void LoadingAMissingCorpusReturnsNull()
    {
        Assert.Null(Store().Load("nope"));
    }

    [Fact]
    public void SavingAgainReplacesRatherThanDuplicates()
    {
        var store = Store();
        var corpus = StoredCorpus.Create("c", Provenance()).With(Document("d1", "one\n"));
        store.Save(corpus);

        var extended = corpus.With(Document("d2", "two\n"));
        store.Save(extended);

        var loaded = store.Load("c")!;
        Assert.Equal(new[] { "d1", "d2" }, loaded.Documents.Select(d => d.DocumentId));
        Assert.Equal(new[] { "c" }, store.List());
    }
}
