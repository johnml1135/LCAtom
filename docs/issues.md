# Issues register

Everything identified during design and review, with status. Issues found in **our** code, in the
**contract**, in **upstream** (LibLCM / FieldWorks / PanGloss), and in our own **tooling**. Upstream
hazards are recorded because we must defend against them, not because we can fix them.

Status: `open` · `in-progress` · `fixed` · `wontfix (recorded)` · `upstream`

---

## A. Defects in shipped LCAtom code

| # | Issue | Status |
| --- | --- | --- |
| A1 | **Store cannot express amend.** `objects/<changeSetId>.json` is keyed by the frozen id, so amending would mutate a supposedly immutable object. Fix is the git model: object keyed by `intentDigest` (write-once), manifest keyed by `changeSetId` holding a movable pointer. | in-progress |
| A2 | **`apply` is unbound.** `ChangeSetApplier.Apply` takes no Assessment, so [ADR 0004](adr/0004-prerequisite-graph-stable-ids-bound-apply.md) §3's "a bare apply is a hard error" is unenforced and the assess→apply TOCTOU window is open. | in-progress |
| A3 | **`RollbackCacheInvalidator` commits a mutation.** It calls `ILexEntryRepository.ResetHomographs`, which internally runs `LexDbOA.ResetHomographNumbers` — opening its own unit of work and committing a **project-wide homograph renumber** — inside what should be a cleanup path. | in-progress |
| A4 | **Assess can poison derived caches.** Assess mutates then rolls back on every call, and `UndoStack.Rollback` (unlike `Undo`) skips the forward-only-hook cache invalidation, leaving headword / homograph / monomorphemic caches stale. Dormant today only because `setGloss` touches none of them; **fires the moment a lexeme-form or citation-form operation exists**, which is the next operation we add. A second assess on the same cache could then return a different effect digest for identical state — breaking the determinism clause and "re-run and confirm no change." | in-progress |
| A5 | **CLI draft file contradicts the in-memory-draft decision.** `drafts/<name>.json` exists because each CLI verb is a separate OS process, but S3 says drafts never touch disk. Documented as a deliberate session shim; a library or daemon consumer must keep drafts in memory. | fixed (documented) |

## B. Contract and design gaps

| # | Issue | Status |
| --- | --- | --- |
| B1 | **Contract lags the ADRs.** The ten-verb vocabulary, generated kinds, two field spaces, declared-vs-discovered footprint, drift taxonomy and more live only in ADRs. The contract is what implementation follows. | fixed |
| B2 | **`ensure` is a required verb with no home.** Custom fields need tri-state semantics and crash-retry safety depends on it, but `create`'s idempotent-reuse path is keyed by **canonical GUID** while custom fields have **no durable GUID** and match on `(class, name)` — a different identity axis, previously unreconciled. | fixed |
| B3 | **Owning-atomic replacement undefined** across 69 in-scope fields — the everyday "change which allomorph is the lexeme form" edit. Without a rule, a raw `create` silently reintroduces the MSA-orphan bug class. | fixed |
| B4 | **Binary effect-digest equality is too coarse for approval continuity.** Needs the four computable drift classes plus a bulk-approvable path for same-nature-wider-scope. | fixed |
| B5 | **No `Info` outcome category.** "Checked and confirmed harmless" is currently conflated with "nothing needed checking." | fixed |
| B6 | **Dangling and engine-nulled references share one severity bucket.** A dangling reference is materially worse and must be distinguished. | fixed |
| B7 | **No Integer enum table.** The `(Kind, Card, Sig)` triple cannot distinguish a closed enumeration (`PhSegmentRule.Direction`, `MoAdhocProhib.Adjacency`) from a magnitude (`CmPicture.ScaleFactor`). 30 in-scope `Integer` fields would invite magic-number authoring. Needs a manifest column. | open |
| B8 | **Third comparison class missing.** Index-as-identity (alpha variables) is neither unordered, positional, nor feeding, and its reach is not neighbour-limited. | fixed |
| B9 | **Third ownership mode missing.** Pooled-but-private objects (`PhPhonData.Contexts`, `.FeatConstraints`) are a rule's private interior but live in a shared pool, so the fill/frame ownership test gives the wrong answer. Deleting the owner also orphans pool members that were never the delete's target. | fixed |
| B10 | **`reparent` confirmed only for `owning/seq`.** All three evidenced examples are sequences; atomic and collection reparent are plausible but unevidenced. Needs a conformance vector before being promised. | open |
| B11 | **Batch-scoped composers are unaddressed.** Bulk POS/inflection-feature assignment must see the whole selected batch grouped by owning entry to get MSA reuse right — not N independent composer calls. | open |
| B12 | **Writing systems and custom fields have no inventory row.** `LangProject.*Wss` are space-joined ID strings; both families are real but not derivable from the manifest. This is the open field space. | open |
| B13 | **Cross-process protocol unspecified.** Framing, error/exit-code contract, and one-shot-vs-daemon for the Python/Rust consumers. A hybrid was recommended (store ops one-shot; project ops via a per-project daemon that *is* the exclusive-write guarantee). | open |
| B14 | **Resource/DoS bounds are a TODO with no numbers** for untrusted agent-authored change sets. | open |
| B15 | **Diagnostics i18n undecided** — language and localisability of human-readable explanations. | open |

## C. Upstream hazards we must defend against

Not ours to fix; the obligation is to detect, disclose, or refuse.

