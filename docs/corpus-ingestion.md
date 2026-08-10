# Getting text into Motif

**In plain terms:** before Motif can say "this grammar reaches 62% of real Sena text", it needs the real Sena
text. This is how that text arrives, where it comes from, and what stops Motif publishing something it is not
allowed to.

The division of labour, decided in [ADR 0037](adr/0037-fetching-lives-outside-motif.md): **an external tool
fetches and cleans; Motif ingests and records.** In practice the external tool is linguistic-assistant, in
Python, which already knows how to pull eBible.

## The two sources

| | [eBible](https://github.com/BibleNLP/ebible) | [OPUS](https://opus.nlpl.eu) |
| --- | --- | --- |
| **What it is** | 1,000+ Bible translations in 833 languages, already cleaned to verse-per-line | Sentence-aligned parallel corpora, 600+ languages, 40,000+ pairs |
| **How to get it** | Its `code/python/ebible.py`, or the `bible-nlp/biblenlp-corpus` dataset | [`opustools`](https://pypi.org/project/opustools/) — `opus_get`, `opus_read`; or the [OPUS-API](https://github.com/Helsinki-NLP/OPUS-API) directly |
| **Licences** | Per translation, in `licences.tsv`. **~805 of ~1,004 are No-Derivatives** | Varies; OPUS states it does not own the text and offers only what it believes it may redistribute |
| **Best for** | Language coverage. For most languages here this is the widest net there is | Genuine non-biblical parallel text, where it exists |

**A caution on OPUS's Wikipedia corpus:** it is sentence-aligned bitext, so it contains only the sentences
that aligned — not the articles. For bulk monolingual text to build n-grams from, the Wikipedia dumps are the
right source and OPUS is not.

## The handoff: a corpus bundle

The fetching tool writes one small JSON file beside the text it produced. Motif reads it.

```json
{
  "corpusId": "ebible-seh",
  "origin": {
    "description": "eBible, Sena translations",
    "uri": "https://github.com/BibleNLP/ebible",
    "retrievedUtc": "2026-08-09T10:30:00Z",
    "licence": "mixed; see per-document",
    "capabilities": {
      "mayRedistribute": true,
      "mayDerive": false,
      "requiresAttribution": true,
      "basis": "eBible licences.tsv"
    }
  },
  "tokenisation": {
    "method": "fieldworks-word-forming",
    "version": "1",
    "notes": "Verse-per-line input; segment by the project's declared word-forming characters."
  },
  "qualification": null,
  "documents": [
    {
      "documentId": "sehNT",
      "title": "Sena New Testament",
      "source": "sehNT.txt",
      "licence": "CC-BY-NC-ND-4.0",
      "attributes": { "copyrightHolder": "Wycliffe Bible Translators", "isoCode": "seh" }
    },
    {
      "documentId": "sehPD",
      "title": "Sena, public domain",
      "source": "sehPD.txt",
      "licence": "public domain",
      "capabilities": {
        "mayRedistribute": true, "mayDerive": true, "mayUseCommercially": true,
        "requiresAttribution": false, "basis": "eBible licences.tsv"
      }
    }
  ]
}
```

Then:

```
motif add-corpus-bundle --bundle ./handoff/bundle.json
```

### What each part is for

**`origin`** — required. Description, location, retrieval date, licence. A corpus whose source nobody
recorded cannot be published from safely, and the moment to record it is when the text arrives.

**`tokenisation`** — required, and it is not bookkeeping. At corpus scale **tokenisation decides most of what
"unparsed" means**: a form invented by splitting on an apostrophe fails to parse and reads as a gap in the
grammar. Two corpora tokenised differently are not comparable even when the source text is identical.

`method` and `version` must name a tokeniser Motif actually has — see **Which tokeniser to declare** below.
They are **binding, not descriptive**: `CorpusTokenisation` refuses to run a tokeniser that disagrees with
them rather than re-stamping the corpus with whatever happened to run. A fetching tool declares the
tokenisation it wants applied; it does not need the writing system itself, because Motif supplies that from
the open project.

**`capabilities`** — what the licence *permits*, as opposed to what it is called. See below.

**`qualification`** — optional, usually absent, and **its absence is meaningful**. It is a named person's
dated claim that the corpus is clean and in scope. Without it, Motif will compute reach figures over the
corpus and will refuse to compute accuracy figures, saying so explicitly rather than footnoting a number.

**`source`** — a path or a URL. Relative paths resolve **against the bundle file's own directory**, so the
handoff folder can be copied between machines unedited.

**`attributes`** — anything the fetching tool knows that Motif has no field for. Kept verbatim, so a fact
discovered at fetch time is not lost merely because Motif had not modelled it yet.

## Why licences get their own machinery

Roughly **805 of eBible's ~1,004 translations are No-Derivatives**. That matters here specifically because:

- Measuring how much of a corpus a grammar reaches is **reading**. Reading is fine.
- Building a spelling-correction or word-prediction model is **deriving**. For most of eBible that is not
  permitted.

Those two run over identical bytes. Nothing in the data distinguishes them — only the licence record does.
So `StoredCorpus.DocumentsPermittingDerivation()` returns a subset, usually a small one, and anything
publishable must go through it rather than through `Documents`.

**Licences are per document**, because one eBible pull mixes public domain, CC BY-SA and CC BY-NC-ND. A
document's capabilities override the corpus's wholesale rather than merging field by field, so a corpus-level
"may derive" can never fill a gap in a document whose own licence forbids it.

**Unknown is not permission.** Each flag is yes, no, or nobody established it. Unknown blocks derivation
exactly as `false` does, and the two say different things — *go and find the licence* versus *stop*.

**Motif does not interpret licences.** It records what it was told, and `basis` says who told it. A
capabilities block without a `basis` is rejected, because an unsourced permission claim is worse than no
claim: it looks like somebody checked.

## Doing it by hand

For a single file, without a bundle:

```
motif add-corpus --id seh-wikipedia \
  --description "Wikipedia, Sena edition" \
  --uri https://seh.wikipedia.org/ \
  --licence CC-BY-SA-4.0 \
  --may-derive true --may-redistribute true --requires-attribution true \
  --licence-basis "Wikipedia site-wide licence" \
  --tokeniser fieldworks-word-forming --tokeniser-version 1

motif add-document --corpus seh-wikipedia --doc dump-2026-08 \
  --source ./seh-wiki-2026-08.txt --title "Sena Wikipedia, August 2026 dump"

motif show-corpus seh-wikipedia
```

Omitting every licence flag records "nothing established", which blocks derived works and says so. That is
the correct state for text nobody has checked — not an error to be worked around.

## Which tokeniser to declare

Two exist, and a corpus declares the one it was actually tokenised with — `CorpusTokenisation` refuses a
mismatch rather than silently re-stamping it.

| `tokenisation.method` | What it does | Use it when |
| --- | --- | --- |
| `fieldworks-word-forming` | Segments as FieldWorks' `WordMaker` does: maximal runs of characters **the project's writing system** calls word-forming | **Default choice.** Anything measured against a FieldWorks grammar |
| `whitespace-and-punctuation` | Splits on whitespace, trims edge punctuation using .NET's Unicode classification | Corpora already tokenised this way, and cases with no writing system to consult |

**Why the writing system matters, concretely.** FieldWorks decides word boundaries by asking the writing
system, not by asking Unicode. A project that has declared `'` among its word-forming characters keeps the
glottal stop in `'mbali'`; one that has not gets it stripped — **and gets it stripped by FieldWorks too**.
Since the lexicon a grammar is built from was segmented by FieldWorks, matching FieldWorks is what stops
Motif reporting false gaps. Being independently cleverer would manufacture them.

Two behaviours worth knowing before you pick:

- **An undeclared writing system degrades quietly**, so `WhyTokenisationMayBeDegraded()` reports it and names
  the only real fix — running FieldWorks' Valid Characters wizard, which repairs both tools at once.
- **Digits are not word-forming**, so `fieldworks-word-forming` *splits* on them: `2nd` becomes `nd`, and an
  orthography marking tone with digits is shredded (`ma1` becomes `ma`). Declaring the digits fixes it. The
  other tokeniser keeps alphanumeric mixtures whole, so **the two disagree about far more than apostrophes.**

## What this does not do yet

**Nothing is watched.** Motif never revisits a source, never polls, and never tells you a corpus might be out
of date. Text is grabbed when somebody decides to grab it, and a corpus stays what it was until a person
fetches again — at which point a new corpus is the whole mechanism. The per-document SHA-256 is identity, not
surveillance: it says what a figure was computed over. See `B28`.

**Assessments are not stored yet.** Ingestion, tokenisation and a grammar coverage figure all work; keeping
the parser's output between runs does not exist. That is `MOT-20`, and it is what makes a figure cheap to
re-read rather than re-earn.

**Storage is files.** [ADR 0036](adr/0036-motif-has-its-own-data-store.md) decision 6 puts Corpora in an
embedded database; ingestion goes through `ICorpusStore` so that change will not reach it.

## What linguistic-assistant still owes

Its eBible pipeline (`research/corpus/ebible/{config,fetch,read,build}.py`) stops at parallel rows. What is
missing is the step that emits a bundle: the per-translation licence rows it needs are already in eBible's
`licences.tsv`. For OPUS there is nothing yet; `opustools` would supply the fetch.

Nothing in Motif blocks on either — a bundle can be written by hand, and the tests do exactly that.

## Related

- [ADR 0036](adr/0036-motif-has-its-own-data-store.md) — why Motif has a store of its own, and why none of
  this enters the FieldWorks project
- [ADR 0037](adr/0037-fetching-lives-outside-motif.md) — why fetching is somebody else's job
