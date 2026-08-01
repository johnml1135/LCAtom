# The HC surface scope — three tiers and the real ceiling

What Motif can actually let a user change, grounded in reads of HermitCrab (`../machine`),
`HCLoader.cs` (`../FieldWorks`), and PanGloss (`../PanGloss`). Companion to the field-level
[HC grammar map](hc-grammar-map.md); this document is the **construct-level coverage scope**.

Coverage is not one number. It is three nested tiers:

- **T1 — HC engine.** What HermitCrab can represent.
- **T2 — HCLoader-reachable.** What a FieldWorks project can make HC build. `T2 ⊂ T1`.
- **T3 — PanGloss Phase A.** What that consumer compiles today from `.fwdata`. `T3 ⊂ T2`.

> **Full coverage is defined as T2 — "C# HCLoader complete."** Everything `HCLoader` can produce from a
> FieldWorks project must be authorable in Motif, in a friendly way.

**T3 is not a scope boundary.** PanGloss is catching up to full HC, so its Phase A/B split is a moving,
temporary limitation of one consumer; designing the coverage target around it would bake in a boundary
that dissolves. T3 serves two other purposes instead:

1. **Sequencing.** T3 constructs reach a parse today, so building them first buys the fastest feedback
   loop. That is a priority signal, not a gate.
2. **A reporting obligation.** Because Motif knows the tiers, it must *say* when an authored construct
   is outside the consumer's current compile set — "you authored a metathesis rule; this PanGloss build
   compiles Phase A and will skip it." Same actionable-refusal discipline as declared group versions:
   never let a change silently fail to reach the parse.

**T1∖T2 is out of scope** — multi-stratum, realizational morphology, and multiple phoneme sets are
structurally unreachable from a FieldWorks project, and Motif does not promise them.

## The matrix

| Construct | T1 HC | T2 HCLoader | T3 PanGloss A | Note |
| --- | :-: | :-: | :-: | --- |
| Rewrite rules (all four SPE cases) | ✓ | ✓ | ✓ | one construct covers feature-change / epenthesis / deletion / narrowing, selected by LHS-vs-RHS node count |
| Affix process rules + 4 output actions | ✓ | ✓ | ✓ | `CopyFromInput`, `InsertSegments`, `InsertSimpleContext`, `ModifyFromInput`; **no delete action** — deletion is omitting a copy |
| Natural classes (intensional *and* extensional) | ✓ | ✓ | ✓ | `NaturalClass` = hand-authored FeatureStruct; `SegmentNaturalClass` = enumerated |
| Compounding rules | ✓ | ✓ | ✓ | HC has head/non-head/output MPR sets + separate required FSs; FW exposes less |
| Affix templates + slots | ✓ | ✓ | ✓ | HC slots are **one flat ordered list**; a slot holds a **set of competing rules** |
| Co-occurrence rules (5 adjacency modes) | ✓ | ✓ | ✓ | from `MoAlloAdhocProhib` / `MoMorphAdhocProhib` |
| Alpha variables / feature agreement | ✓ | ✓ | ✓ | real unification variables; **24-name ceiling**, exceeding it crashes the load |
| Stem names (region-gated suppletion) | ✓ | ✓ | ✓* | *no reference grammar exercises it — untested in practice |
| Disjunctive allomorph order / free fluctuation | ✓ | ✓ | ✓ | order is **semantic** (elsewhere-blocking), not positional |
| Allomorph environments | ✓ | ~ | ~ | HC allows arbitrary compiled patterns; FW only the `PhonEnvRecognizer` **string** grammar |
| MPR features / groups | ✓ | ~ | ~ | HC groups have configurable `MatchType`/`Output`; HCLoader hardcodes exactly three groups |
| **Metathesis rules** | ✓ | ✓ | **✗** | Phase B — warns and skips |
| **Reduplication** (bracket-pattern forms) | ✓ | ✓ | **✗** | Phase B. HC has no reduplication *type* — it's the idiom of naming an LHS part and referencing it twice in the RHS, plus `ReduplicationHint` |
| **Circumfix cross-products** | ✓ | ✓ | **✗** | Phase B |
| **Clitic-as-affix / clitic stratum placement** | ✓ | ✓ | **✗** | Phase B |
| **Multi-stratum (Linear/Unordered, per-stratum inventories)** | ✓ | **✗** | ✗ | 3 hardcoded strata; user `<Strata>` strings are also Phase B |
| **Realizational rules + `LexFamily` suppletion** | ✓ | **✗** | ✗ | HCLoader has `// TODO`; PanGloss's *engine* ported it, but nothing can feed it |
| **Multiple phoneme sets / per-level inventories** | ✓ | **✗** | ✗ | `PhonemeSetsOS[0]` only |
| prefix/suffix/proclitic/enclitic partition | **✗** | ✓ | — | FieldWorks-only. HC has no such concept — affix position emerges from where inserted material sits relative to the root copy |

## What the T1∖T2 gaps actually cost

- **Multi-stratum.** `Stratum` owns the lexicon, its own phonological rules, morphological rules,
  templates, and exactly one character-definition table; `Language.Strata` order is a strict pipeline
  where stratum *k* sees only the output of *k−1*. Losing it means: no level-1/level-2 affixation split,
  **no per-level segment inventories**, no interleaving phonology between two morphological operations
  in the same bucket, no bracketing paradoxes.
- **Realizational morphology.** Fires per *unrealized inflectional feature* rather than per slot,
  enabling paradigmatic blocking and **suppletive-stem selection from a `LexFamily`** (*go/went*,
  *feet*). Without it you cannot express "plural is realized either by *-s* or, for this lexeme, by the
  suppletive stem" — every realization must be hard-wired to a specific affix pattern.

