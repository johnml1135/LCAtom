# ADR 0032 — The stem-assessment loop is PanGloss's supplied lexicon; Motif owns persistence and promotion

**Status:** accepted, 2026-08-06. Corrects a finding in
[ADR 0031](0031-collaboration-follows-the-data-not-the-surface.md) and decides `B24`. Constrains `MOT-15`
and `MOT-19`.

**In plain terms:** PanGloss can already take a batch of new words, try them against the existing grammar,
and report which ones fit and which do not — without rebuilding or recompiling anything. It also has a
built assistant that works out which grammatical category a word belongs to by asking about forms. Motif
should use both rather than build its own. What PanGloss deliberately leaves out is anything durable: it
does not save the words, sync them, or move an accepted word into the real dictionary. **That leftover is
exactly Motif's job**, so the two fit together with almost no overlap.

## The finding this corrects, and how I got it wrong

[ADR 0031](0031-collaboration-follows-the-data-not-the-surface.md) records, from a reading of the source,
that "no incremental stem addition exists" and that new stems therefore mean a full grammar reload. **That
is wrong.** The owner:

> *It may only be in plans, but PanGloss in its spell-checking function has the ability for loading a set of
> user-defined stems that should fit into an existing FST in deployment. We should use that.*

It is not only in plans — it is built. **How I missed it:** I searched `pg-grammar` and `pg-lexicon` for
`add_entry`, `insert_entry` and `extend_lexicon`, found none, and concluded absence. The mechanism is real
but sits **beside** the grammar rather than inside it, and is named nothing like what I guessed. The narrow
claim I made — that `Grammar` owns `entries` and nothing mutates a loaded `Grammar` — is still true, and is
in fact a *deliberate non-goal* of the design ("does not ... dynamically mutate a loaded grammar"). I drew a
conclusion about the system from a fact about one type.

Same lesson as twice before in this repository: a grep that finds nothing is weak evidence of absence, and
the weakest form of it is a grep for the names you expected.

## What actually exists

**A supplied-lexicon overlay,** `pg_lexicon::SuppliedLexiconRuntime`, held on the FFI grammar handle
(`pg-ffi/src/grammar.rs:83`). Its design document calls it *"a glorified spell-checker add-on"*, and the
implementation plan explicitly deletes *"XML augmentation, grammar reload, and foma recompilation"* in favour
of an overlay carrying a revision, with the analysis cache keyed on that revision so stale entries are
ignored.

**Around thirteen operations across a length-delimited JSON boundary**, one function per operation rather
than a command dispatcher: `hc_lexicon_add_json`, `_update_json`, `_remove_json`, `_clear_json`, `_list_json`,
`_get_json`, `_search_json`, `_catalog_json`, `_export_json`, `_import_json`, `_set_authority_json`,
`_set_gloss_language_json`, plus `hc_analyze_word_json`. Add and update take an expected revision, so
concurrent edits are caught rather than merged.

**A classification matrix and an adaptive guide** — `hc_classification_matrix_json` plus
`hc_classification_guide_{answer,undo,remaining,next,useful,selection}_json`. The guide records yes / no /
unknown judgements about whether a word takes given forms, eliminates the signatures those answers rule out,
and adaptively picks the next most informative form to ask about. It is **advisory**: a host may ignore it
and reason over the matrix directly, which is what an AI would do.

**Identity discipline that matches Motif's.** A signature's identity comes from authored XML ids and GUIDs
for parts of speech, features, values and inflection classes, reduced to a deterministic `sig_` id over a
canonical sorted encoding. Display names do not participate. Renaming a category preserves identity;
deleting and recreating one does not. That is the same rule Motif applies to canonical ids, reached
independently.

**And a hard boundary, stated as non-goals.** The supplied lexicon *"does not author new grammatical
categories, model exceptional or bound stems, manage users, persist data, synchronize devices, merge
independent stores, implement promotion,"* nor dynamically mutate a loaded grammar. A supplied entry adds
**one ordinary free literal stem to one or more signatures already observed in the official lexicon.**

## Decision

### 1. Motif evaluates stems through the supplied lexicon, and never reloads a grammar to do it

The loop in ADR 0031 — add stems, reanalyse, report coverage — runs against the overlay. No grammar reload,
no FST recompilation, so the measurement that ADR 0031 called for before designing an incremental API is no
longer on the critical path. (It is still worth having for the *grammar*-editing loop, where a rule change
genuinely does require a rebuild.)

### 2. The refusal is the classifier — this is a better answer than the one ADR 0031 gave

A supplied entry is only accepted if it fits a signature **already present in the official lexicon**. So:

> If PanGloss will not accept a word as a supplied entry, that word needs grammar work.

This is mechanical, it happens at the moment the fact becomes knowable, and it needs no labelling by an
author. ADR 0031 said the check class is derived from the operations in a Proposal's current revision — true,
but it framed the derivation as a manifest lookup. **Trying the stem is a sharper test than inspecting it**,
and it catches precisely the case the owner described: a batch of new words that turns out to carry a grammar
change, revealed *because* the words were added.

A new category, a bound root, or an irregular allomorph therefore routes itself to a grammar Proposal.
Nothing has to notice; the rejection is the notice.

