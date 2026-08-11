# Manifest trust audit — how much can the generator believe?

*Research of 2026-08-03, against all 473 `Scope=in` rows of
[`liblcm-inventory.tsv`](../../manifest/liblcm-inventory.tsv), cross-checked against
`MasterLCModel.xml` (5,368 lines), `HCLoader.cs` (2,837 lines), and liblcm's own `LingTests.cs`.
Nothing was modified.*

**This was the biggest hidden risk in Plan A**: the generator reads `ComparisonClass` and `Verbs`
directly, so a wrong row becomes wrong code. The finding is better than feared in one way and worse in
another — **the risk is concentrated, not diffuse.**

---

## 1. Evidence-quality census — issue B18 is wrong, pessimistically

Every in-scope `Rationale` classified by what evidence it actually contains:

| Tier | Definition | Count | % |
| --- | --- | ---: | ---: |
| **A — pinpoint citation** | names a file **and** line/range (`MasterLCModel.xml:1275-1279`) | 27 | 5.7% |
| **B — named source, no location** | names an artifact (`HCLoader`, `hc-grammar-map.md`) but no line | 40 | 8.5% |
| **C — generic template, zero citation** | boilerplate restating the classification | **406** | **85.8%** |

*Independently reproduced with a cruder regex: A=27 exact, B=47, C=399 — the headline holds.*

[`issues.md`](../issues.md) B18 claims *"~300 of 473 classified by heuristic."* The real
zero-citation count is **406 (85.8%)** — 106 rows and 22 points worse than the register admits. Even
counting Tier B as evidence leaves **446/473 (94.3%)** without a pinpoint citation. **B18 should be
corrected, not merely left open.**

The modal case, `CmMedia.Label`: *"Core authorable content of the media construct (basic/MultiString)."*
That is the classification restated as its own justification.

## 2. But `ComparisonClass` is almost entirely mechanical

The whole trust question collapses once you see how the column is produced — it is derived from `Card`
alone, with seven hand-written overrides:

| `Card` | → `ComparisonClass` | Count |
| --- | --- | ---: |
| `basic` / `atomic` / `col` | `unordered` | 405 of 412 |
| `seq` | `positional` (default) | 56 |
| `seq` | **override → `unordered`** | 2 |
| `seq` | **override → `feeding`** | 2 |
| `seq` / `atomic` | **override → `index-as-identity`** | 3 |

So there are two separate problems: (a) is `Kind/Sig/Card` extracted correctly — a checkable structural
fact; and (b) are the hand-classified rows right — a genuine judgment call.

## 3. Sample verification — 35 rows, stratified toward the risky stratum

**Structural layer: 22 of 22 exact matches, zero errors.** `PhSegRuleRHS.LeftContext`,
`LexEntryRef.ComponentLexemes`, `CmMedia.MediaFile` all confirmed against `MasterLCModel.xml`, and all
five Tier-A citations re-checked were byte-accurate. **A generator reading `Kind/Sig/Card` is on solid
ground.**

**Judgment layer: 3 clear errors, 2 conditionally-incomplete, out of 22 non-default rows sampled.**

Confirmed correct, with evidence the manifest itself never cited:

- `PhPhonData.FeatConstraints` → `unordered`. Backed hard by `LingTests.cs:150-256` (four `LT-22575`
  tests: identity is object-sharing, GC-by-last-referrer, not pool index). **The XML comment reads the
  opposite way and is stale — the manifest correctly ignored it.**
- `PhPhonData.Contexts` → `unordered`. `LingTests.cs:258-291` proves members are held by reference.
- `PhPhonData.PhonRules` → `feeding`. `MasterLCModel.xml:4496-4498`: *"given in the order in which they
  are to be applied."*
- `MoInflAffixTemplate.Prefix/SuffixSlots` → `HcReachable=yes`. `HCLoader.cs:297` literally reads
  `template.SuffixSlotsRS.Concat(template.PrefixSlotsRS.Reverse())`. And `Enclitic`/`Proclitic`/`Slots`
  → `no` is right, correctly *not* confused with the differently-scoped `MoInflAffMsa.Slots` — a good
  sign against the D1 name-collision failure mode.
