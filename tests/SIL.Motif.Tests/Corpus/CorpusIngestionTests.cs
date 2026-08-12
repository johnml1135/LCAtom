using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Getting text into Motif: from a file, from a URL, or from the bundle a fetching tool writes.
/// </summary>
/// <remarks>
/// The rules these tests defend, in order of how much damage breaking them would do:
/// <list type="number">
/// <item>Text arrives with its origin and its tokenisation attached, or it does not arrive.</item>
/// <item>What a licence <b>permits</b> is resolved per document, because one eBible pull mixes public domain
/// with No-Derivatives, and "may I build an n-gram model from this" has different answers within one corpus.</item>
/// <item>Order and repetition survive ingestion. Deduplicating on the way in is irreversible and destroys both
/// the frequency ranking and the n-gram sequence.</item>
/// <item>Ids that came from outside cannot escape the store directory.</item>
/// </list>
/// </remarks>
public class CorpusIngestionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "motif-corpus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FileCorpusStore Store() => new(Path.Combine(_root, "corpora"));

    private static CorpusProvenance Provenance(LicenceCapabilities? capabilities = null) => new(
        new CorpusOrigin(
            Description: "eBible, Sena",
            Uri: "https://github.com/BibleNLP/ebible",
            RetrievedUtc: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            Licence: "CC-BY-SA-4.0",
            Capabilities: capabilities),
        new TokenisationRecord("whitespace-and-punctuation", "1", "Splits on whitespace; strips edge punctuation."));

    private string WriteFile(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(_root, "incoming"));
        var path = Path.Combine(_root, "incoming", name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A fetcher that serves canned bytes, so no test depends on a host being reachable.</summary>
    private sealed class FakeFetcher : IContentFetcher
    {
        private readonly Dictionary<string, byte[]> _byUri = new(StringComparer.Ordinal);

        public List<string> Requested { get; } = new();

        public FakeFetcher Serving(string uri, string text)
        {
            _byUri[uri] = Encoding.UTF8.GetBytes(text);
            return this;
        }

        public Task<byte[]> FetchAsync(DocumentSource source, CancellationToken cancellationToken = default)
        {
            Requested.Add(source.Describe());

            return source switch
            {
                DocumentSource.Url url when _byUri.TryGetValue(url.Uri.ToString(), out var bytes) =>
                    Task.FromResult(bytes),
                DocumentSource.File file => Task.FromResult(File.ReadAllBytes(file.Path)),
                _ => throw new IOException($"Nothing served at '{source.Describe()}'."),
            };
        }
    }

    // ---------------------------------------------------------------- the ordinary path

    [Fact]
    public async Task AFileBecomesADocument_WithItsBytesHashedAsTheyArrived()
    {
        var path = WriteFile("sehNT.txt", "Mbali ninga mbali.\nNinga mbali.\n");
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());

        ingestion.AddCorpus("ebible-seh", Provenance());
        var document = await ingestion.AddDocumentAsync(
            "ebible-seh",
            new DocumentSource.File(path),
            new DocumentMetadata("sehNT", "Sena New Testament"));

        Assert.Equal("Mbali ninga mbali.\nNinga mbali.\n", document.Text);

        // Hash of the exact bytes, so a source that changes under us is detectable.
        Assert.Equal(64, document.ContentSha256.Length);
        Assert.Equal(document.ContentSha256, document.ContentSha256.ToLowerInvariant());
    }

    [Fact]
    public async Task AUrlIsRetrievedThroughTheFetcher_AndTheLocationIsKept()
    {
        var fetcher = new FakeFetcher().Serving("https://example.org/seh.txt", "mbali\nnyumba\n");
        var ingestion = new CorpusIngestion(Store(), fetcher);

        ingestion.AddCorpus("ebible-seh", Provenance());
        var document = await ingestion.AddDocumentAsync(
            "ebible-seh",
            DocumentSource.Parse("https://example.org/seh.txt"),
            new DocumentMetadata("seh", "Sena"));

        Assert.Equal("mbali\nnyumba\n", document.Text);
        Assert.Contains("https://example.org/seh.txt", fetcher.Requested);

        // Recording where it came from is the whole point; losing it would make the record unreproducible.
        Assert.Equal("https://example.org/seh.txt", document.Source.Describe());
    }

    [Fact]
    public void ParseTellsAUrlFromAPath_AndDoesNotTreatExoticSchemesAsSomethingToFetch()
    {
        Assert.IsType<DocumentSource.Url>(DocumentSource.Parse("https://example.org/a.txt"));
        Assert.IsType<DocumentSource.Url>(DocumentSource.Parse("http://example.org/a.txt"));
        Assert.IsType<DocumentSource.File>(DocumentSource.Parse(@"C:\data\a.txt"));
        Assert.IsType<DocumentSource.File>(DocumentSource.Parse("./data/a.txt"));

        // ftp: and file: are paths-that-will-fail, not fetches-that-will-be-attempted.
        Assert.IsType<DocumentSource.File>(DocumentSource.Parse("ftp://example.org/a.txt"));
    }

    // ---------------------------------------------------------------- refusals that protect stored work

    [Fact]
    public void ACorpusIsNotSilentlyRecreated()
    {
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("ebible-seh", Provenance());

        var ex = Assert.Throws<InvalidOperationException>(() => ingestion.AddCorpus("ebible-seh", Provenance()));

        // The reason matters: replacing the corpus would leave Assessments pointing at text no longer there.
        Assert.Contains("orphan", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADocumentIdIsNotSilentlyOverwritten()
    {
        var path = WriteFile("a.txt", "mbali\n");
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("c", Provenance());

        await ingestion.AddDocumentAsync("c", new DocumentSource.File(path), new DocumentMetadata("d1", "First"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingestion.AddDocumentAsync("c", new DocumentSource.File(path), new DocumentMetadata("d1", "Second")));

        Assert.Contains("already has a document", ex.Message);

        // And the first document is still intact — the refusal happened before anything was written.
        var corpus = Store().Load("c")!;
        Assert.Single(corpus.Documents);
        Assert.Equal("First", corpus.Documents[0].Title);
    }

    [Fact]
    public async Task ADocumentCannotArriveBeforeItsCorpus()
    {
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ingestion.AddDocumentAsync("nope", new DocumentSource.File("x.txt"), new DocumentMetadata("d", "D")));

        // The corpus carries origin and tokenisation; letting text land first means back-filling provenance later.
        Assert.Contains("tokenisation record", ex.Message);
    }

    // ---------------------------------------------------------------- licence capabilities

    [Fact]
    public async Task OneEBiblePullMixesLicences_AndOnlyThePermittedDocumentsFeedDerivedWork()
    {
        var noDerivatives = WriteFile("nd.txt", "mbali\n");
        var publicDomain = WriteFile("pd.txt", "nyumba\n");

        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("ebible-mixed", Provenance(LicenceCapabilities.Unknown("eBible licences.tsv")));

        await ingestion.AddDocumentAsync("ebible-mixed", new DocumentSource.File(noDerivatives),
            new DocumentMetadata("sehNT", "Sena NT", "CC-BY-NC-ND-4.0",
                new LicenceCapabilities(MayRedistribute: true, MayDerive: false, MayUseCommercially: false,
                    RequiresAttribution: true, Basis: "eBible licences.tsv")));

        await ingestion.AddDocumentAsync("ebible-mixed", new DocumentSource.File(publicDomain),
            new DocumentMetadata("sehPD", "Sena, public domain", "public domain",
                new LicenceCapabilities(MayRedistribute: true, MayDerive: true, MayUseCommercially: true,
                    RequiresAttribution: false, Basis: "eBible licences.tsv")));

        var corpus = Store().Load("ebible-mixed")!;

        // Reach is measured over everything — reading is not deriving.
        Assert.Equal(2, corpus.Documents.Count);

        // An n-gram model may only be built from the one that permits it.
        var derivable = corpus.DocumentsPermittingDerivation();
        Assert.Equal(new[] { "sehPD" }, derivable.Select(d => d.DocumentId));

        var explanation = corpus.DescribeDerivationRestrictions();
        Assert.Contains("1 of 2", explanation);
        Assert.Contains("Sena NT", explanation);
    }

    [Fact]
    public void UnknownIsNotPermission_AndSaysSomethingDifferentFromForbidden()
    {
        var unknown = LicenceCapabilities.Unknown("nobody checked");
        var forbidden = new LicenceCapabilities(true, false, false, true, "eBible licences.tsv");

        Assert.False(unknown.PermitsDerivedArtefacts);
        Assert.False(forbidden.PermitsDerivedArtefacts);

        // Same outcome, different instruction to the reader: go and find the licence, versus stop.
        Assert.Contains("nobody has established", unknown.WhyDerivedArtefactsAreNotPermitted("X"));
        Assert.Contains("forbids derivatives", forbidden.WhyDerivedArtefactsAreNotPermitted("X"));

        // Both must say what the corpus is still good for, or a reader concludes it is useless.
        Assert.Contains("reading is not deriving", unknown.WhyDerivedArtefactsAreNotPermitted("X"));
        Assert.Contains("reading is not deriving", forbidden.WhyDerivedArtefactsAreNotPermitted("X"));
    }

    [Fact]
    public async Task ADocumentsCapabilitiesOverrideTheCorpus_TheyDoNotMergeWithIt()
    {
        var path = WriteFile("a.txt", "mbali\n");
        var permissiveCorpus = new LicenceCapabilities(true, MayDerive: true, true, false, "corpus-level claim");

        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("c", Provenance(permissiveCorpus));

        await ingestion.AddDocumentAsync("c", new DocumentSource.File(path),
            new DocumentMetadata("d", "D", "CC-BY-ND-4.0",
                new LicenceCapabilities(MayRedistribute: true, MayDerive: false, MayUseCommercially: null,
                    RequiresAttribution: true, Basis: "eBible licences.tsv")));

        var corpus = Store().Load("c")!;
        var effective = corpus.Documents[0].EffectiveCapabilities(corpus.Provenance.Origin);

        // Field-by-field merging would let the corpus's MayDerive:true leak into the document; override wholesale.
        Assert.False(effective.MayDerive);
        Assert.Null(effective.MayUseCommercially);
        Assert.Empty(corpus.DocumentsPermittingDerivation());
    }

    [Fact]
    public void DemandingPermissionThrowsWithTheExplanation_ForCallSitesThatMustNotProceed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LicenceCapabilities.Unknown("nobody checked").DemandDerivationIsPermitted("seh-wikipedia"));

        Assert.Contains("seh-wikipedia", ex.Message);
    }

    // ---------------------------------------------------------------- what must survive storage

    [Fact]
    public async Task OrderAndRepetitionSurvive_BecauseFrequencyAndSequenceAreWhatTheOtherConsumersNeed()
    {
        var path = WriteFile("a.txt", "mbali mbali nyumba\nmbali\n");
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("c", Provenance());
        await ingestion.AddDocumentAsync("c", new DocumentSource.File(path), new DocumentMetadata("d", "D"));

        var text = Store().Load("c")!.Documents[0].Text;

        // CorpusDescriptor sorts/dedupes and is right to; a Document must not — "mbali" three times is the data.
        Assert.Equal("mbali mbali nyumba\nmbali\n", text);
        Assert.Equal(3, text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w == "mbali"));
    }

    [Fact]
    public async Task EverythingRecordedAtIngestionComesBackOutOfTheStore()
    {
        var path = WriteFile("a.txt", "mbali\n");
        var attributes = new Dictionary<string, string>
        {
            ["copyrightHolder"] = "Wycliffe Bible Translators",
            ["isoCode"] = "seh",
        };

        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("c", Provenance(new LicenceCapabilities(true, true, false, true, "eBible licences.tsv")));
        var written = await ingestion.AddDocumentAsync("c", new DocumentSource.File(path),
            new DocumentMetadata("d", "Sena NT", "CC-BY-SA-4.0", null, attributes));

        var corpus = Store().Load("c")!;
        var read = corpus.Documents[0];

        Assert.Equal(written.ContentSha256, read.ContentSha256);
        Assert.Equal(written.IngestedUtc, read.IngestedUtc);
        Assert.Equal("Sena NT", read.Title);
        Assert.Equal("CC-BY-SA-4.0", read.Licence);
        Assert.Equal("Wycliffe Bible Translators", read.Attributes!["copyrightHolder"]);

        // Provenance is the reason the corpus exists as an object rather than a folder of files.
        Assert.Equal("eBible, Sena", corpus.Provenance.Origin.Description);
        Assert.Equal("whitespace-and-punctuation", corpus.Provenance.Tokenisation.Method);
        Assert.Equal("eBible licences.tsv", corpus.Provenance.Origin.Capabilities!.Basis);

        // Unattested, so still no accuracy figures — storage must not quietly upgrade that.
        Assert.False(corpus.Provenance.SupportsAccuracyClaims);
    }

    [Fact]
    public async Task AnIdFromOutsideCannotEscapeTheStoreDirectory()
    {
        var path = WriteFile("a.txt", "mbali\n");
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());

        ingestion.AddCorpus("../../escaped", Provenance());
        await ingestion.AddDocumentAsync("../../escaped", new DocumentSource.File(path),
            new DocumentMetadata("../../also-escaped", "D"));

        // Ids come from other programs' bundles; writes stay under the store root, but ids are preserved, not mangled.
        var written = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.All(written, f => Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(f), StringComparison.Ordinal));

        var corpus = Store().Load("../../escaped")!;
        Assert.Equal("../../escaped", corpus.CorpusId);
        Assert.Equal("../../also-escaped", corpus.Documents[0].DocumentId);
    }

    [Fact]
    public async Task ListingShowsWhatIsStored()
    {
        var path = WriteFile("a.txt", "mbali\n");
        var ingestion = new CorpusIngestion(Store(), new FakeFetcher());
        ingestion.AddCorpus("b-corpus", Provenance());
        ingestion.AddCorpus("a-corpus", Provenance());
        await ingestion.AddDocumentAsync("a-corpus", new DocumentSource.File(path), new DocumentMetadata("d", "D"));

        Assert.Equal(new[] { "a-corpus", "b-corpus" }, Store().List());
        Assert.Null(Store().Load("no-such-corpus"));
    }
}