| # | Hazard | Defence |
| --- | --- | --- |
| C1 | **`AddCustomField` inside an open unit of work corrupts the project** (Flexicon lost 1,392 senses). | Schema ops run first in their own non-undoable unit of work; never save mid-task ([ADR 0005](adr/0005-schema-operations-non-undoable-uow.md)). |
| C2 | **Single-writer is not enforced.** A colliding writer calls `Rollback(0)` and destroys the entire open change set, indistinguishably from our own rollback. The 1-second autosave is benign; a shutdown save on a background thread is not. | Host must guarantee exclusive write access; runner treats an unexpected transaction state at task end as an external collision. |
| C3 | **`ReferringObjects` has a first-touch whole-project cost** — `GetIncomingFields` walks to `CmObject`, force-fluffing every instance of classes carrying generic `sig="CmObject"` fields. | Host warms the incoming-reference index at load, off the interactive path. |
| C4 | **De-referencing does not cascade** — clearing a reference to an owned-collection member orphans it. | Composers emit an explicit `delete`; effects disclose the orphan. |
| C5 | **24 alpha variables maximum per rule**, and the ceiling is **per-rule**, counted by traversing `StrucDesc` then each `RightHandSides` context slot in order. Exceeding it throws and kills the whole grammar load. | Pre-apply check simulating that exact traversal. |
| C6 | **MPR referential integrity is unguarded** — ~16 raw dictionary indexers mean any dangling inflection-class / prod-restrict / inflType reference throws and kills the load. Happens in the wild: `GenerateHCConfig` crashes on the Amharic project via a stale `MoMorphAdhocProhib`. | Pre-apply referential-integrity validation. |
| C7 | **An invalid environment string becomes "applies everywhere"** — invalid data silently becomes *more* permissive. | Validate against the `PhonEnvRecognizer` grammar and surface the widening. |
| C8 | **Silent-loss surface**: one bad phoneme makes a whole natural class permanently unusable; one unloadable form drops an entire adhoc prohibition; entries/rules/templates vanish when they end up empty; a stem name with no regions vanishes; natural-class abbreviations collide with last-one-wins. | Predict and report each as part of assessment. |
| C9 | **`PhSegmentRule.InitialStratum`/`FinalStratum` are silently ignored** despite the model comment promising per-rule stratum scoping. `MoStratum` is read nowhere. | Not offered as controls. |
| C10 | **`MoStemName.DefaultAffix`/`DefaultStem` unread** — only `Regions` drives suppletion, so a fallback form is inexpressible. | Documented as unreachable. |
| C11 | **`MoGlossItem`'s entire 10-field gloss system is never consulted** — the gloss HCLoader uses comes from `LexSense.Gloss`. | Documented; don't offer the dead path. |
| C12 | **`InvalidRewriteRule` is declared but never invoked**; `ConsoleLogger` throws `NotImplementedException` on `UnmatchedReduplicationIndexedClass`, so a bad reduplication index crashes the CLI exporter while loading fine in FLEx. | Don't rely on the logger surface for completeness. |
| C13 | **On-disk save is not atomic** — temp file then two separate `File.Move`s. | Host owns save/backup. |
| C14 | **Realizational morphology is unreachable** — `HCLoader` carries `// TODO: use realizational affix process rules`, so paradigmatic blocking and `LexFamily` suppletion cannot be expressed however we write LibLCM. | Documented ceiling; never promised. |

## D. Our own tooling limitations

| # | Issue | Status |
| --- | --- | --- |
| D1 | **The HCLoader extractor matches field *names*, not `class.field`.** The never-referenced set (326) is therefore precise, but the referenced set (152) is a conservative over-approximation. Per-class precision needs the curated map. | wontfix (recorded) |
| D2 | **Method-mediated reads are invisible to the extractor.** `PhMetathesisRule.LeftSwitchIndex`/`RightSwitchIndex` are read via `GetStrucChangeIndices()`; patched by hand. Others may exist. | open |
| D3 | **A regex bug silently dropped every relation accessor** in the first extraction run (optional-suffix group let the greedy capture swallow `...OC`/`...RA`), reporting 32 distinct fields instead of 105. Fixed; recorded because the failure mode was silent and plausible-looking. | fixed |
| D4 | **Scope by naming heuristic was wrong in both directions** — admitted the derivation-trace family, excluded `CmPossibility`/`CmMedia`/`CmResource`. Replaced by computed owning-edge reachability. | fixed |

## E. Corrections made to our own documents

Recorded because each was asserted confidently before being checked.

| # | Correction |
| --- | --- |
| E1 | Stratum guidance was written from what the code *permits* rather than what projects *contain*. Every sampled project has **zero `MoStratum` objects** and a `ParserParameters` holding only `<XAmple>` tuning. General rule adopted: coverage claims are checked against real `.fwdata`. |
| E2 | "HCLoader reads all five slot sequences" was **wrong** — an exhaustive grep finds zero references to `Slots`, `ProcliticSlots`, `EncliticSlots`. Only `PrefixSlots` and `SuffixSlots` are read. |
| E3 | The index-as-identity ordering mode was located on `PhPhonData.FeatConstraints`; it actually belongs to the **per-rule** traversal of `StrucDesc`/`RightHandSides`, so a `move` on the pool is inert. |
| E4 | "Read back, not replay" was described as consuming a LibLCM change feed; no such feed is reachable, so it is implemented as a footprint-scoped snapshot diff. |
| E5 | ADR 0005 claimed a leftover custom field is "idempotently reusable on retry"; `AddCustomField` **throws** on a duplicate name, so retry safety depends on the ensure pre-check. |
| E6 | `convert`/`replace` was framed as two different operations; it is one mechanism with two parameters (target class, target GUID), dispatching to FieldWorks' native call per construct. |
| E7 | The kind-count estimate was a 2× undercount (445 basic properties only, ignoring 453 relations). |