### 3. Motif owns exactly what PanGloss excludes

| | PanGloss | Motif |
| --- | --- | --- |
| Evaluate a batch of stems | **yes** | no |
| Report coverage and mis-fit | **yes** | consumes |
| Work out a word's category | **yes** (matrix + guide) | consumes |
| Persist the batch | no — explicit non-goal | **yes**, as a Proposal |
| Record the evidence durably | no | **yes**, in the Receipt |
| Promote a stem into the real lexicon | no — explicit non-goal | **yes**, an ordinary LibLCM apply |

The overlay is **scratch**, in the same sense as ADR 0016's Dry Run copy: it exists to be measured against
and thrown away. Motif must never treat it as storage.

### 4. Every coverage number carries its provenance — this decides `B24`

The owner, agreeing with the gap ADR 0031 raised:

> *A coverage number should include the text used and a hash of them.*

So a coverage figure is never a bare percentage. It cites **the corpus it was measured over and that
corpus's hash**, the **supplied-lexicon overlay revision**, and the **grammar identity**. A number whose
corpus hash no longer matches is stale and says so, which closes the drift `B24` identified: the footprint
digest cannot see a text that arrived from someone else's Chorus sync, but a corpus hash can.

Pleasingly, PanGloss reached the same conclusion for its own caching — its FST plan versions the user-delta
cache by user-lexicon hash. Two systems, same instinct: **a derived number is only meaningful beside a
digest of what it was derived from.**

### 5. Motif consumes the classification guide; it does not build one

An AI reads the matrix and reasons over it. A human gets the adaptive question sequence. Either way this is
PanGloss's surface, and a second implementation in Motif would be a second thing to keep correct.

## Consequences

- **`MOT-19` gains a coverage report and loses the work of computing one.** It formats and records; PanGloss
  measures.
- **`MOT-15`'s interface list grows** by the supplied-lexicon and classification operations, and its "measure
  `hc_grammar_load` first" note narrows to the grammar-editing loop only.
- ~~**A known limitation to watch:** compounds of a user stem with a base stem are the hard case for a
  delta-FST overlay, so a compounding language may see under-reported coverage.~~ **Withdrawn the same day —
  this was wrong twice over.** See "The compounding worry, withdrawn" below.
- **Stability is unproven.** The supplied-lexicon design states the feature it replaced had no production
  users, so this API is young. Pin a version and expect churn — which ADR 0021 already licenses on Motif's
  side.
- **What would reopen this:** overlay coverage disagreeing materially with full-rebuild coverage on a real
  grammar. That is a measurable claim and nobody has measured it.

## The compounding worry, withdrawn

An earlier version of this ADR recorded a "known limitation to watch": that compounds of a supplied stem with
a base stem might make overlay coverage under-report. **That was wrong in two independent ways, and the
grounding is worth keeping because the failure mode is instructive.**

**Wrong component.** The passage I drew it from is item 7 of `docs/fst-plan/HYBRID_FST_RUST_PLAN.md` — a
**research track** for adding stems on *deployed devices* (a handset keyboard, an office extension) via a
delta FST. That is not the mechanism this ADR relies on. The built thing is
`pg_lexicon::SuppliedLexiconRuntime`, specified in
`docs/superpowers/specs/2026-07-22-runtime-supplied-lexicon-design.md`. I attached a caveat from an unbuilt
future mechanism to the API we are actually going to call.

**Wrong reading, even of that passage.** It does not describe a limitation. It lists four competing mechanisms
to spike, and names compounding as **the test case that would distinguish them** — mechanism (c), hook arcs,
is *"only worth trying if (a)'s composite-level union misses candidates that require base-trie interaction
(compounds of user stem + base stem are the test case; **(a) should handle them via the engine-side lexicon
during verify**, but measure)."* The plan's own expectation is that its preferred mechanism handles compounds.
I converted "the experiment that would tell these apart" into "the thing that is broken."

**And the built design addresses it directly.** *"The overlay path recognizes inflected and compound forms
because morphology is unapplied before trie lookup"* — analysis runs the morphology backwards to a stem and
*then* consults the trie, so a compound reduces to stem lookups the overlay can serve. Compounding through
the overlay trie is also a required test in that design's test list. There is no gap here to watch.

### What is worth keeping: the linguistic argument for stems-only

The scope guard — *"users add stems, never rules or categories"* — has a real justification, and it is the
reason decision 2 above is a **good** classifier rather than merely a mechanical one:

> *This is linguistically safe by Zipf's law of irregularity: rare words are more regular than common ones,
> so novel stems overwhelmingly fit existing paradigms — the irregulars are already in the shipped lexicon.*

Frequent words are the ones that carry irregular morphology, and a mature project has already entered them by
hand. What arrives later in bulk is the long tail, which is overwhelmingly regular. So a batch of 200 new
stems should almost all fit an existing signature, and **a refusal is rare — which is exactly what makes it
informative.** A signal that fires constantly tells you nothing; this one fires when something genuinely
needs a linguist.

It also predicts the failure mode to watch for, which is the opposite of the one I invented: if PanGloss
starts refusing a *large fraction* of a batch, the likely cause is not compounding but that the batch is not
the long tail at all — wrong language, wrong orthography, or word forms rather than stems.
