# Naming a public API over a legacy model — what large systems actually did

*Research of 2026-08-05, against primary sources only: official docs, specs, source, and issue trackers.
Written for the open question in [README.md](../../README.md#open-decisions): "how Construct names get
settled, since only about a quarter are derivable from their class." Grounded against
[ADR 0022](../adr/0022-structure-is-derived-policy-is-five-rows.md) and the
[manifest trust audit](2026-08-03-manifest-trust-audit.md), which measured motif's own Construct column:
of 53 distinct values, **26.4% are an exact `lowerFirst(Class)` match, 32.1% need a hand-supplied
prefix-strip table, and 41.5% have no mechanical relationship to any class name at all**
(`featureStructure` spans 16 classes, `ruleContext` 11). Only the 26.4% bucket is zero-touch derivable —
the prefix table is "a lookup, not a transform, and not present in the data" (ADR 0022 §4) — so on a
strict reading **73.6% of Construct names require a human judgment call**, which is the same
neighborhood as this note's brief's "81%" framing, if not an exact match to a number this note could
verify from the repo's own artifacts. That discrepancy is flagged rather than silently resolved.*

---

## Verdict, up front

No real system in this survey ships a public API whose names are mechanically generated from an
internal legacy model **and** relies on that alone. Every precedent that mirrors a large or legacy
model (Kubernetes, protobuf, OpenTelemetry, Salesforce's Metadata API) pairs an **immutable, ugly-is-fine
machine identifier** with a **separately governed, mandatory human-facing description or label**, and
every one of them treats *renaming the identifier* as a breaking change requiring explicit machinery
(new API version, `reserved`, `@deprecated`, alias) rather than an in-place edit. None of them curate the
identifier itself for human readability at the cost of mechanical derivability — that curation happens
in the *label*, *display_name*, *brief*, or *description* field, which is allowed to churn freely because
nothing hashes it. Motif's `Construct` segment is doing two jobs at once — serving as the hashed wire
identifier *and* as the human-readable grouping label — and every precedent examined here keeps those
two jobs on separate fields precisely because they have different stability requirements. **The strongest
transplantable finding is not "pick mechanical" or "pick curated," it is "stop making one string serve
both roles."**

Given that, and given that motif's primary consumer is an AI agent rather than a human developer (Section
6 below), the calculus tips further than the precedents alone would suggest: an agent's tool names are
retransmitted on every API call from a definitions array your own code controls (verified against
Anthropic's own tool-use documentation, cited below), not compiled into a binary or memorized as muscle
memory, so **the repointing cost precedents worry about most — human callers who hardcode a name — does
not apply to motif's actual consumer today.** That argues for biasing toward the mechanical, verifiable
name over the curated one *for the identifier*, while still keeping a required, reviewed `description`
per Construct — not because an agent needs it to be pretty, but because every precedent below that ships
at this scale (OpenTelemetry, AIP-192, Salesforce labels) treats a maintained human-facing gloss as
required infrastructure, not optional polish, and because a human reviewer approving the manifest still
needs it.

---

## 1. What large, real projects do over a legacy/internal model

### Kubernetes — group/version/kind, never rename in place

Kubernetes structures every object as `(apiGroup, version, kind)` plus a lowercase-plural resource name
(`kubernetes.io/docs/concepts/overview/kubernetes-api/`). Group names use reverse-domain notation
(`rbac.authorization.k8s.io`), and "for historical reasons, there are 2 'monolithic' API groups — 'core'
(no group name) and 'extensions'. Resources will incrementally be moved from these legacy API groups into
more domain-specific API groups" — i.e., Kubernetes' own naming has visible legacy scar tissue it has
chosen not to retroactively clean up.

The deprecation policy (`kubernetes.io/docs/reference/using-api/deprecation-policy/`) is explicit and
absolute about renaming:

> "API elements may only be removed by incrementing the version of the API group. Once an API element has
> been added to an API group at a particular version, it can not be removed from that version or have its
> behavior significantly changed, regardless of track."

Renaming is handled as add-new/keep-old, not edit-in-place: "a v1 field named 'magnitude' which was
deprecated might be named 'deprecatedMagnitude' in API v2. When v1 is eventually removed, the deprecated
field can be removed from v2." Every API object must "round-trip between API versions in a given release
without information loss" — the same round-trip requirement motif already borrowed for `contractVersions`
(`2026-08-03-prior-art-canonical-diff-versioning-batch.md`). Timelines are concrete: beta APIs are
supported "9 months or 3 minor releases after introduction (whichever is longer)," and GA APIs "must not
be removed within a major version." Since v1.19, requesting a deprecated endpoint returns a `Warning`
header (RFC 7234 §5.5), an audit annotation, and increments an `apiserver_requested_deprecated_apis`
metric — a machine-readable refusal, not a changelog entry.

**This was not painless in practice.** The 1.22 release removed `extensions/v1beta1` and
`networking.k8s.io/v1beta1` Ingress after a multi-year deprecation window
(`kubernetes.io/blog/2021/07/14/upcoming-changes-in-kubernetes-1-22/`), and real migrations required field
renames inside the object (`spec.backend` → `spec.defaultBackend`, `serviceName` → `service.name`) that
took ecosystem tooling (ingress-nginx, GitLab Auto DevOps, GKE) real engineering effort to absorb —
GKE granted a one-time extension for clusters created on 1.21 or earlier. For schema changes *within* one
CRD across served versions, Kubernetes requires a **conversion webhook**: "if the conversion involves
schema changes and requires custom logic, a conversion webhook should be used. If there are no schema
changes, the default `None` conversion strategy may be used"
(`kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/`).
That webhook is the machinery that makes a rename survivable — it does not make renames cheap; it makes
them possible without breaking every stored object.

### Stripe — dated versions, additive-only "safe" changes, per-account pinning

Stripe versions its whole API by dated major release plus rolling monthly minors of the same name
(`docs.stripe.com/upgrades`): "Each major release... includes changes that aren't backward-compatible...
Each monthly release includes only backward-compatible changes, and uses the same name as the last major
release." Stripe's own definition of "backward-compatible" is a short, concrete list: new resources, new
optional request parameters, new response properties, reordering response properties, and changing the
*length or format* of opaque ID strings (explicitly including "adding or removing fixed prefixes such as
`ch_` on charge IDs") — notably, **Stripe reserves the right to change the literal text of an object ID's
prefix** as a non-breaking change, because callers are expected to treat IDs as opaque, not parse them.
Every account is pinned to a version at first request and must explicitly opt into upgrading through
Workbench; a 72-hour rollback window exists after upgrading. Nothing in the primary source discusses
renaming *fields* as backward-compatible — it is conspicuously absent from the "compatible changes" list,
implying Stripe treats field rename as squarely a breaking, version-gated change, consistent with
Kubernetes and the AIPs below. (Unverified beyond this: I could not find a primary Stripe source
enumerating past field/object *renames* the way Kubernetes documents past API removals — Stripe's
changelog was not crawled item-by-item for this note.)

### GitHub's REST-to-GraphQL transition — new naming layer, not tracked to Rails

GitHub's own engineering account (`github.blog/developer-skills/github/the-github-graphql-api/`) gives the
motivating problems — REST was "responsible for over 60% of the requests made to our database tier,"
responses "simultaneously sent too much data *and* didn't include data that consumers needed," and GitHub
wanted machine-checkable OAuth-scope metadata and parameter type-safety that no existing standard
supplied. GitHub built the schema with `rmosolgo/graphql-ruby` and "wrote linters to ensure that our
naming structure was standardized" — a governance detail (schema-level naming lint) worth transplanting on
its own, independent of the rename question. The primary source does **not** state whether GraphQL
type/field names track internal Rails model names or were deliberately renamed; I could not verify this
either way from GitHub's own documentation or engineering blog and it should be flagged as **unverified**
rather than assumed. What is verifiable: GraphQL's own type-system philosophy, per the spec itself
(`spec.graphql.org`), explicitly separates the *schema* (the naming/typing surface) from storage — a schema
"traverses and returns application data based on the schema definitions, independent of how the data is
stored" — which is architecturally the same move motif has already made in ADR 0009 (Layer 0 primitives
vs. LibLCM internals) whether or not GitHub's specific naming decisions can be recovered.

### Salesforce — API Name and Label are two different fields, permanently

Salesforce's platform is the cleanest "double up the name" precedent, though I was only able to confirm
parts of it against Salesforce's own docs directly — WebFetch against `help.salesforce.com` returned a
JavaScript-rendered shell rather than article text, so several of the following claims are corroborated
by practitioner/vendor sources rather than quoted Salesforce prose; each is flagged.

Verified against `developer.salesforce.com`: custom object and field API Names carry a mandatory `__c`
suffix, and installed managed-package components carry an additional namespace prefix, producing names
like `mypackage__Product__c` — the combination is what disambiguates one ISV's custom field from
another's on the same object. Every object and field additionally carries a `Label`, shown in the UI, that
is edited completely independently of the API Name.

Not independently confirmable from Salesforce's own prose via this tool (flagged as **corroborated by
secondary/practitioner sources, not directly quoted from a Salesforce-owned page**): that editing a Label
never changes the API Name, and that changing an API Name is the operation Salesforce and its ecosystem
treat as genuinely dangerous — Gearset's DevOps documentation states plainly that "the deployment will
never change API names, it will deploy a new item with the new name" when a rename is pushed through a
pipeline (`docs.gearset.com/en/articles/11003069`), and community reports describe integrations
(Marketo, middleware) breaking or silently re-pointing to a *new* field when an API Name changes, because
external systems reference the API Name, not the Label. The practical rule reported across that
practitioner literature: rename the Label freely; renaming the API Name is a last resort requiring a
find-and-replace across every Apex class, Visualforce page, trigger, and integration that references it.
This is the same "identifier is load-bearing, label is free" split as Kubernetes and protobuf, just
enforced by convention and tooling rather than by the platform refusing the write.

### FHIR — Coding.code vs Coding.display, and the 80% rule

FHIR's `Coding` datatype splits exactly the way this research brief predicted, confirmed directly against
the spec (`hl7.org/fhir/datatypes.html#CodeableConcept`):

- `code` — "A symbol in syntax defined by the system. The symbol may be a predefined code or an expression
  in syntax defined by the coding system (e.g. post-coordination)."
- `display` — "A representation of the meaning of the code in the system, following the rules of the
  system."

`CodeableConcept` wraps zero-or-more `Coding`s plus a `text` field: "A human language representation of
the concept as seen/selected/uttered by the user" — a third tier again, for what the *user* actually typed
or said, distinct from both the coded identifier and the coding system's canned display string. Three
tiers, three different stability/authority guarantees, in one datatype used across the entire standard.

FHIR's "80% rule" — only include an element in a core Resource if roughly 80% of implementers would use
it, and push the long tail to Extensions — is widely and consistently reported (e.g., via HL7's own
Confluence wiki page "Guide to Designing Resources," `confluence.hl7.org/spaces/FHIR/pages/35718826`), but
I could not get a clean fetch of that page's exact wording (HTTP 405 on direct retrieval), so **this is
flagged as reported-but-not-directly-quoted from the primary source**, despite being extremely
well-attested secondhand. It is the closest real-world analog to motif's own `Scope`/`ScopeReason`
decision (ADR 0022 §4: "which of the 898 fields we expose is a product decision") — FHIR faced the same
problem at far larger scale (legacy HL7 v2/v3 messaging models feeding hundreds of resource types) and
answered it with an explicit, named threshold rather than silence.

### Two more, briefly, for range

**Language Server Protocol** governs by an open pull-request process against a single versioned Markdown
spec (`microsoft.github.io/language-server-protocol`); each new feature is tagged with the spec version
that introduced it (`@since 3.17.0`), and consumers negotiate supported features via capability flags
exchanged at `initialize` rather than assuming a fixed surface — forward-compatibility by construction,
not by naming discipline. I found no explicit LSP policy on *renaming* an existing method/capability;
this should be treated as **unverified** rather than assumed absent.

**OpenTelemetry semantic conventions** never remove or rename a convention in place: "Removing
attributes, metrics, or enum members is not allowed, they should be deprecated instead" (governance, cited
in full in Section 5). Renames are handled through **schema transformations**: every span/resource can
carry a `schema_url` (e.g. `https://opentelemetry.io/schemas/1.43.0`), and published schema files "describe
the exact transformations, mostly renames, between any two versions, so consumers can translate old data
to new names" (secondary summary of the mechanism — I could not get a clean primary-source fetch of the
schema-file format itself, flagged below). This is architecturally the closest precedent to "a script can
mechanically fix up every consumer's reference to a renamed field" — but note it exists precisely because
OpenTelemetry's consumers are *not* re-issued a fresh definitions list on every call the way an LLM tool
schema is; the transform has to run over already-emitted, already-stored telemetry.

