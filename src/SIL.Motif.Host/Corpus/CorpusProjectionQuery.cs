using System;
using System.Linq;
using SIL.Motif.Projection;

namespace SIL.Motif.Host.Corpus;

/// <summary>Queries stored Corpora and shapes the results for the shared read surfaces.</summary>
public static class CorpusProjectionQuery
{
    public static CorpusListProjection List(ICorpusStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var corpora = store.List()
            .Select(store.Load)
            .Where(corpus => corpus is not null)
            .Select(corpus => corpus!)
            .Select(corpus => new CorpusListItem(
                corpus.CorpusId,
                corpus.Provenance.Origin.Description,
                corpus.Documents.Count,
                corpus.DocumentsPermittingDerivation().Count,
                corpus.Provenance.SupportsAccuracyClaims))
            .ToList();

        return new CorpusListProjection(corpora);
    }

    public static CorpusDetailProjection? Detail(ICorpusStore store, string corpusId)
    {
        ArgumentNullException.ThrowIfNull(store);

        var corpus = store.Load(corpusId);
        if (corpus is null) return null;

        var origin = corpus.Provenance.Origin;
        var documents = corpus.Documents.Select(document => new CorpusDocumentView(
            document.DocumentId,
            document.Title,
            document.Text.Length,
            document.ContentSha256,
            document.EffectiveLicence(origin),
            document.EffectiveCapabilities(origin).PermitsDerivedArtefacts)).ToList();

        var attestor = corpus.Provenance.Qualification?.Attestor;
        var accuracyStatement = corpus.Provenance.SupportsAccuracyClaims
            ? $"Attested by {attestor}: accuracy figures may be computed."
            : corpus.Provenance.WhyAccuracyIsNotComputable();

        return new CorpusDetailProjection(
            corpus.CorpusId,
            origin.Description,
            string.IsNullOrWhiteSpace(origin.Uri) ? null : origin.Uri,
            origin.RetrievedUtc.ToString("u"),
            origin.Licence,
            corpus.Provenance.Tokenisation.Method,
            corpus.Provenance.Tokenisation.Version,
            string.IsNullOrWhiteSpace(corpus.Provenance.Tokenisation.Notes)
                ? null
                : corpus.Provenance.Tokenisation.Notes,
            corpus.Provenance.SupportsAccuracyClaims,
            attestor,
            accuracyStatement,
            documents,
            corpus.DescribeDerivationRestrictions());
    }
}
