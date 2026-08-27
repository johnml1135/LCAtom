using SIL.Motif.Contract.Responses;
using System;
using System.Collections.Generic;
using System.Linq;
using SIL.Motif.Host.Corpus;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using Xunit;

namespace SIL.Motif.Tests.Projection;

public sealed class CorpusProjectionTests
{
    [Fact]
    public void ListAndDetailText_AreReproducibleFromTheirJsonProjection()
    {
        var corpus = SampleCorpus();

        var store = new StubCorpusStore(corpus);
        var list = CorpusProjectionQuery.List(store);
        var detail = CorpusProjectionQuery.Detail(store, corpus.CorpusId)!;

        var listText = CommandTextRenderer.Render(list);
        var listJson = ProjectionJson.Serialize(list);
        Assert.Contains(corpus.CorpusId, listText);
        Assert.Contains(corpus.CorpusId, listJson);
        Assert.Contains(corpus.Provenance.Origin.Description, listText);
        Assert.Contains(corpus.Provenance.Origin.Description, listJson);
        Assert.Contains("2 document(s); 1 permit derived works", listText);
        Assert.Equal(
            "test-corpus" + Environment.NewLine +
            "  Test corpus" + Environment.NewLine +
            "  2 document(s); 1 permit derived works" + Environment.NewLine +
            "  accuracy figures: permitted" + Environment.NewLine,
            listText);
        Assert.Equal(2, list.Corpora[0].DocumentCount);
        Assert.Equal(1, list.Corpora[0].DerivableDocumentCount);
        FigureAudit.AssertEveryTextFigureAppearsInJson(
            CommandTextRenderer.Render(detail), ProjectionJson.Serialize(detail));
    }

    [Fact]
    public void DetailCarriesEffectiveDocumentLicenceAndCapabilitiesWithoutItsText()
    {
        var corpus = SampleCorpus();
        var detail = CorpusProjectionQuery.Detail(new StubCorpusStore(corpus), corpus.CorpusId)!;

        Assert.Equal("CC-BY-SA-4.0", detail.Documents[0].Licence);
        Assert.True(detail.Documents[0].PermitsDerivedArtefacts);
        Assert.Equal("CC-BY-ND-4.0", detail.Documents[1].Licence);
        Assert.False(detail.Documents[1].PermitsDerivedArtefacts);
        Assert.DoesNotContain("first document text", ProjectionJson.Serialize(detail));
        Assert.DoesNotContain("second document text", ProjectionJson.Serialize(detail));
    }

    [Fact]
    public void DetailTextPreservesTheLegacyCommandOutput()
    {
        var corpus = SampleCorpus();
        var detail = CorpusProjectionQuery.Detail(new StubCorpusStore(corpus), corpus.CorpusId)!;
        var line = Environment.NewLine;
        var expected =
            "Corpus:       test-corpus" + line +
            "Origin:       Test corpus" + line +
            "Location:     https://example.test/corpus" + line +
            "Retrieved:    2026-08-20 12:00:00Z" + line +
            "Licence:      CC-BY-SA-4.0" + line +
            "Tokenisation: test-tokeniser 1" + line +
            "              keeps apostrophes" + line + line +
            "Attested by Reviewer: accuracy figures may be computed." + line + line +
            "Documents (2):" + line +
            "  doc-1  First document" + line +
            $"    19 characters, sha256 {new string('a', 12)}..." + line +
            "    licence: CC-BY-SA-4.0; derived works: permitted" + line +
            "  doc-2  Second document" + line +
            $"    20 characters, sha256 {new string('b', 12)}..." + line +
            "    licence: CC-BY-ND-4.0; derived works: not permitted" + line + line +
            "1 of 2 document(s) in 'test-corpus' permit derived works. " +
            "The rest do not, and are excluded from anything published:" + line +
            "  - Second document: No derived work may be built from 'Second document': its licence forbids " +
            "derivatives (basis: document metadata). Reach and grammar coverage figures over it remain " +
            "permitted, because reading is not deriving." + line;

        Assert.Equal(expected, CommandTextRenderer.Render(detail));
    }

    [Fact]
    public void DetailNormalizesAnEmptyOriginLocationOutOfBothRenderers()
    {
        var corpus = SampleCorpus();
        corpus = corpus with
        {
            Provenance = corpus.Provenance with
            {
                Origin = corpus.Provenance.Origin with { Uri = " " },
            },
        };

        var detail = CorpusProjectionQuery.Detail(new StubCorpusStore(corpus), corpus.CorpusId)!;

        Assert.Null(detail.Uri);
        Assert.DoesNotContain("Location:", CommandTextRenderer.Render(detail));
    }

    [Fact]
    public void EmptyListPreservesTheLegacyTextAndHasAnEmptyJsonCollection()
    {
        var projection = CorpusProjectionQuery.List(new StubCorpusStore());

        Assert.Equal("No corpora in store." + Environment.NewLine, CommandTextRenderer.Render(projection));
        Assert.Equal(
            "{" + Environment.NewLine + "  \"corpora\": []" + Environment.NewLine + "}",
            ProjectionJson.Serialize(projection));
    }

    private static StoredCorpus SampleCorpus()
    {
        var corpusCapabilities = new LicenceCapabilities(true, true, true, true, "source metadata");
        var origin = new CorpusOrigin(
            "Test corpus", "https://example.test/corpus", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            "CC-BY-SA-4.0", corpusCapabilities);
        var provenance = new CorpusProvenance(
            origin,
            new TokenisationRecord("test-tokeniser", "1", "keeps apostrophes"),
            new CorpusQualification(true, true, "Reviewer", new DateTimeOffset(2026, 8, 20, 13, 0, 0, TimeSpan.Zero), "checked"));

        var first = new CorpusDocument(
            "doc-1", "First document", new DocumentSource.File("first.txt"), "first document text",
            new string('a', 64), new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero));
        var second = new CorpusDocument(
            "doc-2", "Second document", new DocumentSource.File("second.txt"), "second document text",
            new string('b', 64), new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero),
            "CC-BY-ND-4.0", new LicenceCapabilities(true, false, true, true, "document metadata"));

        return new StoredCorpus("test-corpus", provenance, new[] { first, second });
    }

    private sealed class StubCorpusStore(params StoredCorpus[] corpora) : ICorpusStore
    {
        public bool Exists(string corpusId) => corpora.Any(corpus => corpus.CorpusId == corpusId);

        public StoredCorpus? Load(string corpusId) =>
            corpora.FirstOrDefault(corpus => corpus.CorpusId == corpusId);

        public void Save(StoredCorpus corpus) => throw new NotSupportedException();

        public IReadOnlyList<string> List() => corpora.Select(corpus => corpus.CorpusId).ToArray();
    }
}