---

## 2. Identity vs. label: how the split is kept in sync, and what breaks when it drifts

| System | Stable identifier | Human label | Sync mechanism | Documented drift cost |
| --- | --- | --- | --- | --- |
| **Salesforce** | API Name (`__c`, namespace-prefixed) | Label | None — deliberately independent, edited separately in Setup | Renaming the *identifier* breaks Apex, Visualforce, and third-party integrations that hardcoded it; renaming the *label* is reported as safe (Gearset, community reports — not confirmed from a Salesforce-owned page, see §1) |
| **FHIR** | `Coding.code` (from an external code system) | `Coding.display` | None within the datatype — `display` is documentation, not authoritative, and the spec does not require it match the code system's own display text | Not independently verified from HL7 prose beyond the datatype definitions themselves (flagged) |
| **Kubernetes** | `(group, version, kind)` + field name in a given `apiVersion` | none formally — `kubectl explain` descriptions, doc comments | Fields are load-bearing identifiers, not paired with a separate label; "naming" and "documentation" are not split the way Salesforce/FHIR split them | The 1.22 Ingress removal is the closest documented drift cost: real migration labor across ingress-nginx, GitLab, GKE tooling (§1) |
| **AIP-122/180 (Google)** | resource `name` (a path, e.g. `books/1234`) | `display_name` (AIP-140) | Governance: `display_name` "should not have a uniqueness requirement" and is explicitly *not* usable as an identifier | Not independently verified with a drift-cost citation; the AIPs are prescriptive, not a postmortem |