Both are **structural FieldWorks limits**, not Motif choices. They bound the product honestly.

## Scope consequences

1. **Do not build HC XML export as the primary path.** PanGloss labels `.xml` *"legacy … being sunset"*;
   the forward path is direct `.fwdata` (`pg-fwdata` → `compile_project`, itself a port of `HCLoader.cs`).
   So the loop is *Motif applies to `.fwdata` → PanGloss reads `.fwdata`*. **PanGloss is the projection** —
   "show me the resulting grammar" is a PanGloss call, not an Motif feature. This deletes the plan's
   forward-projection-by-harvesting-`GenerateHCConfig` work. XML remains only for the C# oracle.
2. **Build to T2; sequence by T3.** All of T2 is in scope, including metathesis, reduplication,
   circumfix cross-products, and clitic-as-affix — no waiting on PanGloss. T3 constructs go first for
   the fastest feedback loop, and anything in T2∖T3 carries the consumer-capability warning above.
   T1∖T2 is documented as out of reach, not roadmapped.
3. **Author environments as strings**, against the `PhonEnvRecognizer` grammar — HCLoader never reads the
   structured `PhEnvironment` context graph.
4. **The five parallel slot sequences are FieldWorks sugar with no HC counterpart.** Either preserve them
   as authoring convenience over HC's flat position-derived model, or drop them. They cannot be validated
   against HC semantics because HC has no such distinction.
5. **Allomorph order joins the semantically-ordered comparison class** — it encodes elsewhere-blocking, so
   reordering changes meaning. Second counterexample to the plan's "feeding = `PhonRules` only."
6. **Motif owns the record, not the verdict.** PanGloss states repeatedly that consuming applications own
   history, baselines, publication policy, and UI; that it *"never asserts a root cause,"* renders
   *"never a publish/deny decision,"* and will *"not claim that one grammar edit caused a semantic
   change."* Its `diagnostics.rs` confirms **no report-to-report comparison exists yet**. The comparable
   that *does* exist: `AssessmentReport` (schema-versioned JSON) with per-word `gloss_signature`,
   `candidates_generated`, `confirmed`, and a `{complete, incomplete}` summary, plus `BuildReport` counts
   and `load_warnings` — a real schema Motif can carry as a labelled attachment and whose declared
   values it can surface as typed metrics.

   **Correction (2026-07-27).** This section previously read *"'Did it parse better' = diffing gloss
   signatures and confirmed counts across runs — Motif's job."* That was wrong and contradicted
   [stage-2 S5](stage2-change-management.md#s5--attachments-derived-provenance-stamped-store-not-interpret)'s
   `[core]` "stores and lists by provenance; **it never interprets**". S5 holds. Motif stores the
   reports, shows how a *declared* metric moved between two change sets, and renders them — it does
   not parse tool output, does not compute a verdict, and does not decide whether a grammar got
   better. See [ADR 0011](adr/0011-experiment-loop-boundary-motif-is-the-record.md).

## The oracle

HC's **conformance suite is directly reusable**. Each fixture is `grammar.xml` + `words.yaml`;
`FixtureMaterializer` derives an expected TSV and comparison is a `(word, status, signature)` multiset —
and critically the harness **mechanically re-derives ground truth from `grammar.xml` itself**
(`RequiresDerivation.Derive`) instead of trusting hand-authored metadata. That is exactly the
author-a-change → project → compare shape. Coverage is 19/19 in-scope constructs. One caveat to inherit:
adapter/`batch` mode can never produce a `guess:true` parse; in-process self-check can.

## XML fidelity, for the oracle path only

The DTD is **enforced** (`ValidationType.DTD`), so malformed documents fail rather than misload. The XML
is a near-complete authoring surface for what the model implements, with three real asymmetries:
**`isActive` is loader-only** (the writer never emits it, so disabled content is irrecoverably lost on a
round trip); the **writer silently drops co-occurrence rules whose targets weren't written**, meaning an
in-memory grammar built by `HCLoader` can hold state the XML cannot express; and **bracket pattern
shorthand (`[NC]`, `([NC])`, `[NC]*`) is input-only sugar** with no inverse. Dead on both sides and not
worth chasing: `SyntacticRules`, all subcategorization markup, `cyclicity`,
`phonologicalRuleOrder`, `obligatoryHead/FootFeatures`, `PreviousWord`/`NextWord`.

## Decisions (settled)

1. **`.fwdata` is the only output.** Defining full coverage as HCLoader-complete makes direct HC XML
   authoring unnecessary — its only purpose would be to exceed T2, which is explicitly out of scope. XML
   remains solely for the C# conformance oracle.
2. **No gating on PanGloss.** Build all of T2; sequence T3 first; warn when an authored construct is
   outside the consumer's compile set.
3. **Keep the prefix/suffix partition — but only those two.** `HCLoader` reads exactly
   `SuffixSlotsRS` and `PrefixSlotsRS`, collapsing them as
   `SuffixSlotsRS.Concat(PrefixSlotsRS.Reverse())` (closest-to-stem first in both). An exhaustive grep
   finds **zero** references to `Slots`, `ProcliticSlots`, or `EncliticSlots` — those three are
   FieldWorks UI/legacy surface, invisible to the parser, and the API should not promise them. Since HC
   has no partition concept at all, the validation target is **HCLoader's collapse behavior**, not HC
   semantics: authoring must produce the intended flat slot order after that transform.
