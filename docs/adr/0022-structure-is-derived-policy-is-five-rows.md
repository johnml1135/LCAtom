# ADR 0022 — The generator derives structure; the only hand-authored policy is naming, scope, and seven rows

**Status:** accepted, 2026-08-05. Amends [ADR 0014](0014-generate-the-crdt-layer-from-masterlcmodel.md)
decision 2. Answers `B7a`, and retires most of `B18`.

> **The filename still says `five-rows`.** This ADR was written asserting exactly five exceptions and the
> fail-closed check found two more on its first run against the real manifest — the episode decision 3 is
> largely about. The heading and the body were corrected to seven; the filename is deliberately left alone,
> because every inbound link in this repository uses it and a stale link is a worse failure than a stale
> slug. There have been seven since 2026-08-05. Do not re-derive "five" from the path.

## Context

ADR 0014 decision 2 drew the line as: *structure comes from `MasterLCModel.xml`; policy (`Scope`,
`Construct`, `ComparisonClass`, `Verbs`) comes from the manifest, which is human judgement and exists
nowhere else.* Everything downstream inherited that framing — including `B18`'s worry that 406 of 473
in-scope rows carry no citation, and `B7a`'s proposal to hand-audit the 61 rows whose values depart from the
common case.

Checked against the manifest, **two of those four columns are not judgement at all.**

`Verbs` is a pure function of two columns LibLCM itself declares:

| `Kind`/`Card` | `Verbs` | Rows |
| --- | --- | ---: |
| `basic` | `set\|clear` | 175 |
| `rel/atomic` | `set\|clear` | 45 |
| `owning/atomic` | `create\|delete` | 63 |
| `owning/col` | `create\|delete` | 36 |
| `owning/seq` | `create\|delete\|move\|reparent` | 32 |
| `rel/col` | `addRef\|removeRef` | 34 |
| `rel/seq` | `addRef\|removeRef\|move` | 27 |

**Seven combinations, zero exceptions, all 412 authorable rows.** It was typed out 412 times by hand.

`ComparisonClass` is nearly as mechanical — `seq` → `positional`, everything else → `unordered` — with
**seven exceptions in the entire manifest, in two opposite categories.** Five where order carries *more* than
position:

```
LexEntry.AlternateForms      feeding             allomorph order
PhPhonData.PhonRules         feeding             rule order encodes feeding and bleeding
PhSegRuleRHS.LeftContext     index-as-identity   alpha variables: position IS the identifier
PhSegRuleRHS.RightContext    index-as-identity
PhSegRuleRHS.StrucChange     index-as-identity
```

**And two where `card=seq` carries *nothing*** — corrected 2026-08-05, when the check found them on its first
run against real data:

```
PhPhonData.Contexts          unordered           pooled storage, not an order
PhPhonData.FeatConstraints   unordered           pooled storage, not an order
```

These are the *pooled-but-private* objects of [issue B9](../issues.md): a rule's private interior that lives in
a shared pool. They are `seq` in `MasterLCModel.xml` because a sequence is how LibLCM stores a pool, not
because position means anything — what matters is which rule references which context. So **`card=seq` does
not imply that order is meaningful**, and this ADR's original claim of "exactly five exceptions" was wrong in
the direction that matters: it asserted a completeness the data did not support.

**And the stakes attached to that column were inherited from a withdrawn design.** `ComparisonClass` was
introduced to decide which CRDT type each field merged with — `declarative-commands-vs-crdt.md` calls it
*"the exact column that decides which CRDT treatment"* a field gets, and the "412 of 473 commute natively"
figure is a statement about merge convergence. There is no CRDT on this path. A wrong `ComparisonClass` used
to risk silent divergence between replicas; now it risks a less helpful diff. Nothing in `src/`, `tests/`, or
`spikes/` reads the column at all.

## Decision

### 1. The generator derives `Verbs` and `ComparisonClass`

Both are computed from `Kind`, `Card`, and `Sig`. Neither is read from the manifest as an authority.

### 2. Seven rows are an explicit, cited exception table, in two named categories

Neither category is derivable, and they must not be flattened into one list, because they mean opposite
things: `feeding` and `index-as-identity` say order carries *more* than position; pooled storage says a `seq`
carries *nothing*. Each row cites which category it is in and why, so anyone adding an eighth has to say which
kind it is. Seven rows in the generator beats a column across 494.

### 3. The build fails on any unexplained departure

If a manifest row's `Verbs` or `ComparisonClass` disagrees with the derivation and the row is not in the
exception table, **the build fails, naming the row.** Same fail-closed shape as `MOT-2`'s `(Class, Field)`
join. A future LibLCM field inherits the gate automatically; nothing has to be remembered.

This replaces `B7a`'s hand-audit. Reviewing 61 rows would fix 61 rows once; deriving them fixes every row
forever, and costs less.

### 4. What remains genuinely hand-authored

Small, and each item is a decision rather than a transcription:

| Input | Why it cannot be derived |
| --- | --- |
| `Scope` / `ScopeReason` | Which of the 898 fields we expose is a product decision. |
| `Construct` | The middle segment of `lexical/lexSense/setGloss`. Only 26% of names match their class; 41% — `featureStructure` spans 16 classes, `ruleContext` 11 — have no mechanical relationship to any class. `B19`/`B20` stand unchanged. |
| Creation validity | A `create` must build a *valid* entity, and the model file does not encode validity. |
| Error and integrity handling | Per-family, hand-written, and expected to stay that way. |

## Consequences

- **`B7a` is answered without an audit**, and `B18` is largely retired: "406 of 473 rows lack a citation"
  stops mattering when 407 of them are computed rather than asserted. What survives of `B18` is the handful
  of rows where a citation would document a real decision — the seven exceptions and the `Scope` calls.
- **`MOT-2` grows one check** and `MOT-4` loses an input. The manifest keeps `Verbs` and `ComparisonClass` as
  *derived, checked* columns — useful for querying and for review, no longer trusted as authority.
- **`C10a` gets easier.** `AssessPoisonsCache` already had no consumer; with dry runs on a throwaway
  file-loaded scratch ([ADR 0016](0016-scratch-cache-copy-not-undo.md) as amended) it has no purpose either.
  Retire it in the same pass.
- **The check earned its keep on its first run**, which is the argument for keeping it fail-closed rather than
  advisory. It was specified as having exactly five exceptions and immediately found two more — a category the
  specification asserted did not exist. An advisory warning would have been read as noise and suppressed.
- **A lesson worth recording, since it recurred twice in one session.** The alarming version of `B7a` — "23%
  of the rows the generator trusts are flawed" — was true only while `ComparisonClass` decided merge
  behaviour. The number survived the architecture change; the consequence did not. When a decision is
  withdrawn, the *stakes* that decision created have to be re-derived too, not just the plan items.
