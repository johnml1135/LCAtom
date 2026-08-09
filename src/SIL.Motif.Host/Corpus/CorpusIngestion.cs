using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Metadata supplied when a Document is added. Everything the ingesting tool knows and Motif cannot work out.
/// </summary>
/// <param name="DocumentId">Stable identity within the corpus.</param>
/// <param name="Title">What a person would call it.</param>
/// <param name="Licence">This document's licence verbatim, when it differs from the corpus's.</param>
/// <param name="Capabilities">What that licence permits, when it differs from the corpus's.</param>
/// <param name="Attributes">Anything else the fetching tool knows. Kept verbatim.</param>
public sealed record DocumentMetadata(
    string DocumentId,
    string Title,
    string? Licence = null,
    LicenceCapabilities? Capabilities = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// Adds Corpora and Documents to Motif's store, from a file or a URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>In plain terms:</b> this is how text gets into Motif. Something outside — usually linguistic-assistant,
/// which already knows how to pull eBible and OPUS — produces the text and what is known about it, and this
/// takes it in, hashes it, and records both. From here on, everything Motif says about a grammar's reach is
/// said about text that arrived through this door with its origin attached.
/// </para>
/// <para>
/// <b>Motif does not clean, tokenise or interpret licences.</b> It records what it was told and hashes what it
/// was given. The division is deliberate: the fetching side changes whenever a source changes its layout,
/// and Motif should not.
/// </para>
/// </remarks>
public sealed class CorpusIngestion
{
    private readonly ICorpusStore _store;
    private readonly IContentFetcher _fetcher;
    private readonly Func<DateTimeOffset> _clock;

    public CorpusIngestion(ICorpusStore store, IContentFetcher? fetcher = null, Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fetcher = fetcher ?? new ContentFetcher();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Create a Corpus. It starts empty; Documents are added afterwards, because a fetch arrives a file at a
    /// time and a partial corpus is a real state rather than an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">A corpus with this id already exists.</exception>
    public StoredCorpus AddCorpus(string corpusId, CorpusProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        if (_store.Exists(corpusId))
        {
            throw new InvalidOperationException(
                $"Corpus '{corpusId}' already exists. Add documents to it rather than recreating it — " +
                "replacing it would orphan every Assessment computed over the old contents.");
        }

        var corpus = StoredCorpus.Create(corpusId, provenance);
        _store.Save(corpus);
        return corpus;
    }

    /// <summary>
    /// Add a Document to an existing Corpus, from a file already on disk or a URL to retrieve.
    /// </summary>
    /// <remarks>
    /// The content is hashed as it arrived, before any decoding decision, so the hash identifies the bytes
    /// rather than Motif's reading of them.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No such corpus, or the document id is already used.</exception>
    public async Task<CorpusDocument> AddDocumentAsync(
        string corpusId,
        DocumentSource source,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metadata);

        var corpus = _store.Load(corpusId)
            ?? throw new InvalidOperationException(
                $"No corpus '{corpusId}'. Create it first, so that the text arrives with an origin and a " +
                "tokenisation record attached rather than being back-filled later.");

        var bytes = await _fetcher.FetchAsync(source, cancellationToken).ConfigureAwait(false);

        var document = new CorpusDocument(
            DocumentId: metadata.DocumentId,
            Title: metadata.Title,
            Source: source,
            Text: DecodeUtf8(bytes),
            ContentSha256: Sha256Hex(bytes),
            IngestedUtc: _clock(),
            Licence: metadata.Licence,
            Capabilities: metadata.Capabilities,
            Attributes: metadata.Attributes);

        _store.Save(corpus.With(document));   // With() rejects a duplicate id before anything is written
        return document;
    }

    /// <summary>
    /// Take in a whole handoff bundle: the corpus metadata and every document it names, in one call.
    /// </summary>
    /// <remarks>
    /// The expected route. An external tool writes a bundle describing what it fetched; this reads it. Adding
    /// a corpus and its documents separately works and is what a person does by hand — a bundle is what a
    /// program produces.
    /// </remarks>
    public async Task<StoredCorpus> AddBundleAsync(CorpusBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        AddCorpus(bundle.CorpusId, bundle.Provenance);

        foreach (var entry in bundle.Documents)
        {
            await AddDocumentAsync(bundle.CorpusId, entry.Source, entry.Metadata, cancellationToken)
                .ConfigureAwait(false);
        }

        return _store.Load(bundle.CorpusId)!;
    }

    /// <summary>
    /// Decodes as UTF-8, stripping a byte-order mark if present.
    /// </summary>
    /// <remarks>
    /// UTF-8 without negotiation, because every source in scope emits it and a mis-guessed encoding produces
    /// forms that look like exotic morphology and fail to parse — a grammar gap that is really a decoding bug,
    /// which is among the worse things this project could report.
    /// </remarks>
    private static string DecodeUtf8(byte[] bytes) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes).TrimStart('﻿');

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
