# Flexicon harvest

Flexicon (`C:\Users\johnm\Documents\repos\flexicon`; Python, LGPL-2.1, © Craig Farrow) is a
battle-hardened LibLCM writer whose lexicon, grammar, and list operations are intended to move
through Motif. Its source encodes hard-won LibLCM ordering, cascade, and identity rules learned from
real data loss. This document harvests that knowledge.

**Reuse mechanism: learn-from, not copy.** Unlike `FwDataMiniLcmBridge` (C#/MIT, copy-and-adapt per
[ADR 0003](adr/0003-feasibility-findings.md)), Flexicon is Python/LGPL-2.1 — no line ports into
Motif's C# core. What is valuable is the scar tissue below, re-implemented independently.

## Coverage gaps — constructs moving through Motif the spec does not yet cover

- **Reversal index entries** — create, sense-linking, subentries. No operation family exists yet.
  `ReversalIndexEntry.EntriesOC` is owning, `SensesRS` is a pure reference: deleting a sense orphans
  the reversal entry (no cascade). Added to the operation vocabulary as an in-scope family to specify.
- **Publication flags** — `DoNotPublishInRC` / show-in-dictionary on entries and senses. Missing from
  both tools; in scope. Added to the operation vocabulary.
- **Reference-collection validation** — `LexReference` enforces a floor of two members and
  homogeneous target type (all-`LexSense` or all-`LexEntry`); generic add/remove needs this
  per-construct validation in the coverage manifest, not just generic collection semantics.
- **Non-uniform list roots** — `AnalyzingAgentsOC` is a bare `LcmOwningCollection` with no
  `PossibilitiesOS`; per-chart `ICmOverlay` has no project-level list. Any generic possibility-list
  family must not assume every list has `PossibilitiesOS`.
- **Writing-system lifecycle** — create/delete/set-default, full-list vs current-list sync. **In
  scope**: Motif bootstraps projects, so this is an operation family. Creation is a two-step
  `Create(tag)` then `Set(ws)` (`System/WritingSystemOperations.py:246-267`); skipping `Set` leaves a
  detached writing system that errors on the next FLEx open, and the current-vs-full list must be kept
  in sync via `AddToCurrent*WritingSystems`, not raw string-list assignment.
- **System-list deletion guard** — Flexicon's `DeleteList` refuses on well-known roots
  (SemanticDomainList, PartsOfSpeech, …); Motif's generic delete family has no such policy guard.

## Harvested gotchas

### Transaction-critical (validate before v1)

1. **`AddCustomField` inside an open unit of work corrupts the project.** `CustomFieldOperations.py:280-326`,
   `docs/CUSTOM_FIELDS.md:27-51`: Flexicon refuses schema mutation while `CurrentDepth>0` because it
   once stranded 1,392 senses (issue #21). In-UoW `AddCustomField` creates the flid in-memory only;
   `SaveChanges` throws "Commit at wrong place"; the `.fwdata` persists data referencing a field whose
   schema addition never saved. Resolved by
   [ADR 0005](adr/0005-schema-operations-non-undoable-uow.md): mirroring FieldWorks, the custom-field
   family runs first in its own non-undoable unit of work, one-way, never saving while a task is open.

### Delete / de-reference closure

2. **Stratum delete leaves dangling `StratumRA`** on affix templates, MSAs, compound rules, and
   phonological rules with no LCM cleanup (`Grammar/StratumOperations.py:222-229`) — surfaces later as
   silent HermitCrab parser failure. Test against Motif's delete-closure assessment.
3. **De-referencing an owned object does not cascade** (`Lexicon/MSAOperations.py:643-717 RemoveOrphaned`,
   `Grammar/PhonologicalRuleOperations.py:869-955`): clearing/replacing an atomic reference to an
   owned-collection member leaves it as an orphan needing an explicit compensating sweep. Folded into
   [Ownership and delete](change-set-contract.md#ownership-and-delete).

### Ownership / creation ordering

4. **First `ComponentLexemesRS` member becomes Primary implicitly** (`Lexicon/LexEntryOperations.py:2698,2733-2742`)
   — an ordering side-effect, not a flag. Sequence-insert lowering must special-case position 0.
5. **Attach owned child before setting its properties** (`Lexicon/VariantOperations.py:412-424`):
   a `LexEntryRef` must be added to `EntryRefsOS` before its `RS` properties are touched, or LibLCM
   throws a native `NullReferenceException`. One concrete instance of the general fill-ordering rule.
6. **Write phonological context to the correct owner** (`Grammar/PhonologicalRuleOperations.py:1076-1079`):
   `SetLeftContext`/`SetRightContext` are disabled in Flexicon after an earlier version wrote to
   `ctx.LeftContextOA` instead of `rhs.LeftContextOA`, producing rules that silently never ran. Direct
   confirmation that HermitCrab "fill" (owned interior authored by `Expand`, never caller-wired) is right.

### Model-awareness / validation

7. **Right factory and collection per destination** (`Grammar/GramCatOperations.py:184-209`): top-level
   feature-structure categories go in `MsFeatureSystemOA.TypesOC` (unordered, typed), subcategories in
   `SubPossibilitiesOS` (ordered) — the wrong one silently violates a typed-collection invariant
   (issue #163). Operations must be model-aware per construct, not "add to a collection by name."
8. **Don't infer optionality from CLR type** (`Lists/AgentOperations.py:399-405`): `ICmAgent.Human` is
   a non-nullable bool; `is not None` was always true and misclassified parser agents. Check LibLCM
   metadata for unset-vs-set, don't read it off the scalar type.

## Corroborations — Flexicon independently validates existing Motif decisions

- **NFD normalization** (`Shared/string_utils.py:55-56` and 5+ call sites): unnormalized-NFC lookups
  silently miss LibLCM's NFD storage. Confirms the canonical snapshot's NFD rule is necessary.
- **Custom-field three-identity model** (`docs/CUSTOM_FIELDS.md`): flid runtime / (ownerClass,name)
  physical / no durable GUID, plus mark-for-deletion over raw delete — near-exact match to
  [custom-fields](custom-fields.md), independently arrived at.
- **Homograph number is LCM-computed**, never set manually — confirms the `derived-read-only` class.

## Verdict

Harvest as documentation, re-implement independently. The two items that touch Motif's core
guarantees — #1 (schema mutation vs the outer unit of work) and #2/#3 (delete/de-reference closure
completeness) — deserve Phase 0 validation and, if confirmed, their own ADR before the contract locks.
