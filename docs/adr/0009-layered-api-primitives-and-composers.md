# ADR 0009 — Layered API: primitives, composers, and generated kinds

Status: accepted (2026-07-25)

## Context

The operation catalog needed a shape. An inventory of Flexicon's ~150 write methods was the starting
point, but Flexicon is AI-generated and its method surface is an implementation artifact, not an
interface — ~65 near-identical per-field setters are the slop signature. **The API surface is the
product**, so it was designed from best practice and prior art instead, then stress-tested against
four worked cases.

Source hierarchy for all catalog work, in order: **FieldWorks** (shipping, 20 years, 1000+ languages)
is the authority on how to do a thing properly; **LibLCM + its tests** are ground truth for engine
semantics; **Flexicon** maps which constructs matter and contributes specific scar tissue, but is not
an API model. Precedent already validated this ordering: on custom fields Flexicon refused to
implement at all while FieldWorks showed the correct pattern ([ADR 0005](0005-schema-operations-non-undoable-uow.md)).

## Decisions

### 1. Two layers, two governance regimes

- **Layer 0 — primitives.** The closed, versioned, **hashed** operation catalog. What a Change Set
  literally contains. Strictly gated: a new primitive is admitted only if (a) it is irreducible *or*
  its expansion is a baseline-dependent structural closure, (b) it maps to a real LibLCM capability
  rather than a convenience, (c) its target surface is manifest-classified, and (d) it ships with
  schema, validation, lowering, effects, and conformance vectors. **Anything failing (a) is a composer.**
- **Layer 1 — composers.** LCAtom-owned, first-class, and the product surface: `Expand`,
  find-and-replace, batch update, duplicate, `setPartOfSpeech`. **A composer's output is always an
  ordinary Change Set of Layer-0 primitives**, so every composer inherits review, effect capture,
  digests, drift, and idempotence for free and adds **zero** permanent contract surface. Composers may
  expand freely, but each must (a) raise semantic altitude — one authored intent replacing N primitives
  — and (b) be reusable across more than one workflow.
- **Composers are project-reading; the primitive builder is pure.** A composer takes
  `(project, intent)` because it must resolve references and fail closed. The builder is in-memory only.
- **Policy lives in composers.** MSA reuse-vs-create, copy semantics, orphan cleanup: these are
  composer decisions, so FieldWorks' behavior can be adopted without touching the contract.
- **The composer and its parameters ride as provenance** on the emitted Change Set — non-hashed,
  re-runnable. Re-running a composer against a new baseline yields a new Change Set: this is exactly
  ADR 0001's re-projection, and it falls out of provenance-not-hashed rather than needing machinery.

### 2. Nine primitive verbs, semantics-driven

`create` (carries `owner`, `ownerField` where ambiguous, an initial value map, and identity-relative
`placement` — LibLCM has no free-floating-then-insert state, so there is no separate `insert`) ·
`set` · `clear` · `addRef` / `removeRef` · `move` · `reparent` · `delete` · `merge` / `convert`.

`set` covers **all** value shapes — scalar, per-writing-system alternative, rich text with runs,
GenDate, Binary, **and atomic references** (value = a target canonical id): one deterministic
whole-value replacement. The splits are semantic, not shape-driven: `addRef`/`removeRef` exist because
a whole-collection `set` would violate minimal-diff; `move` because placement is identity-relative;
`reparent` because `set` may never target an owning slot; `clear` because JSON `null` is never
overloaded. GenDate/Binary/rich-runs are irreducible in *shape* but not in *verb*.

EMF's edit-command set (`Set`/`Add`/`Remove`/`Move`/`Replace`/`Delete`/`CreateChild`) — a generic
command layer over a reflective typed metamodel, the closest structural analog — converges on the same
split, as do MiniLcm and Harmony one layer down. `TextPropBinary` (style blobs) is scoped out of v1 as
configuration rather than authorable linguistic intent.

