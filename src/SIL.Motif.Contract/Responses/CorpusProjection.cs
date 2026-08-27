using System.Collections.Generic;

namespace SIL.Motif.Contract.Responses;

/// <summary>One row of the <c>corpora</c> report.</summary>
public sealed record CorpusListItem(
    string CorpusId,
    string Description,
    int DocumentCount,
    int DerivableDocumentCount,
    bool SupportsAccuracyClaims);

/// <summary>The <c>corpora</c> report: every Corpus currently in the Motif store.</summary>
public sealed record CorpusListProjection(IReadOnlyList<CorpusListItem> Corpora);

/// <summary>One Document in the <c>show-corpus</c> report, without its stored text.</summary>
public sealed record CorpusDocumentView(
    string DocumentId,
    string Title,
    int CharacterCount,
    string ContentSha256,
    string? Licence,
    bool PermitsDerivedArtefacts);

/// <summary>The <c>show-corpus</c> report: provenance, tokenisation, permissions, and Documents.</summary>
public sealed record CorpusDetailProjection(
    string CorpusId,
    string Description,
    string? Uri,
    string RetrievedUtc,
    string? Licence,
    string Tokeniser,
    string TokeniserVersion,
    string? TokeniserNotes,
    bool SupportsAccuracyClaims,
    string? Attestor,
    string AccuracyStatement,
    IReadOnlyList<CorpusDocumentView> Documents,
    string DerivationStatement);
