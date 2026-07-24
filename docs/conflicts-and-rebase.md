# Conflict, warning, and rebase semantics

## Outcomes

Every condition belongs to exactly one of four categories.

### Deterministic resolution

There is one result that preserves authored semantics and model validity. The runner may resolve
it automatically and records what it did.

Examples:

- resolving a canonical ID to its exact existing LibLCM GUID;
- refreshing a sequence anchor when exactly one gap preserves intent;
- treating equivalent LibLCM-normalized text as equal;
- recognizing an already-realized creation only when identity and complete expected structure
  agree;
- the whole-project baseline digest moved but the Change Set's comparison footprint and effects are
  unchanged — the normal state of a project under ongoing manual editing.

### Warning / application-policy decision

The operation retains one clear meaning, but drift or consequence deserves visibility. The runner
reports it; the application/user decides whether to apply, rebase, or reject.

Examples:

- before-state differs from a prior Assessment but unconditional `set` remains well-defined;
- a same-type GUID already exists and values would be overwritten/reused;
- delete cascade changed;
- display-only custom-field metadata differs;
- a newer runner lowers the same intent differently, or a changed default or reclassified member
  moves the expected effects.

### Genuine semantic conflict

Proceeding requires selecting or changing intent. A human or higher-level LLM/application must
amend the operation.

Examples:

- target identity is ambiguous;
- both branches changed the same scalar differently;
- a sequence item has multiple plausible target gaps;
- a custom field has the same class/name but incompatible type;
- an entity was deleted on one side and semantically edited on the other;
- a required reference target was replaced by multiple candidates;
- a storage GUID is occupied by a different LibLCM type (an authored storage-GUID override is the
  escape hatch, producing amended intent).

### Hard error / defect

No valid interpretation or safe execution exists.

Examples:

- malformed or unknown contract;
- invalid 22-character ID suffix;
- forbidden unknown semantic property;
- dependency/order violation;
- a referenced target is neither present in the baseline nor created earlier in the same Change Set;
- a declared prerequisite Change Set is absent from the applied history, or the prerequisite chain
  contains a cycle;
- unsupported model member;
- broken LibLCM invariant;
- rollback failure.

## Rebase rule

Normative rule:

> Rebase may refresh baseline-relative evidence and anchors only when a single result preserves
> the authored target, verb, value, entity identities, operation order, and create/delete intent.
> It reports every changed consequence. If resolution requires selecting or changing semantic
> intent, it is a conflict. If no valid interpretation exists, it is a hard error.

Two operations must not be conflated:

- **Reassessment** evaluates unchanged intent against a new baseline, a new runner or projection
  version, or both, and produces a new Assessment. The intent digest cannot change.
- **Rebase** may produce an amended Change Set when a unique mechanical anchor rewrite is needed.
  It returns an explicit old-intent-to-new-intent record and the amended Change Set has a new
  digest.

Reassessment may update:

- baseline semantic digest association in a new Assessment;
- before-state evidence in a newly produced Assessment;
- assessed delete/reference effects;
- resolved execution anchors in the output-only mutation plan when authored anchors still identify
  one unique intended gap;
- diagnostics and impact calculations.

Rebase may rewrite an authored identity-relative placement anchor only when exactly one new anchor
pair preserves the same relative ordering intent. This is never silent and always produces new
intent. Reassessment or rebase may not update:

- operation target;
- operation kind/verb;
- desired value;
- canonical entity identity;
- create versus update versus delete meaning;
- operation order;
- ambiguous reference choice.

The Change Set intent digest remains unchanged after reassessment because runner evidence is not
embedded in intent. Any actual Change Set amendment, including an authored-anchor rewrite,
produces a new intent digest.

## Three-way conflict principles

Given common ancestor O and descendants A and B:

- unchanged on one side and changed on the other is mechanically adoptable;
- identical normalized changes are compatible;
- disjoint fields on the same identified entity are compatible if model invariants remain valid;
- different changes to one scalar are conflicts;
- delete versus edit is a conflict unless the edit is provably irrelevant to an explicitly
  authored deletion and policy accepts the changed effects;
- ownership and reference changes are evaluated structurally, not as raw JSON lines;
- ordered changes use identity-aware sequence comparison and explicit moves;
- grammar/domain meaning is not guessed.

The engine reports enough data for a UI or LLM to explain and amend conflicts. It does not impose a
merge-queue or approval policy.

## Review equivalence

A reviewer asks one question — *these actions will happen, this is the resulting state delta; is that
what I intended?* — and the answer does not depend on why the assessment moved. A changed baseline and
a changed engine produce the same review. Cause is recorded as an attribute of the diagnostic, never
as a separate category, artifact, or workflow.

Ordered actions and the state delta are both presented. Execution order governs legality and is shown
for comprehension. Where order carries meaning it is already visible in the state delta, because
ordered model properties are sequences and a position change is a state change.

Effect-set equality is therefore the unit a reviewing application needs. When a reassessment produces
an effect set identical to one already reviewed, nothing has changed for the reviewer, whatever moved
underneath. When it differs, the delta is the review. This repository supplies the comparison and the
stable effect digests that make it checkable. Whether a prior approval carries is application policy
and remains host-owned.

## What is compared

A reviewer's practical question is *what does LCAtom actually check to decide something changed?* It
does not diff the whole project — under normal use the project changes constantly for reasons no
Change Set caused. It checks each Change Set's **comparison footprint**: the facts its meaning depends
on, and nothing else. In plain terms:

- **For unordered data** (most of the lexicon), the question is *did the thing I am editing change?* —
  the target object itself.
- **For ordered data** (template slots, sense order), it adds *are my neighbors still the same
  items?* — the left/right links, not the neighbors' contents. A neighbor editing its own internals
  is not my concern; a neighbor being replaced, or a new item inserted beside me, is.
- **For phonological rule order**, it adds the neighbors' *contents* too, because rule order is
  feeding/bleeding: an adjacent rule changing what it does changes what my rule produces.

A membership change to the object itself is always shown — a lexeme joining a new template or class is
a change to that lexeme. The template's or class's own internal churn is not shown, unless placing the
lexeme there is what the Change Set is doing. The normative definition and the migratable
per-property classification live in the
[comparison footprint](change-set-contract.md#comparison-footprint). Effect comparison remains the
final word; the footprint is what makes the cheap "still clean?" check possible.

## Diagnostic requirements

Diagnostics require:

- stable machine-readable code;
- category/disposition;
- operation and target IDs;
- baseline, expected, and observed facts;
- what moved since the compared Assessment — baseline, runner or projection version, or both —
  recorded as an attribute rather than as the diagnostic's category;
- candidate resolutions when deterministic mechanisms exist;
- effect/cascade differences;
- concise human-readable explanation;
- sufficient structured data for a reviewing application or LLM.

Never report “best effort success.” Every skipped, unsupported, or unresolved action is explicit.
