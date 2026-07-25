# ADR 0010 — The HermitCrab experimentation loop is LCAtom's primary purpose

Status: accepted (2026-07-25)

## Context

Earlier documents described LCAtom as a general-purpose LibLCM change-set runner with several
motivating consumers listed side by side, and treated the HermitCrab projection as one capability
among many. That framing under-specified the actual product and led to at least one wrong design
recommendation (deferring stratum support because the *model's* `MoStratum` objects are vestigial,
without noticing that HermitCrab itself supports strata and is the consumer that matters).

## Decision

**LCAtom exists primarily to serve one loop:** a person or an AI working on a language asks *"what if
we change this — does the text parse better?"*, and LCAtom makes that question safe to ask, cheap to
repeat, and honest to answer.

```
author a change (human or AI, high-level intent)
  → assess: review the exact state delta before touching anything
  → project the would-be grammar to HermitCrab XML
  → PanGloss compiles/parses a text and returns a report
  → compare against previous runs
  → keep it (apply, recorded in history) or discard it
```

The engines are: **HermitCrab** — the C# morphological parser in `../machine`
(`src/SIL.Machine.Morphology.HermitCrab`) — and **PanGloss** in `../PanGloss`, a Rust native port of
that parser running a propose-and-confirm FST architecture, which names *"compare grammar revisions"*
as a first-class capability and states that *"consuming applications own history, publication
decisions, UI, and interactive debugging."* **LCAtom is that consuming application.** The division of
labor is already declared from the other side.

### Consequences for coverage

1. **Everything HermitCrab supports must be authorable through LCAtom's API, in a friendly way.**
   The HermitCrab language model — natural classes, features, strata, phonological rules and their
   contexts (including iteration), affix processes, templates and slots, compound rules, allomorph
   environments, co-occurrence rules — is the **primary completeness criterion**. LibLCM's model
   surface remains the storage target and must still be 100% classified for safety, but *HC construct
   coverage* is what defines "grammar complete."
2. **100% lockstep with both engines, by reverse engineering — not design.** LCAtom's grammar API is
   *exactly* the set of LibLCM inputs `HCLoader` consumes: nothing less (the user could not control the
   grammar) and nothing pointless (controls wired to nothing). The authoritative artifact is the map
   *HC construct ← LibLCM fields actually read*, derived from
   `FieldWorks/Src/LexText/ParserCore/HCLoader.cs`, together with HC's own `Language` model from
   `../machine`. Both are versioned dependencies: when either changes, the map is re-derived and the API
   re-checked. See [HermitCrab projection](../hermitcrab-projection.md#lockstep-with-hermitcrab-and-the-fieldworks-grammar-creator).
3. **Judge model surface by what projects contain, not by what code permits.** Strata are the cautionary
   example: HC supports strata first-class and `HCLoader` can build extra ones from a
   `ParserParameters` XML string, so "HC supports it, therefore we must author it" *looked* compelling.
   But in every project sampled there are **zero `MoStratum` objects** and `ParserParameters` holds only
   `<XAmple>` tuning with no `<HC>`/`<Strata>` section — every real project runs on the three hardcoded
   strata. Stratum configuration is therefore not a v1 requirement; not disturbing the default three
   is. Coverage claims get checked against real `.fwdata`, not against what a code path allows.
4. **Iteration speed is a product requirement, not an optimization.** The loop is run repeatedly on
   one project, so assess → project → parse → compare must stay fast. This is what makes the
   incoming-reference warm-up ([ADR 0006](0006-engine-reality-apply-readback-preflight.md)) and
   footprint-scoped assessment load-bearing rather than nice-to-have.
5. **Comparability is a product requirement.** "Does it parse better?" is meaningless without knowing
   exactly what changed and what each report was measured against. That is precisely what change sets,
   effect digests, and provenance-stamped attachments provide — the safety apparatus doubles as the
   experiment record.
6. **Reversibility is a product requirement.** Most experiments are discarded. Non-committing
   assessment, atomic apply, and an honest applied-log are what make "try it and throw it away" safe.

### What this does not change

The lexical surface stays in scope and first in the build order — HermitCrab consumes lexical data
(entries, allomorphs, MSAs, morph types), so lexical completeness is a prerequisite for grammar
experimentation, not a parallel track. Non-HC consumers (Flexicon, FlexTools, Linguistic Assistant,
GramTrans, FieldWorks) remain supported; they are simply no longer co-equal with HC in setting
priorities.
