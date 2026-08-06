# ADR 0024 — The name's group is derived; the linguistic domain is editorial metadata

**Status:** accepted, 2026-08-05. Completes the mechanisation begun in
[ADR 0022](0022-structure-is-derived-policy-is-five-rows.md) and
[ADR 0023](0023-derived-kind-names-required-descriptions.md): **no part of a hashed identifier is
hand-authored any more.** Amends [ADR 0018](0018-change-class-is-two-axes-not-one.md) — its domain axis is
this ADR's `domain` column, not the kind's first segment.

## Context

ADR 0023 derived the middle segment of `group/construct/verb` and left the first one hand-typed. `Group` has
four values (`grammar` 230, `lexical` 157, `system` 47, `lists` 39) and it is hashed, so the identifier was
still not fully mechanical.

**A mechanical rule was written and tested against all 473 in-scope rows: 88.8%, with 53 genuine
disagreements** (a first attempt scored 81.8%, but 35 of those misses were a bug in the test's own prefix
table — `Cm*` mapped to `system`, which is wrong for `CmPossibility`). The surviving 53 fall into three
clusters, none of them a table bug:

| Rows | Pattern |
| ---: | --- |
| 21 | The field points into another domain, and the manifest follows the **owner** |
| 15 | `Mo*` classes the manifest calls **lexical** — allomorphs, morph types — while calling other `Mo*` grammar |
| 14 | Generic types (`CmPicture`, `CmMedia`, `CmTranslation`) grouped by **who uses them**, not what they are |

The 15 `Mo*` rows are not sloppiness: `Mo` is *morphology*, which genuinely straddles both sides — an
allomorph of an entry is lexicon, an inflectional template is grammar. A prefix cannot know which.

And one row pair forecloses any rule at all:

```
LexEntry.LexemeForm            Sig=MoForm              Group=lexical
LexEntry.MorphoSyntaxAnalyses  Sig=MoMorphSynAnalysis  Group=grammar
```

Same owning class, both pointing at grammar-shaped objects, different groups. No function of
`(class, field, target)` yields both. The distinction is *"is this part of the entry, or grammar hanging off
the entry"* — a linguist's judgement, not a fact about the model.

**And that judgement is load-bearing.** `Group` is not only a name segment; it is the versioning unit
(`contractVersions` maps group → major/minor) and the domain axis of ADR 0018's change classes, which is what
would route a Proposal to the right reviewer and set its risk tier. So the 53 rows where a rule disagrees are
**exactly the rows it would misroute** — a lexeme-form edit sent to a grammar reviewer.

Which means "make it mechanical" and "keep the judgement" were never in conflict. It is the same defect ADR
0023 found: one string doing two jobs with different requirements.

## Decision

### 1. Two fields, not one

| | Derived? | Hashed? | Job |
| --- | --- | --- | --- |
| **`group`** — first segment of the kind | **yes** | yes | Namespacing, and versioning granularity |
| **`domain`** — editorial | no, hand-authored | **no** | Review routing and risk tiering |

```
kind:   grammar/moForm/setForm     ← derived, hashed, never argued about
domain: lexical                    ← editorial, not hashed, free to be re-cut
```

The manifest's existing `Group` column is **renamed `domain`** and keeps its hand-authored values, including
all 53 rows a rule cannot reproduce. Nothing is lost; it stops being an identifier.

### 2. The derived rule is the declaring class prefix, and nothing else

One closed table over LibLCM's own class-name prefixes, applied to the class where the field is declared. **No
owner fallback, no target inspection, no exceptions** — the test rule used a fallback chain to try to
*reproduce* the hand-made answer, and that requirement is now gone, so the rule can be as simple as possible.

Accuracy against the old column is no longer a criterion. It is a different question with a different answer.

### 3. Each field answers a different question, and both answers are right

- **`group` answers "what changes together when LibLCM changes."** Derived from LibLCM's own class families,
  so a LibLCM upgrade that touches `Mo*` classes maps onto a version bump for the `Mo*` group. That is a
  *better* versioning boundary than linguistic domain, because upgrades arrive as classes, not as linguistics.
- **`domain` answers "who should look at this."** That is the linguist's judgement and it stays.

### 4. ADR 0018's domain axis is this `domain` column

ADR 0018 said a change class is a `(domain, shape)` pair and that **"both axes are already segments of ADR
0009's kind name."** The second half is now false: `domain` is separate metadata. The finding it rests on —
21 populated cells, all 473 rows, zero straddle — is unaffected, because it was computed from the `Group`
column that is now `domain`.

## Consequences

- **No hand-authored value feeds a hashed identifier.** Across ADRs 0022–0024: verbs derived, comparison
  behaviour derived with five cited exceptions, construct derived, group derived. The manifest's remaining
  authorities are `Scope` (what we expose), `Construct` (what ships together), and `domain` (who reviews) —
  none of which is hashed.
- **`MOT-2` checks the derived group** the same way it checks the others, and fails the build on a class whose
  prefix is not in the closed table.
- **The derived group is partly redundant with the class segment, and that is accepted.** In
  `grammar/moForm/setForm`, `moForm` already implies grammar — both come from the same class. The group earns
  its place as a versioning handle and a namespace, not as information. If it later proves to be pure noise,
  dropping it is a rename, which is cheap now and expensive after the contract stabilises (`B9b`).
- **The name and the routing will visibly disagree on 53 rows.** `grammar/moForm/setForm` carrying
  `domain: lexical` looks wrong at a glance and is correct: the name says which LibLCM family it belongs to,
  the domain says who should review it. Anyone reading a kind and inferring the reviewer from it will be wrong,
  so tooling should surface `domain` wherever it surfaces the kind.
- **A risk worth stating:** two fields that must not drift, and nothing forces them to agree — because they
  *shouldn't* agree. The mitigation is that neither is derived from the other, so there is no sync to break;
  the failure mode is a stale `domain`, which misroutes a review rather than corrupting data.