**Salesforce and FHIR are the richest here, but the "concrete cost of drift" evidence is thinner than
hoped.** For Salesforce, the strongest available evidence is Gearset's pipeline documentation (a real
Salesforce DevOps vendor, not a blog opinion piece) stating deployments never rewrite an API Name in
place — they create a new component and orphan the old — which is itself the cost: a silent duplicate
rather than a clean rename. For FHIR, the datatype split is unambiguous in the spec, but I found no
HL7-authored postmortem or "known issues" page describing `code`/`display` drifting out of sync in
practice (e.g., a coding system's `display` text changing upstream while `code` stays fixed); **this
should be treated as a plausible but unverified failure mode**, not a documented one, despite being widely
discussed informally in FHIR implementer communities.

The one genuinely well-documented "drift breaks things" case in this whole survey is protobuf's, covered
in Section 4: renaming a field name breaks generated code and reflection-based tooling even though the
wire format is tag-numbered and should not care — that is an identity/label conflation cost with primary
evidence (a real GitHub issue thread and Buf's own engineering writeup), just not from Salesforce or FHIR
as the brief anticipated.

---

## 3. Is "ugly-but-stable name + required description" an established pattern?

Yes, and it recurs with real teeth (mandatory review gates), not just recommended convention — though the
strength of the *requirement* varies by system, and one case (OpenTelemetry's per-attribute `brief`) turns
out weaker than expected once checked against the actual machine-readable schema rather than the prose
docs.

- **Google AIP-192 (Documentation)** is the strongest: "In APIs defined in protocol buffers, public
  comments **must** be included over every component (service, method, method, message, field, enum, and
  enum value)" — a hard MUST, not a SHOULD, enforced by the API Linter (`linter.aip.dev/192/has-comments`,
  confirmed to exist by search though not fetched directly). Deprecated components carry a required
  format too: "the first line of the respective comment must start with 'Deprecated: '... and provide
  alternative solutions."
- **JSON Schema** is the weakest of the group, by design: `title`/`description` are annotation keywords,
  and the spec text is explicit that "none of these 'annotation' keywords are required, but they are
  encouraged for good practice" (`json-schema.org/understanding-json-schema/reference/annotations`).
- **GraphQL SDL** sits in between: the spec states "Descriptions should be provided as Markdown... for
  every definition in a type system," using `should`, not `must` (`spec.graphql.org`, Type System
  Descriptions section) — a strong recommendation, not a hard gate, though many schema linters (see below)
  turn it into one in practice.
- **Protocol Buffers** has no *language-level* required-comment rule at all (comments are just comments to
  `protoc`), but the ecosystem fills the gap with linting: `protolint` is a "pluggable linter and fixer to
  enforce Protocol Buffer style and conventions" (`github.com/yoheimuta/protolint`) and supports
  comment-presence rules as an opt-in policy, not a spec mandate.
- **OpenTelemetry semantic conventions** requires `id`, `type`, and `stability` on every attribute
  definition per the formal Weaver syntax schema (`github.com/open-telemetry/weaver`,
  `schemas/semconv-syntax.md`) — but on a strict reading of that schema, **`brief` is not marked required
  at the individual-attribute level** the way `id`/`type`/`stability` are (it inherits "same meaning as
  for the whole semantic convention, but per attribute," without an explicit `Required.` marker attached).
  This is worth flagging plainly: the widely-repeated claim that OpenTelemetry "requires a brief per
  attribute" is **weaker in the actual schema than in common description of it** — enforcement in practice
  comes from the CONTRIBUTING.md governance process (two required approvals, area-owner review — Section
  5) rather than from the schema validator rejecting a missing `brief` outright. I could not find a
  contradicting normative "brief is REQUIRED" line elsewhere in the primary schema to override this
  reading, so it stands as checked-and-weaker-than-expected rather than unverified.

**Evidence on whether descriptions actually stay current, versus rot:** the strongest concrete evidence
found is that linting exists specifically *because* rot is expected and needs a mechanical backstop, not
because it is hypothetical:

- Spectral's default OpenAPI ruleset ships `info-description` and `operation-description` rules requiring
  a present, non-empty description string (`stoplight.io/open-source/spectral`, corroborated via search of
  Spectral's own rule names — not independently re-verified against Spectral's source in this pass).
- `protolint`'s existence as a *fixer*, not just a linter, for style/comment conventions is itself evidence
  that hand-maintained proto comments drift enough in real codebases to need automated correction, not
  just detection.

I did not find a named postmortem or blog retrospective specifically diagnosing "doc rot" as a discovered
production incident in any of these ecosystems (as opposed to lint rules existing preemptively); that
absence is itself worth registering as **unverified-by-incident-report**, distinct from the pattern's
existence, which is well established.

---

## 4. Renaming machinery, cheap vs. notorious

| Mechanism | What it does | Real-world verdict |
| --- | --- | --- |
| **Kubernetes API versioning + round-trip conversion** | New API group version required to change/remove anything; old and new versions must convert losslessly | Works, but the 1.22 Ingress removal shows the *migration labor* lands on every downstream tool (ingress-nginx, GitLab, GKE) even when the platform-side mechanism is sound — cited in §1, real PRs and issue trackers, not opinion |
| **Kubernetes CRD conversion webhooks** | Custom logic to translate a resource between served versions when schemas differ | Necessary infrastructure, not "cheap" — it is a whole webhook service you must write, deploy, and keep available (`kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/`) |
| **protobuf `reserved` field numbers/names** | Permanently blocks reuse of a deleted field's number/name so old binaries can't misread new data | Cheap to *apply*, mandatory in practice: proto3 guide states you "must reserve the deleted field number" on removal (`protobuf.dev/programming-guides/proto3/`) |
| **protobuf `json_name`** | Decouples the JSON wire key from the `.proto` field identifier | Only a partial fix: "many Protobuf runtimes' JSON codecs will accept both the name set in `json_name`, and the specified field name" — so old and new names both stay live, not a clean cutover (`buf.build/blog/totw-1-field-names`) |
| **protobuf field/message rename generally** | — | **Notorious.** Buf's own engineering guidance: designers "intended for it to be feasible to rename an in-use field. However, they were not successful: it can still be a breaking change" — generated-code identifiers change, JSON breaks, reflection-based tooling (sort-by-name, frequency-by-name middleware) breaks. Buf's verdict, quoted directly: "Renaming fields is nothing but tears and pain." A live protobuf GitHub issue (#3949) proposing an `alias` annotation to make renames safe for text/JSON/`FieldMask` remains open, unresolved, years old — direct evidence this is a known, still-unsolved pain point, not folklore |
| **GraphQL `@deprecated` directive** | Marks a field/argument/enum value/input field as deprecated with a `reason` string; schema keeps serving it | Spec-defined (`spec.graphql.org`, `@deprecated` section): "must not appear on required (non-null without a default) arguments" — a real constraint, but otherwise a clean, low-cost mechanism because GraphQL fields are resolved by name per-query, not compiled into a fixed struct offset |
| **GraphQL field aliases in queries** | Client can request `newName: oldFieldName` in the query itself | Shifts the cost to the *caller* per-query rather than the schema owner — cheap for the schema owner, invisible to this survey's server-side rename cost |
one-of / alternate accessors were not independently found documented as a *renaming* mechanism in any of the primary sources checked (protobuf `oneof` is about mutual exclusivity, not name aliasing); flagged as **unverified as a renaming mechanism specifically**, separate from its well-documented use for mutual exclusion. |

**Summary judgment across all of these:** the mechanisms that are cheap (GraphQL `@deprecated`, query
aliases, additive-only backward compatibility everywhere) all share one property — the *consumer* re-reads
the current schema/definition on every interaction rather than baking a name into a compiled artifact.
The mechanisms that are notorious (protobuf field rename, Kubernetes API group removal) share the opposite
property — a name gets compiled into generated code, persisted storage, or hardcoded caller logic, so
changing it later requires either a parallel-old-and-new period or an explicit, versioned migration.

---

## 5. Literature: AIPs, DDD, and naming-registry governance

### Google AIPs

- **AIP-121 (Resource-oriented design):** "Resource-oriented APIs emphasize resources (data model) over
  the methods performed on those resources (functionality)," but explicitly warns "a service with a
  resource-oriented API is not necessarily a database" — cautioning against 1:1 mirroring of a storage
  schema into the API even while organizing around resources (`google.aip.dev/121`).
- **AIP-122 (Resource names):** resources "must expose a `name` field that contains its resource name";
  alternate ID fields, if present, "must apply the `OUTPUT_ONLY` field behavior classification"; and
  "resources must not expose tuples, self-links, or other forms of resource identification" —
  one canonical identifier, no side channels (`google.aip.dev/122`).
- **AIP-140 (Field names):** the `display_name` convention — "many resources have a human-readable name...
  This field should be called `display_name`, and should not have a uniqueness requirement" — is AIP's own
  version of the identity/label split, with the explicit rule that the label carries **no** uniqueness or
  identity guarantee, precisely so it can be edited freely (`google.aip.dev/140`).
- **AIP-180 (Backwards compatibility):** the master rule for this whole note: "Renaming a component is
  semantically equivalent to 'remove and add.'" When renaming is desired, the API "must add the new
  component, but must not remove the existing one." And, most directly on point for motif's Construct
  names: "A resource must not change its name. Unlike most breaking changes, this affects major versions
  as well" (`google.aip.dev/180`).
- **AIP-192 (Documentation):** covered in Section 3 — the MUST-comment rule.

### DDD / ubiquitous language

Martin Fowler's own bliki is the cleanest primary-ish source for the concept (Fowler is not Evans, but is
the standard secondary authority DDD practitioners cite, and his page is a direct primary source for
*his* formulation): "DDD deals with large models by dividing them into different Bounded Contexts and
being explicit about their interrelationships" (`martinfowler.com/bliki/BoundedContext.html`). Crucially
for motif's question, Fowler is explicit that unification is not always the goal: "different groups of
people will use subtly different vocabularies in different parts of a large organization," and "total
unification of the domain model for a large system will not be feasible or cost-effective" — contexts "may
have completely different models of common concepts with mechanisms to map between these polysemic
concepts for integration," reconciled through an explicit **Context Map**. Separately, Fowler's Ubiquitous
Language page (`martinfowler.com/bliki/UbiquitousLanguage.html`) frames the language as something that
"should be based on the Domain Model used in the software" and must be allowed to evolve: "the language
(and model) should evolve as the team's understanding of the domain grows," validated by "domain experts
[who] should object to terms or structures that are awkward or inadequate."

Read together, these give a specific, transplantable answer to "mirror the legacy vocabulary or translate
it": **DDD's own answer is neither, uniformly** — it is to decide explicitly which Bounded Context you are
speaking from and translate at the boundary (via a Context Map) rather than either forcing the public API
to speak LibLCM's internal class names, or forcing a from-scratch curated vocabulary that has no traceable
relationship back to the source of truth. Motif's Layer 0 (raw, LibLCM-shaped) vs. Layer 1 composer
surface (ADR 0009) is already, functionally, two Bounded Contexts with a translation layer between them —
the open question is really about naming *within* the already-drawn boundary, not whether to draw one.

### Naming-registry governance: OpenTelemetry semantic-conventions as a concrete model

OpenTelemetry's contribution process (`github.com/open-telemetry/semantic-conventions/blob/main/CONTRIBUTING.md`)
is a genuine committee-curated governance model, verified directly: a PR is "ready to merge" only once it
has "received at least two approvals from the code owners," area-owners review first, and "a review from
[@specs-semconv-approvers] is required on every PR." Non-trivial changes must sit for "at least two working
days since the last modification" before merging — a cooling-off period, not just an approval count.
Structurally, this is exactly the "reviewed `class → construct` map" ADR 0009 §3 already requires for
motif — a small, named group of reviewers gating additions to a shared vocabulary — just with OpenTelemetry's
process written down as an explicit doc rather than implicit team practice. The hard backward-compatibility
rule underneath it all: "Removing attributes, metrics, or enum members is not allowed, they should be
deprecated instead."

---

## 6. Verdict for this case — a small team, an AI-agent-first consumer, 900 fields

Every precedent above was built assuming its primary consumer is a **human developer** who writes the
identifier into source code, muscle memory, a blog post, a Stack Overflow answer, or a five-year-old
internal wiki page — which is exactly why every one of them treats renaming as expensive machinery
(new API version, `reserved`, `@deprecated`, Context Map) rather than a find-and-replace. That premise is
where motif's actual situation diverges, and the divergence is verifiable, not speculative.

Verified directly against Anthropic's own tool-use documentation (`platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools`,
`.../overview`): a tool's `name`, `description`, and `input_schema` are sent as a JSON array in the
**`tools` parameter of every single API request** your own application code constructs — there is no
persistent, separately-versioned "system prompt artifact" that a human must hand-edit and ship; the
definitions are exactly as current as the array your code sent on that call. Anthropic's own guidance
states plainly that **the description, not the name, is "by far the most important factor in tool
performance"** — the docs' worked "good vs. poor" example varies the *description* length and specificity
while leaving the *name* (`get_stock_price`) identical in both, and separately recommends "meaningful
namespacing in tool names" (a `service_resource_verb`-shaped convention, structurally identical to what
motif already has in `group/construct/verb`) specifically to keep tool selection unambiguous as the
surface grows — a naming concern about *disambiguation*, not about aesthetic appeal to a human reader.

