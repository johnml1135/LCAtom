using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// The handoff format: what an external fetching tool writes, and Motif reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam these tests hold in place.</b> linguistic-assistant knows how to pull eBible and OPUS, strip
/// their markup, and find the per-translation licence rows. Motif knows none of that. A bundle is the
/// paperwork that travels with the text, and it is the only thing on this seam — so the failure modes worth
/// testing are all "the bundle claimed something that was not true", because those are what turn into a
/// published figure over text nobody can account for.
/// </para>
/// <para>
/// A bundle <i>names</i> files rather than containing them, which is why relative-path resolution is tested
/// as carefully as the parsing: a bundle that resolves against the current working directory works on the
/// machine that wrote it and fails everywhere else, and fails by finding nothing rather than by finding the
/// wrong thing — so it is only caught the first time somebody copies the folder.
/// </para>
/// </remarks>
public class CorpusBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "motif-bundle-tests", Guid.NewGuid().ToString("N"));

    public CorpusBundleTests() => Directory.CreateDirectory(Path.Combine(_root, "handoff"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string HandoffDirectory => Path.Combine(_root, "handoff");

    private string WriteBundle(string json)
    {
        var path = Path.Combine(HandoffDirectory, "bundle.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private void WriteText(string name, string text) =>
        File.WriteAllText(Path.Combine(HandoffDirectory, name), text, new UTF8Encoding(false));

    private const string EBibleBundle = """
    {
      "corpusId": "ebible-seh",
      "origin": {
        "description": "eBible, Sena translations",
        "uri": "https://github.com/BibleNLP/ebible",
        "retrievedUtc": "2026-08-09T10:30:00Z",
        "licence": "mixed; see per-document",
        "capabilities": { "mayRedistribute": true, "mayDerive": false, "requiresAttribution": true,
                          "basis": "eBible licences.tsv" }
      },
      "tokenisation": {
        "method": "SIL.Machine LatinWordTokenizer",
        "version": "3.6.2",
        "notes": "Verse-per-line input; punctuation split off; digits kept."
      },
      "documents": [
        { "documentId": "sehNT", "title": "Sena New Testament", "source": "sehNT.txt",
          "licence": "CC-BY-NC-ND-4.0",
          "attributes": { "copyrightHolder": "Wycliffe Bible Translators", "isoCode": "seh" } },
        { "documentId": "sehPD", "title": "Sena, public domain", "source": "sehPD.txt",
          "licence": "public domain",
          "capabilities": { "mayRedistribute": true, "mayDerive": true, "mayUseCommercially": true,
                            "requiresAttribution": false, "basis": "eBible licences.tsv" } }
      ]
    }
    """;

    [Fact]
    public async Task AnEBibleHandoffIngestsWholesale_WithLicencesResolvedPerDocument()
    {
        WriteText("sehNT.txt", "Mbali ninga mbali.\n");
        WriteText("sehPD.txt", "Nyumba ikulu.\n");
        var bundlePath = WriteBundle(EBibleBundle);

        var store = new FileCorpusStore(Path.Combine(_root, "corpora"));
        var corpus = await new CorpusIngestion(store).AddBundleAsync(CorpusBundle.ReadFile(bundlePath));

        Assert.Equal("ebible-seh", corpus.CorpusId);
        Assert.Equal(new[] { "sehNT", "sehPD" }, corpus.Documents.Select(d => d.DocumentId));
        Assert.Equal("Mbali ninga mbali.\n", corpus.Documents[0].Text);

        // The whole point of the per-document licence: one pull, two answers to "may I derive from this".
        Assert.Equal(new[] { "sehPD" }, corpus.DocumentsPermittingDerivation().Select(d => d.DocumentId));

        // sehNT states no capabilities, so it inherits the corpus's — which forbid derivation.
        Assert.False(corpus.Documents[0].EffectiveCapabilities(corpus.Provenance.Origin).PermitsDerivedArtefacts);

        // Facts the fetching tool knew and Motif has no field for survive verbatim.
        Assert.Equal("Wycliffe Bible Translators", corpus.Documents[0].Attributes!["copyrightHolder"]);

        // Nobody attested the text, so no accuracy figure may be computed — a bundle cannot grant that.
        Assert.False(corpus.Provenance.SupportsAccuracyClaims);
    }

    [Fact]
    public void RelativePathsResolveAgainstTheBundle_NotTheWorkingDirectory()
    {
        WriteText("sehNT.txt", "x\n");
        WriteText("sehPD.txt", "y\n");
        var bundlePath = WriteBundle(EBibleBundle);

        var previous = Directory.GetCurrentDirectory();
        try
        {
            // Run from somewhere else: a bundle that only works from its own folder breaks under any script.
            Directory.SetCurrentDirectory(Path.GetTempPath());
            var bundle = CorpusBundle.ReadFile(bundlePath);

            var source = Assert.IsType<DocumentSource.File>(bundle.Documents[0].Source);
            Assert.True(Path.IsPathRooted(source.Path));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(HandoffDirectory, "sehNT.txt")),
                Path.GetFullPath(source.Path));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void AUrlInABundleStaysAUrl()
    {
        var bundle = CorpusBundle.Read(System.Text.Json.JsonDocument.Parse("""
        {
          "corpusId": "opus-seh",
          "origin": { "description": "OPUS bible-uedin", "retrievedUtc": "2026-08-09T00:00:00Z" },
          "tokenisation": { "method": "opustools", "version": "1.6.2" },
          "documents": [ { "documentId": "d", "source": "https://opus.nlpl.eu/download/x.txt" } ]
        }
        """).RootElement, HandoffDirectory);

        Assert.IsType<DocumentSource.Url>(bundle.Documents[0].Source);

        // A document with no title is named after itself, not rejected — the id is always a usable name.
        Assert.Equal("d", bundle.Documents[0].Metadata.Title);
    }

    // ---------------------------------------------------------------- what a bundle may not omit

    [Theory]
    [InlineData("origin", "cannot be published from safely")]
    [InlineData("tokenisation", "not reproducible")]
    public void TheTwoThingsThatMakeAFigureMeaningfulAreRequired(string omit, string expectedReason)
    {
        var json = """
        {
          "corpusId": "c",
          "origin": { "description": "d", "retrievedUtc": "2026-08-09T00:00:00Z" },
          "tokenisation": { "method": "m", "version": "1" },
          "documents": [ { "documentId": "d1", "source": "a.txt" } ]
        }
        """.Replace($"\"{omit}\"", "\"omitted\"", StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() =>
            CorpusBundle.Read(System.Text.Json.JsonDocument.Parse(json).RootElement, HandoffDirectory));

        // The message says why: a fetching tool's author has to fix this and will not be reading the ADR.
        Assert.Contains(expectedReason, ex.Message);
    }

    [Fact]
    public void ABundleThatNamesTheSameDocumentTwiceIsRejectedBeforeAnythingIsWritten()
    {
        var json = """
        {
          "corpusId": "c",
          "origin": { "description": "d", "retrievedUtc": "2026-08-09T00:00:00Z" },
          "tokenisation": { "method": "m", "version": "1" },
          "documents": [ { "documentId": "d1", "source": "a.txt" },
                         { "documentId": "d1", "source": "b.txt" } ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() =>
            CorpusBundle.Read(System.Text.Json.JsonDocument.Parse(json).RootElement, HandoffDirectory));

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void ACapabilityBlockWithoutABasisIsRejected()
    {
        var json = """
        {
          "corpusId": "c",
          "origin": { "description": "d", "retrievedUtc": "2026-08-09T00:00:00Z",
                      "capabilities": { "mayDerive": true } },
          "tokenisation": { "method": "m", "version": "1" },
          "documents": [ { "documentId": "d1", "source": "a.txt" } ]
        }
        """;

        // An unsourced permission claim is worse than no claim: it looks like somebody checked.
        var ex = Assert.Throws<InvalidDataException>(() =>
            CorpusBundle.Read(System.Text.Json.JsonDocument.Parse(json).RootElement, HandoffDirectory));

        Assert.Contains("basis", ex.Message);
    }

    [Fact]
    public async Task AMissingFileFailsAtIngestion_NamingTheFile()
    {
        WriteText("sehNT.txt", "x\n");     // sehPD.txt deliberately absent
        var bundlePath = WriteBundle(EBibleBundle);

        var store = new FileCorpusStore(Path.Combine(_root, "corpora"));
        var ingestion = new CorpusIngestion(store);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ingestion.AddBundleAsync(CorpusBundle.ReadFile(bundlePath)));

        Assert.Contains("sehPD.txt", ex.Message);

        // Partial ingestion is visible, not hidden: the document that arrived is there; the fix is add-the-rest.
        var corpus = store.Load("ebible-seh")!;
        Assert.Single(corpus.Documents);
        Assert.Equal("sehNT", corpus.Documents[0].DocumentId);
    }

    [Fact]
    public void AQualificationInABundleIsCarriedThrough_AndIsWhatUnlocksAccuracy()
    {
        var json = """
        {
          "corpusId": "c",
          "origin": { "description": "Curated Sena wordlist", "retrievedUtc": "2026-08-09T00:00:00Z" },
          "tokenisation": { "method": "m", "version": "1" },
          "qualification": { "knownClean": true, "inScope": true, "attestor": "A. Linguist",
                             "attestedUtc": "2026-08-09T12:00:00Z",
                             "note": "Checked every form against the lexicon." },
          "documents": [ { "documentId": "d1", "source": "a.txt" } ]
        }
        """;

        var bundle = CorpusBundle.Read(System.Text.Json.JsonDocument.Parse(json).RootElement, HandoffDirectory);

        Assert.True(bundle.Provenance.SupportsAccuracyClaims);
        Assert.Equal("A. Linguist", bundle.Provenance.Qualification!.Attestor);
    }
}
