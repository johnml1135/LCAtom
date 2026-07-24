# Normative Change Set contract

This document fixes the semantics that implementation and conformance fixtures must enforce.
Concrete JSON Schema files should be generated or written during Phase 1 and must agree with this
document.

## Document shape

Illustrative shape:

```json
{
  "contractVersions": { "lexical": "1.0" },
  "changeSetId": "agent_AAECAwQFBgcICQoLDA0ODw",
  "operations": [
    {
      "operationId": "agent_EBESEhMUFRYXGBkaGxwdHg",
      "kind": "lexical/entry/create",
      "entityId": "agent_ICEiIyQlJicoKSorLC0uLw",
      "after": {},
      "rationale": "Corpus evidence...",
      "confidence": 0.92,
      "provenance": [],
      "extensions": {}
    }
  ],
  "extensions": {}
}
```

`contractVersions` maps each endpoint group to the contract major/minor the Change Set was authored
against. The group is the leading segment of `kind`. The map must name exactly the groups the
operation array uses: a missing group is a validation error, and a padded one is too, because the map
is hashed and must depend only on authored content.

The map does not compute compatibility. Ingestibility is decided by strict closed parsing; the
declared versions exist so that a runner which cannot honor one can say which group, which version
was required, and which it carries, letting the operator upgrade or rewrite instead of receiving a
partial application. See [versioning](architecture.md#versioning).

The operation array is authoritative execution order. The runner never silently reorders it.
Dependencies validate whether that order is legal. Planning may resolve the identity of a later
proposed entity, but an operation cannot execute against an entity before its creator operation.

Omission always means “leave untouched.” Clearing, detaching, removing, and deleting require
explicit verbs. JSON `null` is never overloaded to mean several different mutations.

`set` means unconditional desired semantic value. Baseline `before` evidence belongs to an
Assessment, not portable intent. If apply is given a prior Assessment and current before-state
differs, that drift is a diagnostic condition, not a reinterpretation of `set`; application policy
chooses whether warnings may proceed. Structural guards deliberately authored as part of intent
(for example an expected target type) are different: they are hashed and enforced.

## Operation vocabulary

The exact v1 inventory must be produced from use cases and the LibLCM coverage manifest. It follows
these semantic families:

- create an entity through an appropriate LibLCM factory and owner;
- set or replace a scalar value;
- set or clear a writing-system alternative;
- set or clear a rich string without flattening runs;
- attach or detach an atomic reference;
- add or remove a collection reference;
- insert, remove, or move a sequence member;
- explicitly clear a value;
- delete an entity through LibLCM ownership semantics;
- define, update supported metadata for, or delete a custom field.

Operations are model-aware. A lexical-entry create is not a generic “create object of class name.”
Closed schemas expose only meaningful, supported properties.

## IDs and GUID mapping

Change Set IDs, operation IDs, and proposed entity IDs use this textual convention:

```text
<optional arbitrary prefix><22-character unpadded base64url suffix>
```

Only the suffix is structurally enforced:

- exactly 22 characters;
- URL-safe base64 alphabet;
- no padding;
- decodes to exactly 16 bytes/128 bits.

The prefix may be empty and is informational. Provenance is likewise not structurally required.
Implementations must not infer authority, engine type, or uniqueness domain from the prefix.
The suffix is always the final 22 characters. Everything preceding it is the prefix, preserved
exactly with no normalization or character/separator requirement. Document resource limits may
bound total string length, but validators do not assign prefix semantics.

`changeSetId` and every `operationId` must be present and valid. Operation IDs must be unique within
one Change Set. A proposed entity ID may have only one creator operation in a Change Set; later
operations reference that creation. Global uniqueness of Change Set and operation IDs cannot be
proven or enforced by the runner. Only entity IDs participate in LibLCM GUID realization.

For entity IDs, the 16 canonical bytes map left-to-right to the ordinary textual GUID hexadecimal
bytes. Example:

```text
bytes:   00 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f
suffix:  AAECAwQFBgcICQoLDA0ODw
GUID:    00010203-0405-0607-0809-0a0b0c0d0e0f
```

.NET's `Guid.ToByteArray()` and `new Guid(byte[])` use historical mixed-endian layout and must not
be used for this conversion. Parse/format the textual GUID or use explicit network-order helpers.
Preserve all 128 bits; do not force UUID version or variant marker bits.

Normally the entity suffix becomes the LibLCM storage GUID. A receipt records the mapping.

### GUID collision behavior

The runner preflights every proposed storage GUID before mutation.

- Same canonical ID and compatible expected entity/type may be reused or updated only according to
  the authored operation semantics. Always emit a warning enumerating values that would be
  overwritten or reused. The caller/application decides whether to proceed.
- A GUID occupied by an unrelated entity of the same broad type is also at least a warning; never
  silently assume identity merely because the type matches.
- A GUID occupied by a different LibLCM type is a genuine semantic conflict that blocks
  application. An explicit authored storage-GUID override is the escape hatch and therefore
  produces amended intent.
- Overrides change storage realization, not canonical identity, and are recorded in assessment and
  receipt.

Preflight is mandatory because LibLCM identity-map registration can otherwise overwrite an
existing mapping.

LibLCM does not persist the canonical-to-overridden-storage mapping. A caller using an override
must retain the Application Receipt and supply its identity mapping to later assessment, diff, and
apply calls. With no supplied mapping, a snapshot can expose only the storage GUID-derived identity
and must diagnose the missing lineage; it may not guess the original canonical ID. The mapping
resolver is an input port, while storage of its records remains outside this repository.

## Ownership and delete

Deletion uses LibLCM native ownership cascade and reference cleanup. The canonical Change Set does
not enumerate synthetic child-delete operations merely to imitate the cascade.

Assessment must expose the complete delete closure:

- target object;
- all owned objects that will be deleted;
- inbound references that LibLCM will remove or modify;
- custom values affected;
- counts and a deterministic effect digest.

Assessment generates baseline-relative `expectedEffects`; they are not mutable fields filled into
the canonical Change Set. A changed cascade discovered on apply or re-assessment is one instance of
the general rule in [drift](#drift): emit the full delta and let the application or user decide.
A missing, already-deleted object is deterministically ignorable only when prior baseline identity
and intent prove it is the same deletion, not an unresolved target.

## Ordered data

Reordering is represented by explicit moves, never delete-and-reinsert.

Placement uses identity-relative anchors:

```json
{
  "kind": "sequence/move",
  "target": "item_...",
  "placement": {
    "after": "left_...",
    "before": "right_..."
  }
}
```

An edge anchor may omit one side. Numeric indices are not canonical intent.

During reassessment, resolved execution anchors may be refreshed when exactly one gap satisfies the
unchanged authored anchors. If the authored anchors themselves must change, explicit rebase emits
an amended Change Set and new digest only when exactly one gap preserves the ordering intent. If
several positions are plausible, the operation conflicts.

## Canonical JSON and hashes

Use RFC 8785 JSON Canonicalization Scheme bytes, then SHA-256.

The intent digest includes executable desired content:

- declared contract group versions, which depend only on the operations authored and never on the
  runner's own version table;
- operation order;
- operation IDs, because dependencies refer to them;
- operation kinds;
- targets and new entity IDs;
- desired values, clears, deletes, references, and placements;
- dependencies;
- explicit collision/storage-GUID overrides.

It excludes:

- Change Set ID, avoiding a circular/content-address ambiguity;
- pretty formatting;
- rationale, confidence, and provenance, which are review metadata rather than executable meaning;
- assessment before-state;
- expected/observed effects generated by the runner;
- warnings and conflicts;
- impact analysis;
- application receipt;
- non-semantic extensions.

The formal intent projection written in Phase 1 must contain exactly these included fields and no
others. Digests are rendered as `sha256:` followed by 64 lowercase hexadecimal characters.

Ordered arrays remain ordered. Collections classified as unordered are sorted by their canonical
identity in the semantic snapshot. Dates, binary values, floating-point policy if any, writing
systems, and rich-text properties must have explicit canonical encodings and conformance vectors.

The raw `.fwdata` artifact receives a separate SHA-256 over exact bytes. Artifact digest and
semantic digest are not interchangeable.

## Canonical Semantic Snapshot

The snapshot is an inspectable deterministic projection of every supported semantic model member.
The digest is derived from its RFC 8785 canonical JSON form.

The preimage includes `projectionVersion`, so two different projections can never yield equal hex.
It excludes runner, LibLCM assembly, and coverage-manifest versions, which are implementation facts
carried as provenance. The projection is additive-stable: members semantically indistinguishable from
absent are omitted entirely, so classifying a newly shipped LibLCM member leaves the digest of an
unpopulated model unchanged. Where LibLCM distinguishes unset from set-to-default, so does the
projection. See [versioning](architecture.md#projection-stability).

Normalization follows LibLCM:

- plain Unicode and MultiUnicode alternatives use LibLCM's in-memory NFD representation;
- rich String/ITsString and MultiString use LibLCM NFSC;
- rich strings preserve run boundaries, writing-system identity, and all properties classified as
  semantic;
- values are never flattened to `.Text`;
- raw `.fwdata` commonly serializes plain Unicode as NFC, but storage normalization does not define
  semantic equality.

The snapshot includes enough identity, ownership, reference, order, and definition information to
support diff, expected effects, rebase, and read-back validation.

## Assessment

Assessment is deterministic for the same Change Set, semantic baseline, runner/version matrix, and
policy-independent options. It contains:

- intent digest;
- baseline semantic and optional artifact digests;
- resolved targets and storage mappings;
- before-state evidence;
- output-only LibLCM Mutation Plan;
- expected effects and effect digests;
- warnings, conflicts, and hard errors with stable diagnostic codes;
- impact summary;
- applicability;
- ingestibility, naming any declared group version the runner cannot honor and the version it carries;
- effect drift against a supplied prior Assessment, if one was given;
- runner, declared contract group, projection, model, and manifest versions.

Warnings do not silently become errors or approvals. The host owns application policy.

### Drift

`expectedEffects` are the compatibility oracle. When a Change Set is re-assessed or applied against a
prior Assessment, any difference in expected effects is a typed diagnostic carrying the full delta,
resolved by application policy and never auto-accepted.

Assessment determinism is conditioned on the runner/version matrix, so this one rule covers both
kinds of drift: the baseline moved, or the tools did. An operator on a newer build sees a changed
default, a reclassified member, or a new lowering as an effect delta to review — never as a silent
reinterpretation.

Comparison is over effects, never the Mutation Plan. An improved lowering produces a different plan
and identical effects, and treating that as drift would train operators to dismiss the warnings that
matter. Effect digests must therefore be stable under lowering optimization, which is a conformance
obligation with fixtures.

The reviewer sees one review regardless of cause. See
[review equivalence](conflicts-and-rebase.md#review-equivalence).

### Comparison footprint

Drift is judged over a Change Set's **comparison footprint** — the model facts its meaning depends
on — never over the whole project. This is what keeps drift meaningful while linguists keep editing
the project in FieldWorks indefinitely: an unrelated edit does not touch the footprint, so the review
stays silent.

One rule fixes the footprint's reach: **it extends into another object exactly when this Change Set's
meaning depends on that object.** That happens in exactly three ways.

1. **The operation's own owned target** — always in the footprint, in full: the object the operation
   targets and the state it owns, the same ownership boundary as *fill, never frame*
   ([ADR 0001](adr/0001-hermitcrab-projection-not-canonical.md)). For unordered data this is the
   entire footprint.
2. **A relationship the operation authors** — placing a lexeme into a template slot or a class *is*
   the change, so the referenced template or class is in the footprint. A referenced object the
   operation does not author is not: a lexeme's own membership changing is surfaced, but the internal
   state of a template the lexeme merely references is not — unless authoring that membership is what
   the operation does.
3. **Ordered neighbors**, to the depth the ordering's semantics require — see the classes below.

This yields three **comparison classes** for a model property, each a declared attribute of the
coverage manifest and migratable as understanding improves:

| Class | Footprint reach into neighbors | Examples |
| --- | --- | --- |
| Unordered (`col`) | none — the owned target only | lexicon entries, feature structures |
| Positionally ordered (`seq`) | neighbor **identity** (the left/right links) | template slots, sense order |
| Semantically ordered (`seq`, feeding) | neighbor **full state** | `PhPhonData.PhonRules` |

The third class exists because phonological rule order is feeding/bleeding: a neighbor rule editing
its own content changes the surface form this rule produces, so the neighbor's *state*, not merely
its identity, is part of this Change Set's meaning. Positionally ordered data has no such coupling —
a neighbor's internal edits do not change what the operation means; only a change to *which* object
is adjacent does. Reclassifying a property between these buckets is a declared manifest change.

The footprint defines what an effect set must span; effect comparison remains the oracle. A cheap
identity-and-adjacency check over the footprint may pre-filter *possibly drifted, re-assess*, but may
never conclude *clean* — only a fresh effect comparison grants that, because the feeding class proves
identity alone can miss a real change.

## Application Receipt

A core receipt is emitted only after all operations, read-back, and invariant validation succeed
and the unit of work commits. It contains:

- Change Set ID and intent digest;
- baseline and result semantic digests;
- per-operation outcomes;
- canonical-ID-to-storage-GUID mappings;
- actual effect closure;
- warnings explicitly accepted by the caller;
- runner, projection, and LibLCM/model versions, so a stored result digest remains interpretable
  after a dependency bump.

Apply requires an opaque applier identity from the host and writes exactly one
[applied-change log](applied-log.md) entry inside the same unit of work. That entry is excluded from
the semantic snapshot and from expected effects, so it never reaches any digest; including it would
make every effect digest unique and change the project's semantic digest on every apply.

No receipt is emitted for a rolled-back application, except a distinct failure report that cannot
be mistaken for a realized state edge.

Because the core does not save projects, a result `.fwdata` byte digest cannot exist at core commit
time. A host may later emit a separate `ArtifactAttestation` linking the Application Receipt,
semantic result digest, saved artifact digest, and save/reopen verification. It never mutates the
committed core receipt.

## Mechanical diff

Diff is deliberately linguistically unaware.

- Match ordinary entities only by exact GUID/canonical identity.
- Do not infer equivalence from form, gloss, POS, labels, fingerprints, or similarity.
- Unrelated projects may therefore produce delete/create operations for semantically similar
  objects.
- External tools may propose an explicit identity mapping; the runner validates the resulting
  operations but does not invent that mapping.

Two-way diff synthesizes operations that transform snapshot A into snapshot B.

Common-ancestor three-way comparison distinguishes changes relative to ancestor O in descendants A
and B and reports compatible changes, warnings, and genuine semantic conflicts. It does not
silently choose “source wins,” “target wins,” or “newest.”

Its primary output is a `ThreeWayAssessment`. It may also synthesize a candidate Change Set
containing only deterministic compatible edits. Conflicting intent remains structured conflict
data and is never inserted as a guessed operation.

### Minimum ordered-sequence edits

For sequences with unique stable identities:

1. Delete source-only IDs.
2. Insert target-only IDs.
3. Map common source IDs to their target positions.
4. Compute a deterministic longest increasing subsequence (LIS).
5. Keep the chosen LIS and move every other common ID exactly once.

Minimum move count is:

```text
commonCount - LISLength
```

Minimum total edit count is:

```text
deletes + inserts + commonCount - LISLength
```

Implement the O(n log n), O(n)-space algorithm directly in C#. Freeze LIS tie-breaking, operation
emission order, and anchor choice in normative fixtures. Verify minimality exhaustively against a
small-permutation breadth-first-search oracle and use property tests for larger inputs.