Given that, here is where the precedents' logic actually points, once the premise they were built on is
swapped for motif's real one:

- **The renaming-is-expensive precedents (Kubernetes API groups, protobuf field renames, Stripe major
  versions) are answering a question motif does not currently have.** Their cost is dominated by consumers
  who compiled the old name into generated code, a database schema, or a blog post years ago. Motif's
  stated consumer — an AI agent — regenerates its understanding of the surface from the schema on every
  session; re-pointing it costs a find-and-replace over a generated manifest and possibly a corpus of
  worked examples, not a multi-year deprecation window across independently-deployed client fleets.
- **The identity/label split precedent (Salesforce, FHIR, AIP-140's `display_name`) still fully applies,
  and for a *different* reason than "humans like nice names."** The reviewer approving a Construct mapping,
  and any future maintainer reading the manifest, is human, even if the day-to-day API consumer is not.
  AIP-192's mandatory-comment rule and OpenTelemetry's committee-reviewed `brief`/governance process exist
  to keep a *shared, reviewed vocabulary* legible to the people maintaining it — that need is orthogonal to
  whether the wire identifier itself is pretty, and it does not go away just because an agent is calling
  the API.
- **The 81%/73.6%-unmechanical figure this brief opens with is itself evidence for the mechanical side,
  read against AIP-122 and AIP-180's core rule ("a resource must not change its name") rather than against
  the readability precedents.** A name that already has no derivable relationship to its source class is
  a name a script cannot regenerate or audit — every one of Kubernetes' round-trip guarantees, protobuf's
  `reserved` list, and AIP-180's rename-is-remove-and-add rule exists to protect callers from exactly the
  kind of silent, ungoverned rename risk that a fully hand-curated, unverifiable Construct column
  structurally invites at 900-field scale, independent of who or what is calling the API.
- **What does not transplant cleanly:** none of the precedents surveyed serve an audience that discards and
  reconstructs its entire understanding of the API on every call the way an LLM context window does. That
  is a genuinely novel cost profile this literature was not written for, and the verdict above leans on it
  explicitly rather than presenting it as something Kubernetes or Salesforce already validated.

**Bottom line:** the precedents converge on splitting identity from label, not on which side should be
pretty. For a consumer that is cheaply re-pointed and does not care about ugliness, but a maintenance
process that is human and does, the split argues for a **mechanically-derived, verifiable Construct
identifier** (even where ugly — `lowerFirst(Class)` with an explicit, checked exception table for the
41.5% that cannot resolve that way) **plus a mandatory, committee-reviewed `description`/label field**
carrying the curated, linguistically-meaningful grouping humans currently get from the hand-typed Construct
column. That is not a new invention; it is AIP-140's `display_name` split, OpenTelemetry's
`brief`-plus-governance pattern, and Salesforce's API-Name/Label pattern, applied to a case where the
"expensive to rename" side of every precedent's cost model does not hold for motif's primary caller today.

---

## What I could not verify

- **GitHub GraphQL naming vs. Rails models** — could not confirm from GitHub's own docs/blog whether
  GraphQL type/field names track internal Rails model names or were deliberately renamed during design.
  Flagged in §1.
- **Stripe's historical field/object renames** — Stripe's "backward-compatible changes" list was verified
  directly, but I did not enumerate Stripe's changelog for actual historical renames to characterize how
  painful/common they've been in practice.
- **Salesforce's own prose** on "does renaming a Label change the API Name" and "what breaks when the API
  Name changes" — `help.salesforce.com` and several `developer.salesforce.com` pages returned
  JS-rendered shells or unrelated TOC content to WebFetch; claims here rest on Gearset (a Salesforce DevOps
  vendor) and community/Trailhead reports, not a quoted Salesforce-owned page. Flagged throughout §1–2.
- **FHIR's 80% rule** — extremely well-attested secondhand (multiple independent summaries converge on the
  same wording), but I could not get a clean fetch of HL7's own Confluence page stating it (HTTP 405).
  Flagged in §1.