### 3. Per-field kinds, generated from the coverage manifest

Wire `kind` stays `group/construct/verb` and fully per-field (`lexical/sense/setGloss`), **generated**
from the manifest; implementation is 9 verbs × ~8 type handlers. This unifies at the implementation
layer and splits at the API layer — EMF's reuse and GraphQL's discoverability at once, not a
compromise between them. It yields one validation gate (unknown *kind*, already shipped), exact closed
schemas with no `if/then` conditionals over the in-scope manifest surface (473 fields — see
[issue E7](../issues.md), correcting this ADR's original 445-field basic-only undercount),
per-field version granularity, and an enumerated surface an AI agent selects from rather than
constructs.

`group/construct/verb` is confirmed by Kubernetes' `(apiGroup, resource, verb)` triple and is required
here: `contractVersions` is keyed per group, and per-construct validation needs a stable namespace.

**A reviewed `class → construct` map is required and is not mechanical**: `PhNCSegments` and
`PhNCFeatures` are both `naturalClass`; the three MSA classes are all `msa`; inherited members such as
`CmPossibility.Name` generate at construct level or the namespace explodes. That map is also where
per-construct validation attaches.

**Explicitly rejected:** a single `kind: "set"` with a runtime field-name parameter. That is the
GraphQL generic-mutation anti-pattern, and it would break per-field schema generation.

### 4. Two field spaces

- **Closed / generated** — model fields from the reviewed manifest.
- **Open / runtime-validated** — **custom fields**, which cannot have generated kinds because the
  field does not exist until a `define` creates it. These use a runtime locator, the portable
  `(class, name)` identity from [custom fields](../custom-fields.md):
  `{"kind":"system/customField/setValue","target":…,"field":{"class":"LexEntry","name":"EtymologyNote"},"after":{…}}`.
  Same value grammar and same verbs; validation runs against the project's runtime metadata. This is
  the **only** justified runtime field parameter.

`define` is **digest-neutral**: a newly defined field has no populated values, and additive-stability
(omit-empty) means no digest moves until a value is written. Schema changes are their own effect
category — "field added" is not a semantic-state delta.

### 5. Declared vs discovered footprint — one rule, replacing two mechanisms

An operation's reach is either **declared** (statically known from the authored intent — `set`,
`clear`, `addRef`, `move`) or **discovered** (found only by evaluating the baseline). Every discovered
-reach operation — `merge`, `convert`, **and `delete` when referrers exist** — uses read-back-derived
effects and forces full re-assessment; none may claim a static
[comparison footprint](../change-set-contract.md#comparison-footprint).

This collapses what were two separate machinery items (delete-cascade closure; compound/graph) into
one mechanism with two triggers.

### 6. Compensating sweeps are usually explicit, not machinery

When a composer can determine from the baseline that an object loses its last referent, it **emits an
explicit `delete`** rather than relying on a hidden runner sweep — making the cleanup a visible,
reviewable operation. If the baseline shifts and the delete becomes wrong, `delete`'s own disclosure
surfaces it. This is strictly better than the harvested behavior, where `SetPartOfSpeech` silently
orphaned an MSA and a separate manual sweep was required.

## Consequences

- The catalog is ~9 verbs and one generated kind namespace, not ~150 hand-written operations. The
  Flexicon inventory remains valuable for gotchas and construct coverage only.
- The manifest becomes load-bearing infrastructure (the type system), not merely a completeness gate,
  and gains a `class → construct` map plus per-field type/comparison-class data.
- Composers are where product value accrues and where FieldWorks' policies get adopted.
- Never add a whole-object "replace" verb: that is what forced Kubernetes into `managedFields` /
  server-side apply as a second design generation.
- A `set`/`clear` may legitimately expand into multi-field compensating effects (EMF's bidirectional
  `SetCommand` compounds internally), so "one `set` verb" must not be read as "one field touched."
