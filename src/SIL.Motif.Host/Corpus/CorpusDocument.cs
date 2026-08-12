using System;
using System.Collections.Generic;
using System.Linq;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// One unit of text within a Corpus — an eBible translation, a Wikipedia article dump, one side of an OPUS
/// bitext. The boundary that counts and n-gram models must not run across.
/// </summary>
/// <remarks>
/// <para>
/// <b>A Document keeps its text as it arrived: in order, with its repetitions.</b> That is the difference
/// between this and <see cref="CorpusDescriptor"/>, which sorts and deduplicates. Both are wanted, and only
/// this one can be turned into the other: frequency is what ranks an unparsed-form worklist, and sequence is
/// what an n-gram model is built from. Deduplicating on the way in destroys both irrecoverably.
/// </para>
/// <para>
/// <b>Licence lives here as well as on the Corpus, and that is not redundancy.</b> eBible ships a
/// <c>licences.tsv</c> with a row per translation: one pull produces documents under public domain,
/// CC BY-SA and CC BY-NC-ND together. A single licence on the corpus would have to be either the loosest,
/// which is wrong and unsafe, or the strictest, which discards the usable material. So a Document may carry
/// its own, and <see cref="EffectiveCapabilities"/> resolves it.
/// </para>
/// </remarks>
/// <param name="DocumentId">Stable identity within the corpus. Supplied by the ingester, not invented here.</param>
/// <param name="Title">What a person would call it — <c>"Sena New Testament (sehNT)"</c>.</param>
/// <param name="Source">Where the bytes came from, recorded whether they were fetched or handed over.</param>
/// <param name="Text">The text itself, in order.</param>
/// <param name="ContentSha256">
/// The hash of the exact bytes ingested — <b>identity, not surveillance</b>. It says precisely what a figure
/// was computed over, so two corpora can be compared without shipping their contents and a figure citing a
/// hash that no longer matches is recognisable as describing different text.
/// <para>
/// It is emphatically <b>not</b> a change detector pointed at the source. Motif never revisits a URL, never
/// polls, and never asks whether eBible has news: text is grabbed when somebody decides to grab it, and a
/// corpus stays exactly what it was until a person fetches again. Fetching again means a new corpus, which is
/// the whole mechanism — see ADR 0037 decision 1.
/// </para>
/// </param>
/// <param name="IngestedUtc">When Motif took it in — distinct from when the source published it.</param>
/// <param name="Licence">This document's licence verbatim, when it differs from the corpus's.</param>
/// <param name="Capabilities">This document's licence capabilities, when they differ from the corpus's.</param>
/// <param name="Attributes">
/// Anything the ingesting tool knows that Motif has no field for — eBible's copyright holder and ISO code,
/// OPUS's corpus name and release. Kept verbatim so that a fact discovered at fetch time is not lost merely
/// because Motif had not modelled it yet.
/// </param>
public sealed record CorpusDocument(
    string DocumentId,
    string Title,
    DocumentSource Source,
    string Text,
    string ContentSha256,
    DateTimeOffset IngestedUtc,
    string? Licence = null,
    LicenceCapabilities? Capabilities = null,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    /// <summary>
    /// What this document's licence permits, falling back to the corpus's when the document says nothing.
    /// </summary>
    /// <remarks>
    /// A document that states nothing inherits; a document that states something overrides entirely rather
    /// than merging field by field. Merging would let a corpus-level "may derive" leak into a document whose
    /// own licence forbids it, which is the exact failure this design exists to prevent.
    /// </remarks>
    public LicenceCapabilities EffectiveCapabilities(CorpusOrigin corpusOrigin) =>
        Capabilities ?? corpusOrigin.EffectiveCapabilities;

    /// <summary>This document's licence, falling back to the corpus's.</summary>
    public string? EffectiveLicence(CorpusOrigin corpusOrigin) => Licence ?? corpusOrigin.Licence;
}

/// <summary>
/// A Corpus as Motif stores it: running text in one or more Documents, with the record of where it came
/// from, how it was tokenised, and what anyone attests about it.
/// </summary>
/// <remarks>
/// <para>
/// Never part of the language project — see ADR 0036. A FieldWorks <c>Text</c> is an interlinearised document
/// inside the project and is a different thing with a confusingly similar name; this is deliberately not
/// called one.
/// </para>
/// </remarks>
/// <param name="CorpusId">Stable identity, supplied by whoever creates it.</param>
/// <param name="Provenance">Origin, tokenisation, and any qualification. Required — a corpus without it cannot be measured against honestly.</param>
/// <param name="Documents">The documents, in the order they were added.</param>
public sealed record StoredCorpus(
    string CorpusId,
    CorpusProvenance Provenance,
    IReadOnlyList<CorpusDocument> Documents)
{
    /// <summary>An empty corpus. Documents are added afterwards, because a fetch arrives a file at a time.</summary>
    public static StoredCorpus Create(string corpusId, CorpusProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(corpusId))
            throw new ArgumentException("A corpus must have an id.", nameof(corpusId));
        ArgumentNullException.ThrowIfNull(provenance);

        return new StoredCorpus(corpusId, provenance, Array.Empty<CorpusDocument>());
    }

    /// <summary>The same corpus with one more document; a duplicate id throws (pinned by `ADocumentIdIsNotSilentlyOverwritten`).</summary>
    public StoredCorpus With(CorpusDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Documents.Any(d => string.Equals(d.DocumentId, document.DocumentId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Corpus '{CorpusId}' already has a document with id '{document.DocumentId}'. Document ids " +
                "are the handle everything downstream uses, so a silent overwrite would detach an Assessment " +
                "from the text it was computed over.");
        }

        return this with { Documents = Documents.Append(document).ToArray() };
    }

    /// <summary>
    /// The documents a derived artefact — an n-gram model, a published word list — may lawfully be built from.
    /// </summary>
    /// <remarks>
    /// <b>A subset, and usually a small one.</b> The whole corpus is available for reach and grammar coverage figures,
    /// because reading is not deriving. Callers building anything publishable must go through here rather than
    /// through <see cref="Documents"/>, and the two being different lengths is the normal case, not a fault.
    /// </remarks>
    public IReadOnlyList<CorpusDocument> DocumentsPermittingDerivation() =>
        Documents.Where(d => d.EffectiveCapabilities(Provenance.Origin).PermitsDerivedArtefacts).ToArray();

    /// <summary>What to tell a person who asked for a derived artefact and cannot have all of the corpus.</summary>
    public string DescribeDerivationRestrictions()
    {
        var permitted = DocumentsPermittingDerivation().Count;
        if (permitted == Documents.Count)
            return $"All {Documents.Count} document(s) in '{CorpusId}' permit derived works.";

        var blocked = Documents
            .Where(d => !d.EffectiveCapabilities(Provenance.Origin).PermitsDerivedArtefacts)
            .Select(d => $"  - {d.Title}: {d.EffectiveCapabilities(Provenance.Origin).WhyDerivedArtefactsAreNotPermitted(d.Title)}");

        return $"{permitted} of {Documents.Count} document(s) in '{CorpusId}' permit derived works. " +
               $"The rest do not, and are excluded from anything published:{Environment.NewLine}" +
               string.Join(Environment.NewLine, blocked);
    }
}