- `MoMorphSynAnalysis.Components` → `positional`. **B17 oversold its own uncertainty**:
  `MasterLCModel.xml:3578-3584` says explicitly *"I have defined this as an ordered seq on the
  assumption that the order of the components will match the left-to-right order of the morphemes."*
  B17's *"no documented guidance found either way"* is not accurate.

Genuine problems:

| Row | Manifest | Problem |
| --- | --- | --- |
| `LexEntry.AlternateForms` | `feeding` | ~~Rationale claims order encodes disjunctive elsewhere-blocking, but `HCLoader.cs:744,1726` carry `// TODO: irregularly inflected forms should be handled by rule blocking in HC` — **the cited mechanism is not implemented.** Probably plain `positional`.~~ **This finding is withdrawn — see the addendum below.** The classification is correct; the TODO is a different mechanism. |
| `MoAlloAdhocProhib.Allomorphs` | `positional` | `MasterLCModel.xml:4548-4552`: order matters *only when* the sibling `MoAdhocProhib.Adjacency ≠ "Anywhere"`. **A flat per-field label cannot express a condition on a sibling field**, so a generator will over-diff harmless reorderings. |
| `MoAlloAdhocProhib.RestOfAllos` | `positional` | Presumed to inherit the same conditionality; no restated comment. |
| `LexEntry.MainEntriesOrSenses` | `positional`, `Verbs: n/a` | A `derived-read-only` row carrying a `ComparisonClass` no verb can invoke. Dead metadata, or a missing schema rule. |
| `MoGlossItem.GlossItems` | `positional`, `Verbs: n/a` | Same inconsistency. |

## 4. One reported "concrete manifest error" that is itself wrong

The audit flagged `PhSimpleContextNC.PlusConstr`/`MinusConstr` (`positional`, with a `move` verb) as a
**clear error**, citing `MasterLCModel.xml:4275-4286`: *"although this attr is defined as a collection
seq (not an ordered seq), the order is assumed to be stable."*

**The quote is real — I read it — but it argues the opposite of the conclusion drawn.** The very next
sentence is the reason: *"Otherwise the labels used in SPE-style rules would be subject to change."*