- **FHIR `code`/`display` drift-in-practice cost** — the datatype split is directly verified from the FHIR
  spec; a documented cost of the two drifting apart in a real deployment was not found from an HL7-owned
  source. Flagged in §2.
- **LSP's explicit rename/versioning governance** — confirmed capability-negotiation and `@since` tagging
  mechanisms; found no explicit written policy on renaming an existing method or capability. Flagged in §1.
- **OpenTelemetry schema-file rename-transform format** — the existence and purpose of `schema_url` and
  version-to-version transform files is corroborated by multiple secondary sources (Dash0, Honeycomb,
  ClickHouse blog posts) but I could not get a clean primary-source fetch of the schema transform file
  format itself from `opentelemetry.io/docs/specs/otel/schemas/` (404 on the specific URL tried). Flagged
  in §1.
- **A named postmortem on documentation/description "rot"** in any of the ecosystems checked (OpenAPI,
  protobuf, GraphQL) — lint-rule *existence* is well verified; a retrospective diagnosing rot as an
  incident, rather than linting pre-empting it, was not found. Flagged in §3.
- **protobuf `oneof`/alternate accessors as a renaming mechanism** — found no primary source using `oneof`
  for name aliasing specifically (its documented purpose is mutual exclusivity). Flagged in §4.
