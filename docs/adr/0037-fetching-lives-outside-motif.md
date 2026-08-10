# ADR 0037 — Fetching lives outside Motif; a bundle is the handoff

**Status:** accepted, 2026-08-09. Implements the ingestion half of
[ADR 0036](0036-motif-has-its-own-data-store.md).

**In plain terms:** Motif needs large bodies of real text to say anything useful about how much of a language
a grammar reaches. That text comes from OPUS and eBible, and getting it involves knowing each site's layout,
its markup, its licence file, and the proxy that sits between us and it. **Motif does not learn any of that.**
Something else fetches and cleans the text and hands it over with a note saying where it came from and what
its licence allows. Motif takes it in, hashes it, and records the note.

## The decision

### 1. An external tool fetches; Motif ingests

linguistic-assistant already does this half. It has `research/corpus/ebible/{config,fetch,read,build}.py`,
which downloads eBible, reads its verse-per-line format, and builds parallel rows — and it has an
`addons/audio` module that already gates downloads on licence before fetching, which is the same shape this
needs.

**Why not put it in Motif.** Fetching code changes whenever a source changes its layout, and the sources
change on their own schedule. Motif's job is to be right about a grammar, and a component that has to be
edited every time a website moves a file is not a component that stays right. The Python side is also where
the tooling already is: [`opustools`](https://pypi.org/project/opustools/) and the
[OPUS-API](https://github.com/Helsinki-NLP/OPUS-API) are Python, eBible's own downloader is Python, and
linguistic-assistant is Python.

**What we give up, and it is deliberate: Motif does not watch the source.** No polling, no revisiting a URL
to see whether it moved, no "your corpus may be out of date" nagging. Text is grabbed when somebody decides to
grab it. A corpus is a thing that was fetched once, and it stays exactly what it was until a person chooses to
fetch again.

Every document still carries the SHA-256 of the bytes ingested, but **that hash is identity, not
surveillance**: it says precisely what a figure was computed over, so two corpora can be compared and a stale
figure can be recognised as describing different text. It is not a change detector pointed at eBible, and
nothing in Motif ever asks a source whether it has news.

*Amended 2026-08-09. As first written, this section claimed source change was "detectable here", which invited
exactly the polling machinery the owner ruled out.*

### 2. The handoff is a bundle: a small JSON file that names files rather than containing them

A bundle describes one corpus and lists its documents, each with a path or URL and what is known about it.
It is not an archive.

**Why naming rather than containing.** The fetching tool has already written hundreds of megabytes to disk;
repackaging them to hand them over doubles the space and the time for nothing. And a bundle whose files have
gone missing fails loudly at ingestion, naming the file, rather than silently importing less than it claimed.

**Relative paths resolve against the bundle's own directory**, so a handoff folder can be copied between
machines without editing. Resolving against the working directory instead produces a bundle that works on the
machine that wrote it and nowhere else — and fails by finding nothing, which is only noticed much later.

### 3. Motif accepts a file or a URL, and fetches URLs through one replaceable seam

The URL case exists so a one-off does not require a detour through another program, and because recording
where something came from is worth little if nothing ever checks the location resolves. It goes through
`IContentFetcher` — no retries, no caching, no resumption. **If ingestion ever needs resumable downloads, the
work has moved to the wrong side of this seam.**

### 4. What a licence permits is recorded separately from what it is called, and resolved per document

`CorpusOrigin.Licence` keeps the licence verbatim, which is right for the record and useless for a decision.
`LicenceCapabilities` is the decision-shaped form: may-redistribute, may-derive, may-use-commercially,
requires-attribution, and **the basis** — who said so.

**Why this is not over-engineering.** Roughly 805 of eBible's ~1,004 translations are **No-Derivatives**.
Measuring a grammar's reach over that text is reading, and reading is fine. Building a spelling-correction or
word-prediction model from the same text produces a derived work, and for most of eBible that is not
permitted. **The two uses run over identical bytes**, so nothing in the data distinguishes them — only this
record does.

**Per document, not per corpus**, because eBible ships a `licences.tsv` with a row per translation: one pull
produces public-domain, CC BY-SA and CC BY-NC-ND material together. A single corpus-level licence would have
to be either the loosest, which is unsafe, or the strictest, which discards usable material.

**A document's capabilities override the corpus's wholesale rather than merging field by field**, so a
corpus-level "may derive" can never fill in a gap in a document whose own licence forbids it.

### 5. Unknown is not permission

Every capability is three-state: yes, no, or nobody established it. `MayDerive` being unknown blocks
derivation exactly as `false` does, and the two give different explanations — *go and find the licence* versus
*stop*.

This is the same rule [ADR 0036](0036-motif-has-its-own-data-store.md) decision 5 applies to accuracy figures,
and for the same reason: **"I could not look" must never read as "everything is fine."**

## Consequences

- **linguistic-assistant owes a bundle writer.** Its eBible pipeline stops at parallel rows; emitting a
  bundle is a small addition, and the licence rows it needs are in eBible's `licences.tsv`. Nothing in Motif
  blocks on it — a bundle can be hand-written.
- **OPUS is the right door for parallel text and the wrong one for bulk.** Its Wikipedia corpus is
  sentence-aligned bitext, so it carries only the sentences that aligned. Bulk monolingual text for n-grams
  wants the Wikipedia dumps directly.
- **Tokenisation's house answer is the writing system, not SIL.Machine** — *corrected 2026-08-09 after
  reading the source.* This bullet first named SIL.Machine's `LatinWordTokenizer` as the candidate. It is
  hardcoded Latin-script with no writing-system awareness, and it folds a leading apostrophe *into* the word,
  so it does not solve the problem it was named for (`B29`). FieldWorks segments by asking the writing system
  which characters are word-forming, and Motif matches that. SIL.Machine remains worth adopting for Latin
  edge cases — abbreviations, URLs, hyphenation variants — and is netstandard2.0 on NuGet, so net48-safe for
  scope 2. The Thot aligner the gloss pipeline needs is a separate and still-valid reason to want that repo.
- **The store is still files.** [ADR 0036](0036-motif-has-its-own-data-store.md) decision 6 puts Corpora in
  an embedded database; ingestion goes through `ICorpusStore` so that change does not reach it.
- **Ingested text now reaches a grammar coverage figure** — `B26` fixed 2026-08-09. `CorpusTokenisation`
  bridges Documents to a `CorpusDescriptor` behind an `IWordTokeniser` seam, and this ADR's `tokenisation`
  block is what binds a corpus to the tokeniser it may be measured with: a mismatch is refused rather than
  silently re-stamped.
- **Settled 2026-08-09, and settled as "no":** an ingested corpus is not replaced. Creating a new corpus is
  the whole mechanism for fetching again, and `B28` records the reasoning. This follows from decision 1 —
  nothing here watches a source, so there is no event that would trigger a replacement.
