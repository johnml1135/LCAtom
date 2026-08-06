# ADR 0023 — Kind names are derived from the declaring class; meaning lives in a required description

**Status:** accepted, 2026-08-05. Resolves `B19` and `B20`. Completes
[ADR 0022](0022-structure-is-derived-policy-is-five-rows.md): the manifest is now an authority on `Scope`
and on `Construct` **as a staging grouping only** — every column that feeds a wire identifier is derived.
Evidence:
[prior art](../research/2026-08-05-naming-a-public-api-over-a-legacy-model.md),
[FieldWorks' own labels](../research/2026-08-05-fieldworks-user-facing-names.md).

## Context

An operation is named `group/construct/verb` — `lexical/lexSense/setGloss`. The middle segment was
hand-authored, 53 distinct values, and it is hashed into every Proposal's intent digest, so renaming it is
a breaking change. Two problems had been open for months:

- **`B19`: the names are not mechanical.** Of 165 (class, construct) pairings, 14 are `lowerFirst(Class)`,
  17 match after stripping a `Cm`/`Mo`/`Ph`/`Fs` prefix, and **134 have no relationship to the class at
  all** — 16 classes were grouped as `featureStructure`, 11 as `ruleContext`. Measured per distinct name
  rather than per pairing the figure is 73.6%; both are correct about different denominators.
- **`B20`: nineteen rows carry seven names at once**, pipe-separated, and no rule said how to pick.

Two research threads settled it, and they converged from opposite directions.

### The prior art says the question was posed wrongly

No surveyed system — Kubernetes, protobuf, OpenTelemetry, Salesforce, FHIR, Google's AIPs — chooses between
mechanical and curated names. **Every one of them keeps two separate strings**: an immutable, ugly-is-fine
machine identifier, and a separately governed human label or description that nothing hashes. Salesforce
ships API Name *and* Label permanently; FHIR has `code` and `display`; AIP-140 has `display_name`;
OpenTelemetry governs a `brief` by committee. All treat renaming the *identifier* as versioned machinery
rather than an edit — protobuf field renames are described by its own tooling vendor as "nothing but tears
and pain."

**Motif's `Construct` was doing both jobs with one string.** Those jobs have opposite stability
requirements, which is why nobody else combines them.

And the usual reason renaming is expensive does not apply here. Verified against Anthropic's tool-use
documentation: a tool's name and description are re-sent in the `tools` array of **every API request**, from
application code we control — there is no compiled client or cached artifact for a human to go re-edit. The
same documentation states that the **description, not the name, is "by far the most important factor in tool
performance."** So curating the identifier buys little with our first consumer, while a good description
buys a lot.

### The inheritance finding removes the remaining choice

`CmPossibility` is a base class with **13 descendants** (12 direct plus `LexEntryInflType`). The open
question was whether to name after the class where a field is *declared* or the class of the object being
*edited*. The second is impossible:

```csharp
// liblcm/src/SIL.LCModel/DomainServices/BootstrapNewLanguageProject.cs
lp.PartsOfSpeechOA.ItemClsid    = PartOfSpeechTags.kClassId;    // :78  a real subclass
lp.ConfidenceLevelsOA.ItemClsid = CmPossibilityTags.kClassId;   // :80  no subclass at all
```

**Eleven of the twenty possibility lists get the bare base class**; only nine get a real subtype. A
Confidence Level, a Status, a Restriction and an Education Level *are* `CmPossibility` instances. What
distinguishes them is **which list owns the object** — a fact about the data at runtime, not about the
model.

So `B20`'s pipe-separated cell was not sloppiness. It was an attempt to encode a runtime fact in a
schema-time name, which is **impossible, not merely awkward.** The manifest had already drawn that line
correctly; it simply could not resolve it.

### FieldWorks' own labels are a seed, not a source

FieldWorks shows linguists real words, and three sources are mechanically scrapable: `strings-en.xml`
(class names, list purposes), the `.fwlayout` slice system (`<slice field= label= tooltip=>`), and tool
config keyed by `(ownerClass, ownerField)`. But coverage is **roughly a third to under half** of the 473
in-scope rows, and labels are **per-view rather than canonical** — `MoInflAffixSlot.Name` is "Name" in one
layout and "Slot Name" in another. `LcmMetaDataCache.GetFieldLabel` looked ideal and is a dead end: `null`
for every built-in field, populated only for user-created custom fields.

## Decision

### 1. The identifier is derived: `lowerFirst(DeclaringClass)`

Taken verbatim from the class where the field is declared, with only the first letter lowercased. **No
prefix table**, because a prefix table is a lookup rather than a transform and would reintroduce judgement
for no benefit now that meaning lives elsewhere.

```
LexSense.Gloss           → lexical/lexSense/setGloss
CmPossibility.Name       → lists/cmPossibility/setName        (covers all 13 descendants)
PhSegRuleRHS.StrucChange → grammar/phSegRuleRHS/...
```

Fully script-regenerable and script-auditable, with zero exceptions and zero rows requiring a decision —
including the ~425 fields nobody has examined yet.

### 2. Fields are named where they are declared, never by concrete class

Forced by decision 1's evidence: for the majority of possibility lists there is no concrete class to name.
One `setName` operation serves all 13 descendants.

**One exception is required, not optional.** `MoForm` is `abstract="true"` — a `create` naming it would
describe something that cannot exist. **`create` and `delete` name a concrete class; every other verb names
the declaring class.** This is a rule, not a judgement call, and it is checkable: the model file says which
classes are abstract.

### 3. `Construct` survives as a staging concept, and stops being an identifier

The hand-authored grouping is **not deleted** — it is what
[ADR 0012](0012-build-order-hc-spine-first-kinds-generated.md) sequences work by, and what `MOT-6` and
`MOT-7` mean when they say "one construct, then the remaining 29." `featureStructure` genuinely unifying 16
classes is useful linguistic judgement, and it stays.

What changes is that it no longer serves as the wire identifier. **One word had two jobs — grouping work,
and naming operations — and only the second is being taken away.** After this, the two are independent: a
Construct says what ships together, the class segment says what an operation targets. `CONTEXT.md` records
them as separate terms so the collision cannot recur.

### 4. "Which list is this?" is answered by the target, not the name

A reviewer editing a Confidence Level and a reviewer editing a Part of Speech both see
`lists/cmPossibility/setName`. The distinction reaches them through the **target object and its owner in
the effect**, which is where that information actually lives. Effects are already keyed by canonical
identity, so nothing new is required.

### 5. Every kind carries a required description, and the build fails without one

Separate field. **Never hashed**, so it is free to improve forever. Mandatory, following AIP-192 and
OpenTelemetry's `brief`.

**Seeded by harvesting FieldWorks' own vocabulary** — `strings-en.xml` and the slice labels — for the third
to half of rows where it exists. Those are words linguists already reviewed and translators already
translated. Where FieldWorks has several labels for one field, the harvest records all of them for a human
to choose from rather than picking silently. The rest are hand-written as each family ships, which is a
bounded per-family cost rather than a 473-row project.

The description exists for **the human reviewing the manifest and approving a Proposal**, not for the agent.
That need survives regardless of who calls the API.

## Consequences

- **`B19` and `B20` are both resolved**, and **no column that feeds a hashed identifier is hand-authored any
  more.** The manifest keeps exactly two authorities: `Scope` (which fields we expose) and `Construct` (what
  ships together — decision 3). The hand-written surface is now scope decisions, construct grouping, creation
  validity, error handling, five ordering exceptions, and descriptions.
- **A word that had two jobs now has one.** `Construct` grouped work *and* named operations; the naming job
  is gone. `CONTEXT.md` gains **class segment** as a separate term, because a glossary that lets one word
  mean two things is how this ambiguity got in.
- **The one shipped kind was renamed on 2026-08-05**: `lexical/sense/setGloss` → `lexical/lexSense/setGloss`,
  in code, tests and live docs. Suite green at 88/88.
  **Correction to this ADR's original claim:** it said the frozen conformance vectors would need regenerating.
  They did not — they never referenced `setGloss`. They use `lexical/entry/create` and `sequence/move`, and
  neither was touched (see the next two bullets).
- **The conformance vectors keep their non-conformant kind names, deliberately.** They use
  `lexical/entry/create`, which the rule would make `lexical/lexEntry/create`. But those vectors exist to pin
  **canonicalization and digest determinism across languages**
  ([ADR 0007](0007-cross-language-digest-determinism.md)) — the kind string is opaque payload to that job.
  Renaming them would churn two frozen digests, oblige every non-.NET runner to re-sync, and test nothing new.
  They are regenerated when the generator emits real kinds, not for naming hygiene.
- **`sequence/move` does not fit `group/construct/verb` at all**, and that is a contract question rather than a
  naming slip: two segments, a `group` (`sequence`) that is not one of the four domains, and a payload of
  `target` + `placement` rather than a field. Whether field-agnostic placement primitives exist alongside
  per-field kinds is unresolved — recorded as `J45`.
- **`MOT-2` gains a third check** (derived name matches, abstract classes rejected for non-create verbs) and
  **`MOT-4` gains an input** (descriptions, required). A harvest step is added to `MOT-3`.
- **Ugly names are accepted deliberately.** `lists/cmPossibility/setName` reads worse than
  `lists/possibility/setName`. The trade is auditability and zero decisions against aesthetics, for a
  consumer that re-reads the name on every call and never complains — with the description carrying the
  meaning a human needs.
- **Unmeasured risk, stated plainly:** the research found **no postmortem in any ecosystem** on whether
  description fields are maintained or rot. Mandatory descriptions are well-precedented and their long-run
  record is undocumented. The mitigation is that the build enforces presence, and the seed makes the first
  version cheap; nothing enforces that a description stays *good*.