And the [`C13` investigation](2026-08-03-five-computable-grill-items.md#c13) independently found the
mechanism that makes this concrete. Verified at `OverridesLing_Lex.cs:7595-7626`, the alpha-variable
collector walks `PlusConstrRS` then `MinusConstrRS`, deduplicating by reference so **first appearance
wins** — and `HCLoader.cs:2003-2011` assigns Greek letters α, β, γ… in exactly that enumeration order.

**Reordering `PlusConstrRS` therefore renames the alpha variables in the generated rule.** Order *is*
meaning here. The manifest's `positional` classification is **correct**, and `move` is a legitimate
verb on it.

Worth stating plainly because it cuts both ways: the model comment's "not an ordered seq" phrasing is
the kind of thing that will mislead the next reader too, and this row deserves a Tier-A rationale
citing `OverridesLing_Lex.cs:7595-7626` so nobody re-derives the wrong answer.

## 5. Error-rate estimate

- Sampled **22 of the 61 non-`unordered` rows** (36% of the high-risk stratum) and 13 of 412
  mechanical-default rows (3.2%).
- Within the non-default stratum: **~5 of 22 wrong or incomplete (~23%)** after correcting §4.
  Extrapolated across all 61, roughly **12–15 rows** need correction.
- Within the 412 mechanical-default rows: **0 errors in 13 checked**, and the generating rule is
  trivial (`Card ≠ seq → unordered`). Confidence is moderate-to-good despite thin coverage, because it
  rests on `Card` extraction, which every structural check confirmed.

**Recommendation: 100%-review the 61 non-default rows before the generator ships from them; spot-audit
the other 412.** That is a bounded, one-sitting task — not a re-audit of the manifest.

## 6. Construct naming (`B19`) is understated, not overstated

53 distinct single-construct values. Testing whether each maps mechanically to a class name:

| Category | Count | % | Example |
| --- | ---: | ---: | --- |
| Exact `lowerFirst(Class)` | 14 | 26.4% | `lexSense` → `LexSense` |
| Prefix-strip (`Cm`/`Mo`/`Ph`/`Fs`) | 17 | 32.1% | `stratum` → `MoStratum` |
| **No mechanical relationship at all** | 22 | **41.5%** | `featureStructure` spans **16** classes; `ruleContext` 11; `msa` 9 |

Three things a human must supply that no generator can derive:

1. **The prefix table** (`Cm`, `Mo`, `Ph`, `Fs`) — a lookup, not a transform, and not present in the
   data.
2. **Which classes belong under one construct label** for the 41.5% with no name relationship. That
   grouping lives *only* in the hand-authored `Construct` column.
3. **A second, undocumented normalization.** B19's own example: `LexSense.Gloss` has
   `Construct=lexSense` — an *exact-match* case — yet ships as kind segment `sense`. So even the 26.4%
   bucket is not safe. No rule for when that second strip applies was found anywhere.

## 7. `B20`'s "17" reconciles exactly

The manifest has **19** rows carrying the pipe-joined construct string
`possibility|partOfSpeech|lexRefType|lexEntryType|lexEntryInflType|morphType|phonRuleFeat` — 18 on
`CmPossibility`, 1 on `CmPossibilityList.Possibilities`. B20's 17 excludes the 2 `derived-read-only`
rows (`DateCreated`, `DateModified`). **Worth stating in the issue rather than leaving it to be
re-derived.**

**And the ambiguity is not what it looks like.** Every one of the 19 is a plain structural field —
`Name`, `Abbreviation`, `SortSpec`, colors. The field has *one* meaning. `CmPossibility` is a single
generic class that FieldWorks reuses as storage for seven domain-facing lists. **The ambiguity is which
possibility-list instance an object belongs to at runtime** — determined by which
`CmPossibilityList.Possibilities` owns it, a runtime fact, not a schema fact.

So B20's "fan out to one kind per construct" is right in spirit but **cannot be done from the
`Class`/`Field` pair alone.** It needs the owning list's identity, which is not in this manifest at
all. Adjacent to B12's open-field-space problem.

## 8. Does the proposed change-class taxonomy partition the manifest? (`G27`)

The proposal's own row counts are **all five verified exact**: `set|clear` 220, `create|delete` 99,
`addRef|removeRef` 34, `create|delete|move|reparent` 32, `addRef|removeRef|move` 27 — plus 61 `n/a`.
*Independently reproduced.*

But the table assumes `Verbs` alone determines class membership. Cross-tabulating against `Group`:

| Bucket | Rows | % | Fit |
| --- | ---: | ---: | --- |
| Class 1/2 clean (`Group ∈ {grammar, lexical}`, simple verbs) | 246 | **52.0%** | Unambiguous |
| Same verbs, `Group ∈ {lists, system}` | 73 | 15.4% | **No home** — `lists` (39) is the *candidate* shared-vocabulary class; `system` (47) only partly overlaps the *candidate* schema/metadata class |
| Class 5 clean (`addRef\|removeRef`) | 34 | 7.2% | Fits |
| Straddles class 5 + ordering (`addRef\|removeRef\|move`) | 27 | 5.7% | One row, two buckets |
| Straddles class 1/2 + ordering + reparenting | 32 | 6.8% | One verb set, three buckets |
| Not authorable (`Verbs: n/a`) | 61 | 12.9% | All six classes describe *editable* operations |

**A clean 473/473 accounting — but only 52% lands in exactly one class.** 28.3% has no assigned bucket;
12.5% spans several simultaneously by construction of its `Verbs` value.

Three specific breaks:

- **The 73 `lists`/`system` rows have zero correspondence in the taxonomy table.** Their proposed home
  is explicitly labelled *candidate*, so 15% of the data is homeless by the proposal's own admission.
- **The "schema and metadata" candidate describes the wrong rows.** It is defined in prose around
  custom fields and writing systems — which per B12 have **zero manifest rows** (open field space). The
  manifest's actual `system` group is 47 `LangProject` config rows, a different set entirely.
- **Classes 3 and 4 have no evidence in this artifact at all.** `Segment`(10) `WfiAnalysis`(9)
  `WfiMorphBundle`(5) `WfiWordform`(4) `Text`(6) `CmAgent`(7) `StTxtPara`(7) = 48 rows, every one
  `Scope=out`, `not-domain-reachable`. **The cut cannot be tested for them against this manifest** —
  which is `H30`'s gate restated as a data fact.

---

## Bottom line

- **Trust `Kind`, `Sig`, `Card`.** ~22 direct structural checks, zero errors.
- **Do not trust `Rationale` as evidence.** 85.8% is boilerplate restating the classification.
- **The risk is 61 rows, not 473.** Review that stratum completely; spot-audit the rest.
- **B18 needs correcting** (406, not ~300); **B21 is closed** by the `B8` closure computation;
  **B20's 17 reconciles**; **B19 is understated**.
- **The proposed taxonomy does not partition the manifest** — 52% clean, and classes 3/4 have no data
  to test against at all.

---

## Addendum, 2026-08-11 — the `LexEntry.AlternateForms` finding in §3 is withdrawn

§3 listed `LexEntry.AlternateForms` as a genuine problem: its `feeding` classification cites disjunctive
elsewhere-blocking, and `HCLoader.cs:744` says `// TODO: irregularly inflected forms should be handled by
rule blocking in HC`, so the cited mechanism looked unimplemented. **Checked against HermitCrab's own source
this pass, the classification is right and the inference was wrong.**

The two are different mechanisms. That TODO sits among the `MprFeatures` assembled for a `LexEntryInflType`
(`HCLoader.cs:738-750`) and concerns `LexFamily`-level blocking of a whole irregular form — HC's
`Word.CheckBlocking`, which asks whether some *other entry* in the same family should win. It says nothing
about the order of allomorphs within one entry.

Intra-entry allomorph disjunction **is** implemented, and it is positional:

- `LexEntry.cs:45-50` (`SIL.Machine.Morphology.HermitCrab`) reassigns every `Allomorph.Index` from its
  position in the list on each mutation, so list order *is* `Index`.
- `Allomorph.cs:127-152` walks `Enumerable.Range(0, Index)` — every lower-indexed allomorph — and rejects
  the current one if an earlier one also matches and the two do not free-fluctuate, raising
  `FailureReason.DisjunctiveAllomorph`.
- HC's own class comment is explicit (`Allomorph.cs:9-11`): allomorphs "are applied disjunctively within a
  morpheme."
- `HCLoader.cs:263` then `:716-722` carries `AlternateFormsOS` order straight into `hcEntry.Allomorphs`.

One refinement to the manifest's wording rather than its classification: HC does **not** stop at the first
match. It generates a candidate from every allomorph (`Morpher.cs:361-369`) and filters afterwards through
`IsWordValid`. The earliest matching allomorph still wins, so `feeding` holds — but "tries them in order and
stops at the first that matches" described the effect rather than the code, and has been corrected in the
manifest.

**And a detail nobody had recorded:** `HCLoader.cs:263` iterates
`entry.AlternateFormsOS.Concat(entry.LexemeFormOA)`, appending the lexeme form *last*. It therefore receives
the highest `Index` and the **lowest** blocking priority of an entry's forms — which is worth knowing before
anyone reasons about an entry's lexeme form as though it were the primary candidate.

*Verified by a subagent against `repos/machine` and `repos/FieldWorks`; every citation above was re-checked
by hand before this addendum was written.*