- **The exact "81%" figure in this note's brief** — the repo's own audit (manifest-trust-audit,
  2026-08-03) measured 41.5% with *no* mechanical relationship and 26.4% with an *exact* match, putting the
  "some human judgment required" share at 73.6% by that specific accounting. I could not locate a
  motif-internal artifact computing exactly 81% by any metric checked; the discrepancy is noted rather than
  silently reconciled, at the top of this note.

---

## Sources

Kubernetes: [deprecation policy](https://kubernetes.io/docs/reference/using-api/deprecation-policy/) ·
[Kubernetes API concepts/overview](https://kubernetes.io/docs/concepts/overview/kubernetes-api/) ·
[1.22 removals blog](https://kubernetes.io/blog/2021/07/14/upcoming-changes-in-kubernetes-1-22/) ·
[CRD versioning / conversion webhooks](https://kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/) ·
[API conventions (kubernetes/community)](https://github.com/kubernetes/community/blob/master/contributors/devel/sig-architecture/api-conventions.md) ·
[deprecated API migration guide](https://kubernetes.io/docs/reference/using-api/deprecation-guide/)

Stripe: [API upgrades](https://docs.stripe.com/upgrades)

GitHub: [The GitHub GraphQL API](https://github.blog/developer-skills/github/the-github-graphql-api/) ·
[about the GraphQL API](https://docs.github.com/en/graphql/overview/about-the-graphql-api)

Salesforce (see verification caveats in §1/§2): [Object Reference concepts](https://developer.salesforce.com/docs/atlas.en-us.object_reference.meta/object_reference/sforce_api_objects_concepts.htm) ·
[Namespace Prefix (Apex dev guide)](https://developer.salesforce.com/docs/atlas.en-us.apexcode.meta/apexcode/apex_classes_namespace_prefix.htm) ·
[Gearset — API rename through a pipeline](https://docs.gearset.com/en/articles/11003069-how-to-handle-api-rename-of-a-salesforce-component-through-the-pipeline)

FHIR: [Coding / CodeableConcept datatypes](https://hl7.org/fhir/datatypes.html#CodeableConcept) ·
[Resource](https://hl7.org/fhir/resource.html) · [Guide to Designing Resources (HL7 Confluence, "80% rule," reported but not directly quoted)](https://confluence.hl7.org/spaces/FHIR/pages/35718826/Guide+to+Designing+Resources)

LSP: [3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)

OpenTelemetry: [semantic-conventions CONTRIBUTING.md](https://github.com/open-telemetry/semantic-conventions/blob/main/CONTRIBUTING.md) ·
[naming conventions](https://opentelemetry.io/docs/specs/semconv/general/naming/) ·
[attributes registry](https://opentelemetry.io/docs/specs/semconv/attributes-registry/) ·
[Weaver semconv-syntax schema](https://github.com/open-telemetry/weaver/blob/main/schemas/semconv-syntax.md)

Protocol Buffers: [proto3 language guide (reserved fields)](https://protobuf.dev/programming-guides/proto3/) ·
[ProtoJSON format (json_name)](https://protobuf.dev/programming-guides/json/) ·
[protolint](https://github.com/yoheimuta/protolint) ·
[Buf — "Field names are forever"](https://buf.build/blog/totw-1-field-names) ·
[protobuf issue #3949 — backward-compatible rename](https://github.com/protocolbuffers/protobuf/issues/3949)

GraphQL: [October 2021 spec — Type System Descriptions / @deprecated](https://spec.graphql.org/October2021/) ·
[graphql-spec repo, Type System section](https://raw.githubusercontent.com/graphql/graphql-spec/main/spec/Section%203%20--%20Type%20System.md)

JSON Schema: [annotations reference (title/description)](https://json-schema.org/understanding-json-schema/reference/annotations)

Spectral: [Open Source API Description Linter](https://stoplight.io/open-source/spectral)

Google AIPs: [AIP-121 — Resource-oriented design](https://google.aip.dev/121) ·
[AIP-122 — Resource names](https://google.aip.dev/122) ·
[AIP-140 — Field names](https://google.aip.dev/140) ·
[AIP-180 — Backwards compatibility](https://google.aip.dev/180) ·
[AIP-192 — Documentation](https://google.aip.dev/192)

DDD / Fowler: [Bounded Context](https://martinfowler.com/bliki/BoundedContext.html) ·
[Ubiquitous Language](https://martinfowler.com/bliki/UbiquitousLanguage.html)

Anthropic (AI-agent-consumer verification, §6): [tool use overview](https://platform.claude.com/docs/en/agents-and-tools/tool-use/overview) ·
[define tools / best practices](https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools)

Motif's own prior work, cross-checked: [ADR 0009](../adr/0009-layered-api-primitives-and-composers.md) ·
[ADR 0022](../adr/0022-structure-is-derived-policy-is-five-rows.md) ·
[manifest trust audit, 2026-08-03](2026-08-03-manifest-trust-audit.md) ·
[prior-art note on versioning, 2026-08-03](2026-08-03-prior-art-canonical-diff-versioning-batch.md)
