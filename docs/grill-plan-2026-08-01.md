# Grill queue — controlled Motif, Harmony, and LcmCrdt plan

*Created 2026-08-01 from the controlled-materialization and baseline-bound-apply reviews.*

This is the durable queue for future plan grilling. It is not a decision record. Resolved answers
move to [grill-decisions.md](grill-decisions.md), an ADR, or the owning plan; they are then crossed out
or removed here.

The [107-question evidence ledger](research/2026-08-01-grill-evidence-ledger.md) classifies the
stable question identifiers as **47 resolved principles, 27 bounded evidence tasks, and 33 owner
decisions**. Keep the identifiers below for traceability, but interview only the owner decisions and
use the architecture-first lineup below. Q103 is an exact duplicate of Q44 and is never asked.

## Active architecture-first grilling lineup

This order supersedes the numerical gate order for interviewing. It puts decisions that change the
system boundary ahead of implementation policy and operational detail.

1. **Product authority and migration:** Q1, Q2.
2. **Proposal/history/materialization architecture:** Q44, Q53, Q54, Q61, Q64.
3. **Domain and evidence boundaries:** Q33, Q79, Q83.
4. **Trust and authorization architecture:** Q13, Q41, Q42, Q85.
5. **Durability and state ownership:** Q47, Q52, Q86.
6. **Privacy and workspace governance:** Q21, Q23, Q87.
7. **Diagnostic and resolution contract:** Q57, Q71, Q73, Q76, Q88.
8. **Organization and rollout:** Q89, Q90, Q99, Q100.
9. **Operational thresholds:** Q38, Q96, Q98.
10. **AI authority policy:** Q107.

Before asking each item, read its evidence-ledger disposition and recommendation. Once an owner
decision is recorded, remove it from this active lineup. Do not ask a `RESOLVED_FACT`; verify its
proof criterion. Do not ask an `EVIDENCE_TASK`; execute or schedule its named test or measurement.

## Research disposition — do not ask facts as preferences

The 2026-08-01 source and literature reviews close these defaults unless contrary implementation
evidence appears:

- v1 authorization uses a whole-project normalized semantic Baseline Token; scoped tokens require a
  proven transitive read/effect closure;
- plain Unicode uses NFD and rich strings use NFSC; `flid` is never portable identity;
- Dry Runs, Assessments, Check Runs, Reviews, and Receipts are immutable records with independent
  freshness;
- checks and approvals bind an exact Proposal revision, baseline, artifacts, tool contract, and
  policy revision;
- agents use private mutable workspaces; only the live authority may perform final apply;
- a lease/file lock alone is insufficient without fencing or final version validation;
- unknown/refused history is retained; strict refusal never auto-reactivates;
- alpha variables use stable keys in collaborative state and positional lowering only at the bridge;
- same MiniLcm/LibLCM names never prove semantic equivalence;
- cross-store success is a recovery state machine, not a distributed transaction;
- AI review is typed and advisory by default and cannot imply human/native-speaker approval.

When the queue reaches a question covered above, verify the linked evidence or conformance test and
do not present it as an owner preference. A binding ADR or live plan settles policy; an unimplemented
proof remains plan work and does not reopen the policy by itself.

## Trigger and interview protocol

When the repository owner says **“grill me,” “grill me on the plan,”** or an equivalent phrase:

1. Read [CONTEXT.md](../CONTEXT.md), the live plans, and
   [the 2026-08-01 plan amendment](plan-amendment-2026-08-01-controlled-materialization.md).
2. Check [grill-decisions.md](grill-decisions.md) and skip questions already settled.
3. Look up facts in code, tests, package metadata, and primary sources instead of asking the owner.
4. Ask **one decision question at a time** in the dependency order below.
5. Give a recommended answer, the strongest alternative, and the consequence of postponing it.
6. Follow each branch until its dependent decisions are either resolved or explicitly deferred with
   a trigger.
7. Record decisions immediately; do not rely on chat history.
8. Do not implement a decision merely because it was discussed. Update plans/ADRs first and obtain
   explicit approval for implementation.

The recommendations below are starting positions, not foregone conclusions.

## Gate 1 — authority and the canonical project

### Q1. What is authoritative at apply time?

**Question:** Is the loaded LibLCM/`.fwdata` project always the authority, or can an LcmCrdt snapshot
be authoritative in a Lexbox-hosted deployment?

**Recommendation:** The live LibLCM project is authoritative for Motif v1. Treat LcmCrdt as the change
store and transport until full `.fwdata` materialization and equivalence are proven.

**Why it matters:** Every baseline token, Drift check, and recovery rule depends on which state wins.

### Q2. May authority vary by deployment?

**Question:** Do we support both FieldWorks-authoritative and CRDT-authoritative projects, or choose
one authority model for v1?

**Recommendation:** Choose FieldWorks-authoritative only for v1; design the token with an
`authorityKind` so another mode can be versioned later.

### Q3. Which process owns live writes?

**Question:** Is FieldWorks itself, a local Motif host, or `FwLiteProjectSync` the unique apply
authority?

**Recommendation:** The process already owning the loaded `LcmCache` and project lifecycle mints the
capability and performs apply; Motif libraries never open the project.

### Q4. Can ordinary FieldWorks edits bypass that authority?

**Question:** During final apply, can another FieldWorks window or service write the same project?

**Recommendation:** No supported writer may bypass the same project lock/authority. If this cannot be
enforced, downgrade the guarantee explicitly rather than claiming atomic confidence.

### Q5. What does “exclusive” cover?

**Question:** Does exclusive apply authority cover only LibLCM mutation, or final token check through
save and Receipt handoff?

**Recommendation:** It covers final comparison, UOW, read-back, commit, `.fwdata` save, after-token,
and durable recovery marker. Receipt completion may finish afterward only through reconciliation.

### Q6. Is an OS file lock sufficient?

**Question:** Can the existing `.fwdata` lock be the entire authority contract?

**Recommendation:** Use it as one enforcement mechanism, not the API contract. The API exposes an
apply capability; the host proves that its implementation excludes every supported writer.

## Gate 2 — BaselineToken semantics

### Q7. Whole-project or scoped v1 token?

**Question:** Should v1 authorize against the whole semantic project or only the Proposal footprint?

**Recommendation:** Whole-project semantic token for v1. Retain scoped footprints for explanation and
future optimization.

### Q8. What exactly enters the semantic digest?

**Question:** Which LibLCM classes, fields, writing systems, custom fields, and tombstones participate?

**Recommendation:** Every in-scope canonical semantic field plus identity, ownership, and order;
exclude Motif bookkeeping and cache-local implementation fields. Make omissions manifest-driven.

### Q9. Does out-of-scope data invalidate a Proposal?

**Question:** Should changes to Notebook, scripture, or interlinear analysis alter the token?

**Recommendation:** No, unless the Proposal reads or affects them. Define the token as the entire
Motif semantic authority, not every byte in `.fwdata`.

### Q10. How are custom fields included?

**Question:** Do all custom-field definitions and values enter the baseline?

**Recommendation:** Include in-scope definitions by portable `(ownerClass, internalName)` and their
semantic values; never hash project-local `flid` as identity.

### Q11. How are strings normalized?

**Question:** Does the baseline hash raw storage or normalized semantic values?

**Recommendation:** Hash canonical semantic snapshots using LibLCM conventions: NFD plain Unicode,
NFSC rich strings, canonical writing-system identity, and deterministic run formatting.

### Q12. Does a Harmony head belong in the token?

**Question:** Is the Harmony head required, optional provenance, or forbidden?

**Recommendation:** Include an optional state vector/head as provenance and synchronization guard;
never use it as the sole proof of LibLCM state.

### Q13. Does the token need a generation counter?

**Question:** Should the host maintain a monotonically increasing project generation in addition to
the semantic digest?

**Recommendation:** Yes if it can be atomically maintained by every supported writer; otherwise omit
it rather than create false authority.

### Q14. What version fields invalidate comparison?

**Question:** Do LibLCM, model, Manifest, snapshot schema, normalization, and lowering versions all
participate?

**Recommendation:** Bind all interpretation-affecting versions. Version mismatch causes re-Dry-Run,
not a best-effort comparison.

### Q15. Can equivalent projects share a token?

**Question:** Is the token state-only, or bound to one project identity and authority?

**Recommendation:** Bind project identity and authority identity. A content-identical copy may cite
the same semantic digest but not impersonate the live project.

### Q16. When may scoped authorization replace whole-project hashing?

**Question:** What evidence is sufficient to declare a footprint sound?

**Recommendation:** Require a generated transitive read/effect closure, incoming-reference coverage,
LibLCM side-effect tests, and mutation testing that demonstrates no omitted field changes outcomes.

## Gate 3 — private agent workspaces

### Q17. Physical `.fwdata` copy or `LcmCache.CreateCacheCopy`?

**Question:** Which is the default private workspace mechanism?

**Recommendation:** Begin with a validated physical scratch copy because its lifecycle and isolation
are easiest to inspect; evaluate `CreateCacheCopy` as an optimization after conformance tests.

### Q18. May multiple agents share a read-only workspace?

**Question:** Can several agents inspect one loaded cache concurrently?

**Recommendation:** Prefer one cache per agent/process. Permit shared read-only use only after LibLCM
threading and cache-lifecycle behavior are explicitly proven.

### Q19. May an agent mutate its private workspace?

**Question:** Is agent experimentation read-only, or may it apply tentative Proposals locally?

**Recommendation:** Allow mutation inside a disposable private copy. Never treat those mutations as
canonical input; canonical input remains the semantic Proposal.

### Q20. How is workspace provenance proved?

**Question:** What record proves the private copy came from the named baseline?

**Recommendation:** Create and verify the baseline token during materialization, then store a signed
or host-authenticated workspace manifest with path-independent identity.

### Q21. How long are workspaces retained?

**Question:** Delete on completion, retain through review, or retain indefinitely as evidence?

**Recommendation:** Retain immutable artifacts needed to reproduce Dry Run/Assessment; delete mutable
workspaces after a bounded review/recovery window.

### Q22. What survives a crashed agent?

**Question:** Is a partial workspace recoverable, quarantined, or deleted?

**Recommendation:** Quarantine with lifecycle metadata; reuse only after token and project-integrity
validation. Never silently resume a writable cache after uncertain disposal.

### Q23. What sensitive data may leave the live machine?

**Question:** May a remote agent receive the full project copy?

**Recommendation:** Make workspace placement and disclosure an explicit policy decision. Default to
local execution for unpublished language data; support redacted/frozen artifacts separately.

### Q24. Who owns scratch cleanup?

**Question:** Motif, FieldWorks, or FwLiteProjectSync?

**Recommendation:** The host that creates and owns the project/cache lifecycle owns cleanup. Motif
returns records and never deletes host-owned projects.

## Gate 4 — Proposal and Dry Run binding

### Q25. Does every Proposal name one baseline?

**Question:** Can a Proposal be authored without baseline evidence?

**Recommendation:** Portable intent may exist without a baseline, but review/apply eligibility requires
an attached immutable Dry Run bound to one exact baseline.

### Q26. Is a Dry Run mutated or superseded?

**Question:** Can warnings or effects be updated in place?

**Recommendation:** Never. Each run is immutable; a new baseline or engine version produces a new
Dry Run ID.

### Q27. What must the Dry Run capture?

**Question:** Full before/after semantic snapshots or only effects?

**Recommendation:** Store canonical effects and digests plus enough before-state evidence to explain
conflicts; keep full snapshots content-addressed when size requires external storage.

### Q28. Must Dry Run use mutation-and-rollback?

**Question:** Is rollback on a copied cache acceptable for all operation families?

**Recommendation:** Allow it on disposable private copies, classify cache-poisoning operations, and
discard poisoned caches. Do not depend on rollback restoring every derived cache.

### Q29. Is “impact analysis” a contract term?

**Question:** Should the UI/API rename Dry Run to Impact Analysis?

**Recommendation:** No contract rename. UI may say “Dry Run — impact analysis” for clarity.

### Q30. How are warnings distinguished from conflicts?

**Question:** Can approval override either?

**Recommendation:** Warnings are acknowledged policy findings; conflicts make the current Dry Run
inapplicable. Override must be a new explicit Proposal/policy decision, never a boolean force flag.

### Q31. Is effect digest approval-grade?

**Question:** Does approval bind only intent or also expected effects?

**Recommendation:** Bind both Proposal intent digest and Dry Run effect digest. The same intent against
a different world is not the reviewed action.

### Q32. What makes a Dry Run stale?

**Question:** Baseline change only, or engine/model/policy changes too?

**Recommendation:** Any baseline or interpretation-version change makes it stale.

## Gate 5 — PanGloss Assessment linkage

### Q33. Does apply require an Assessment?

**Question:** Are all Proposals parser-evaluated?

**Recommendation:** Make Assessment policy-dependent. Grammar Proposals normally require one; purely
lexical or administrative changes may not.

### Q34. What binds an Assessment to the candidate?

**Question:** Proposal ID, Dry Run ID, exported artifact digest, or all three?

**Recommendation:** Bind the exact frozen candidate artifact digest, evidence-word-set digest, parser
version, Proposal, and Dry Run provenance.

### Q35. Can an Assessment remain fresh when the live project drifts?

**Question:** Is parser evidence invalidated by unrelated live changes?

**Recommendation:** The Assessment remains a valid historical fact about its frozen artifact; its
eligibility for current apply expires with the Dry Run baseline.

### Q36. Who decides whether an Assessment is acceptable?

**Question:** PanGloss, Motif, or review policy?

**Recommendation:** PanGloss reports facts; Motif stores them; application review policy decides.
Neither engine invents a verdict from one run.

## Gate 6 — apply capability and authorization

### Q37. Who may mint `ApplyAuthorization`?

**Question:** Any reviewer, the server, or only the live project host?

**Recommendation:** Only the authority capable of acquiring the final exclusive write lease mints it,
after review policy has approved the bound artifacts.

### Q38. How short-lived should it be?

**Question:** Seconds, minutes, or through a review session?

**Recommendation:** Long enough for final apply startup, normally minutes at most; never reusable
offline authority.

### Q39. Is it transferable?

**Question:** May another process or agent present it?

**Recommendation:** Bind holder/process/service identity and intended project. Delegation requires a
new authorization.

### Q40. What consumes it?

**Question:** On first attempt, successful commit, or completed Receipt?

**Recommendation:** Mark the nonce attempted before mutation and reconcile afterward. A failed or
uncertain attempt cannot simply replay the capability.

### Q41. Must authorization be cryptographically signed?

**Question:** Is an unforgeable token required in v1?

**Recommendation:** In-process typed capability is enough for a single trusted host. Cross-process or
server-issued capabilities require authentication and tamper evidence.

### Q42. Does approval bind the Harmony payload?

**Question:** Is `HAR-2` now mandatory?

**Recommendation:** If approval crosses a process/trust boundary or names a Harmony commit, add a
separate payload content digest before grammar volume. Do not rely on the current chain hash.

### Q43. Can an administrator force stale apply?

**Question:** Should there be an emergency override?

**Recommendation:** No “force apply” of stale evidence. An administrator may expedite re-Dry-Run or
author a new Proposal, but cannot convert unknown effects into reviewed effects.

## Gate 7 — atomicity and durability

### Q44. Is one Proposal exactly one Harmony commit?

**Question:** Can a Proposal span commits or share a commit?

**Recommendation:** For v1, one accepted Proposal maps to one strict atomic Harmony group, preferably
one commit. If chunking is required, add an explicit immutable group envelope.

### Q45. What happens to unrelated commits in the same sync batch?

**Question:** Does one strict refusal block them?

**Recommendation:** No. Retain and materialize unrelated commits; refuse only the strict atomic group
and its dependents.

### Q46. What counts as Proposal apply success?

**Question:** LibLCM UOW commit, file save, Harmony persistence, or Receipt completion?

**Recommendation:** User-visible success requires saved canonical data and a recoverable durable record
linking it to the Proposal. Earlier states are internal recovery states.

### Q47. Where is the applied marker written?

**Question:** Inside `.fwdata`, Harmony, application storage, or several places?

**Recommendation:** Put an idempotency/provenance marker in the canonical project within the same UOW
where feasible, then reconcile Harmony and external Receipt state.

### Q48. What if UOW commits but save fails?

**Question:** Retry save, roll back, or quarantine?

**Recommendation:** Keep exclusive authority, attempt documented recovery, and return
`NeedsReconciliation` if durability is uncertain. Never report success or blindly reapply.

### Q49. What if save succeeds but Harmony recording fails?

**Question:** Which side wins?

**Recommendation:** Canonical `.fwdata` wins; recovery reconstructs/records the missing durable edge
from the applied marker, after-token, and intended operation identity.

### Q50. What if Harmony records before `.fwdata` save?

**Question:** May sync expose history whose canonical apply later fails?

**Recommendation:** Treat it as prepared/refused history until the host confirms materialization.
Do not expose it as a successful Receipt.

### Q51. Do we need a saga/state machine?

**Question:** Can ordinary exception handling cover cross-store failure?

**Recommendation:** Use an explicit persisted recovery state machine; there is no distributed
transaction across LibLCM, filesystem save, Harmony, and Receipt storage.

### Q52. Who owns reconciliation?

**Question:** Motif library, host, or background service?

**Recommendation:** The project-lifecycle host owns execution; Motif defines portable states and proof
requirements. A background service may perform host-authorized reconciliation.

## Gate 8 — Harmony materialization policy

### Q53. Where is policy registered?

**Question:** Change discriminator, entity/field path, generic type, schema metadata, or combination?

**Recommendation:** Versioned registry keyed by change discriminator plus policy key; generated
LcmCrdt registration attaches field/domain metadata without teaching Harmony domain names.

### Q54. Does policy travel with history?

**Question:** Does each change declare its policy revision, or does the replica choose current policy?

**Recommendation:** History identifies the required policy key/minimum revision. Replicas lacking it
retain the change as opaque/refused rather than apply under another policy.

### Q55. May policy upgrades change old materialization?

**Question:** Can replay under a new policy produce different current state?

**Recommendation:** Only through an explicit migration/reprojection event with versioned diagnostics
and compatibility tests. Silent reinterpretation is unacceptable.

### Q56. What is the default policy?

**Question:** Current permissive behavior or fail closed?

**Recommendation:** Permissive for unregistered existing types; fail closed for declared strict types
and unknown strict policy revisions.

### Q57. Is refusal stored or purely derived?

**Question:** Must diagnostics synchronize as commits?

**Recommendation:** Derive from shared history and persist as an idempotent projection table. Sync the
source history and policy version, not automatically authored diagnostic commits.

### Q58. Can a refusal later become applied automatically?

**Question:** If later history restores valid anchors, should replay activate the old operation?

**Recommendation:** Default no for Motif strict groups. Require explicit resolution so an old refused
Proposal cannot surprise users later. Evaluate whether generic non-Motif changes need another policy.

### Q59. How are downstream dependencies handled?

**Question:** Refuse them individually or mark them deferred?

**Recommendation:** Preserve causal explanation: one root refusal, dependent operations
`DeferredByDependency`, atomic peers `DeferredByAtomicGroup`.

### Q60. What does an old client do with a strict change?

**Question:** Preserve as `OpaqueChange`, reject synchronization, or upgrade?

**Recommendation:** Preserve history as opaque and refuse materialization; surface a loud upgrade
diagnostic. Never reinterpret it as legacy `SetOrderChange`.

## Gate 9 — strict ordered grammar

### Q61. What exact intent does a move encode?

**Question:** Before/after one anchor, between two anchors, absolute sequence, or relative constraints?

**Recommendation:** Stable target plus left/right anchors when available, with explicit boundary cases.
Avoid absolute numeric order as canonical intent.

### Q62. Is one surviving anchor enough?

**Question:** If one neighbor disappears, may the move apply?

**Recommendation:** Only if the remaining anchor yields one unambiguous placement under documented
rules; otherwise refuse.

### Q63. What if an anchor moves concurrently?

**Question:** Follow the anchor or preserve the original gap?

**Recommendation:** Treat authored identity relationships as primary, but grill this with concrete
feeding/bleeding examples before schema freeze.

### Q64. What if two users move the same rule differently?

**Question:** Deterministic winner, multi-value conflict, or refuse both?

**Recommendation:** Retain both and refuse materialization unless one operation causally supersedes
the other. A deterministic winner may converge while violating both authors' review expectations.

### Q65. What if users move different rules into the same gap?

**Question:** Tie-break mechanically or refuse?

**Recommendation:** Permissive sequences may tie-break by stable operation identity. Strict feeding
order should refuse when alternative relative orders are linguistically material.

### Q66. Who determines linguistic materiality?

**Question:** Harmony, LcmCrdt, Motif, or PanGloss?

**Recommendation:** Harmony provides mechanism; Motif/LcmCrdt policy classifies the field as strict;
PanGloss Assessment supplies evidence but does not authorize a merge.

### Q67. Can PanGloss choose the better order automatically?

**Question:** If one order parses better, may it resolve the conflict?

**Recommendation:** No automatic resolution. Parser results inform a new explicit Proposal; they do
not rewrite authored intent.

### Q68. What real-project conformance corpus proves order?

**Question:** Which feeding, bleeding, insertion, deletion, and alpha-variable cases are required?

**Recommendation:** Build minimal real grammars where A→B and B→A differ, plus concurrent move/insert/
delete cases and actual FieldWorks round trips. Synthetic list tests alone are insufficient.

### Q69. Are `positional` fields strict or permissive?

**Question:** Does every positional field need fail-closed semantics?

**Recommendation:** No. Classify individually: `feeding` strict by default; ordinary display/order
collections use the permissive converging sequence unless evidence says order carries meaning.

### Q70. Are alpha variables sequences at all?

**Question:** Should their identity continue to derive from position?

**Recommendation:** No in CRDT storage. Use explicit stable keys/maps and lower to LibLCM positional
representation at the bridge.

## Gate 10 — diagnostics and orphaned work

### Q71. What user-facing noun replaces “orphaned fix”?

**Question:** Refused change, deferred operation, unresolved materialization, or orphan?

**Recommendation:** Contract term `MaterializationDiagnostic` with disposition `Refused`; UI may say
“This proposed change was retained but not applied.” Avoid “orphan” unless no owner/target exists.

### Q72. What is the stable diagnostic ID?

**Question:** Random GUID, content hash, or operation-derived key?

**Recommendation:** Deterministically derive it from `(CommitId, ChangeIndex, PolicyKey,
PolicyRevision)` using portable network-order identity rules.

### Q73. What evidence is stored?

**Question:** Reason code only or expected/observed facts and anchors?

**Recommendation:** Store stable reason, operation/entity/policy identity, expected and observed
canonical facts, anchor evidence, dependency/group disposition, and resolution link.

### Q74. Are diagnostics immutable?

**Question:** Update one record or preserve status history?

**Recommendation:** Stable diagnostic identity with versioned derived status; preserve historical
refused/resolved transitions for explanation.

### Q75. How does reconnect notification work?

**Question:** New server protocol or local recomputation after normal sync?

**Recommendation:** Initially recompute after normal commit catch-up and publish a local diagnostics-
changed event. Add server transport only if measurements require it.

### Q76. What must work without UI?

**Question:** Logs, query API, CLI exit code, health counter?

**Recommendation:** Query API plus structured CLI output/non-zero status for newly blocking
diagnostics; logs and counters support operations but are not the sole channel.

## Gate 11 — MiniLcm/LibLCM crosswalk and API evolution

### Q77. Who owns the crosswalk?

**Question:** Motif, lexbox, or jointly generated packages?

**Recommendation:** Source policy in Motif because the Manifest/generator needs it; require lexbox
review for mappings affecting its public API and generated output.

### Q78. What makes a mapping complete?

**Question:** Class names only or field/shape/capability detail?

**Recommendation:** Names, fields, cardinality, ownership, identity, representation, lossiness,
normalization, read/write support, wire names, and evidence.

### Q79. Should the grammar API use LibLCM names?

**Question:** Align `IMiniLcmGrammarApi` closely with LibLCM or follow MiniLcm product vocabulary?

**Recommendation:** Prefer domain-readable names with explicit LibLCM mapping metadata. Do not copy
prefixes merely for symmetry; do preserve one-to-one construct identity where it actually exists.

### Q80. Do we need a breaking MiniLcm v2 now?

**Question:** Rename the lexical API before grammar generation?

**Recommendation:** No. First publish the compatibility policy/crosswalk and identify demonstrated
client pain. Use aliases or a separate grammar namespace for exact mappings.

### Q81. Which serialized names are forever?

**Question:** Must existing Harmony discriminators and JSON properties remain readable indefinitely?

**Recommendation:** Yes for durable history. New canonical names may be introduced only with dual-read,
mixed-version tests, and an explicit write-version transition.

### Q82. How are lossy mappings exposed?

**Question:** Documentation only, capability API, or validation error?

**Recommendation:** Machine-readable capability/coverage metadata plus validation that refuses an
unsupported write before it enters history.

### Q83. Is morph-type creation a contract bug?

**Question:** LcmCrdt can express it while the `.fwdata` bridge refuses it. Remove, constrain, or
implement?

**Recommendation:** Ask maintainers. Until decided, mark it unsupported for cross-store Proposals and
make the capability mismatch explicit.

### Q84. Does matching terminology imply matching semantics?

**Question:** May the generator trust same-named types such as `PartOfSpeech`?

**Recommendation:** Never. Same-name mappings require the same shape/capability review as renamed
ones; they may be more dangerous because the mismatch is less visible.

## Gate 12 — security, operations, and governance

### Q85. What threat model does authorization address?

**Question:** Accidental drift only, malicious callers, replay attacks, or server compromise?

**Recommendation:** State v1 explicitly. At minimum cover accidental concurrency, stale clients,
token replay, wrong-project use, and process crash; add cryptographic requirements for untrusted
boundaries.

### Q86. Where are tokens and artifacts stored?

**Question:** Harmony resources, Lexbox database, local content-addressed store, or all three?

**Recommendation:** Define portable records and content digests first; let application storage vary.
Harmony resources are suitable for referenced immutable artifacts but not mandatory core storage.

### Q87. Are private language artifacts encrypted?

**Question:** At rest, in transit, and in agent-provider disclosure?

**Recommendation:** Follow host/project policy; treat full workspace copies as sensitive canonical
data. Record disclosure scope separately from Proposal semantics.

### Q88. Who may resolve a refusal?

**Question:** Original author, reviewer, administrator, or any writer?

**Recommendation:** Application policy controls authority; the resolution is always explicit authored
history with provenance, regardless of role.

### Q89. Who staffs and reviews cross-repo work?

**Question:** Which team owns Harmony primitives, lexbox integration, and Motif contracts?

**Recommendation:** Secure named maintainers and review expectations before M2's first external PR;
repository location alone is not staffing.

### Q90. What is the compatibility rollout order?

**Question:** Harmony package, lexbox pin, Motif generator, or clients first?

**Recommendation:** New Harmony primitives and opaque-safe behavior; prerelease package; lexbox pin and
mixed-version tests; Motif generator targeting them; then strict Proposal clients.

## Gate 13 — proof, rollout, and stop conditions

### Q91. What is M4's smallest proof?

**Question:** Reuse `lexical/sense/setGloss` or wait for grammar construct 1?

**Recommendation:** Prove baseline/capability/Drift/recovery first with `setGloss`; repeat the gate with
grammar construct 1 in M5.

### Q92. What concurrency matrix is mandatory?

**Question:** How many replicas and arrival orders?

**Recommendation:** At least two-replica exhaustive order permutations for focused cases, plus three-
replica reorder/insert/delete and duplicate/late delivery tests.

### Q93. What crash points are injected?

**Question:** Which durability boundaries receive fault injection?

**Recommendation:** Before UOW, during operations, after UOW commit, during save, after save, before
Harmony record, after Harmony record, and before Receipt completion.

### Q94. What old-client matrix is required?

**Question:** How far back must mixed-version sync work?

**Recommendation:** At least the currently deployed FieldWorks Lite/LcmCrdt version and the new
version; preserve strict changes opaquely on old clients.

### Q95. What performance budget applies to whole-project hashing?

**Question:** Maximum project size and acceptable Dry Run/apply latency?

**Recommendation:** Measure real small, medium, and large projects before optimizing. Correctness wins
for v1; introduce incremental hashes only with equivalence tests.

### Q96. What is the stop condition for scoped tokens?

**Question:** When do we abandon optimization?

**Recommendation:** If proving closure costs more complexity than whole-project hashing costs users,
keep whole-project tokens.

### Q97. What is the stop condition for strict automatic merge?

**Question:** When do we accept that some grammar edits require coordination?

**Recommendation:** If the operation cannot be made invariant-confluent without losing authored
meaning, retain/refuse and require explicit resolution. Do not force CRDT cleverness past semantics.

### Q98. What telemetry proves the policy works?

**Question:** Count refusals, Drift, re-Dry-Runs, recovery, latency, and resolution time?

**Recommendation:** Yes, with privacy-safe structured reason codes. Use evidence to decide whether
strictness is too broad or workspace/token costs need optimization.

### Q99. Who signs off before grammar volume?

**Question:** Which reviewers must approve M4 and M5 gates?

**Recommendation:** Harmony maintainer, lexbox/FwLite maintainer, LibLCM/FieldWorks lifecycle owner,
Motif contract owner, and a grammar/HermitCrab domain reviewer.

### Q100. What would make us revise this architecture?

**Question:** Which falsifying evidence reopens the decisions?

**Recommendation:** Reopen if the live authority cannot exclude writers, whole-project semantic state
cannot be canonically represented, old clients cannot preserve strict history opaquely, or real
grammar tests show anchor-based refusal cannot distinguish safe from invented ordering.


## Gate 14 — product identity and expansion

### Q101. Which authority modes ship in v1?

**Disposition:** `RESOLVED_FACT`; retain the migration/conformance proof and do not interview.

**Recommendation:** CRDT-native authority is the destination, with one FieldWorks-hosted transition
mode. Every artifact declares authority kind/epoch; never allow dual merge authority.

### Q102. Is Motif the product owner of the PR-like workflow?

**Disposition:** `RESOLVED_FACT`; retain staffing under Q89 and do not interview.

**Recommendation:** Yes. Services may live with LexBox or FieldWorks, but
Proposal/Check/Review/Decision semantics remain Motif-owned.

### Q103. Is one Proposal exactly one Harmony commit?

**Disposition:** Duplicate evidence task, superseded by Q44; never ask separately.

**Recommendation:** Treat one Proposal revision as one explicit immutable atomic group; research
whether Harmony can encode that in one commit before adding an envelope.

### Q104. What does “grammar first” mean?

**Disposition:** `RESOLVED_FACT`; retain M4/M5 acceptance proof and do not interview.

**Recommendation:** `setGloss` may prove lifecycle mechanics; the first product operation family
and domain conformance gate is grammar.

### Q105. When does text become authorable?

**Disposition:** `RESOLVED_FACT`; retain the separate bounded-context gate and do not interview.

**Recommendation:** After a separate contract defines durable occurrence identity,
Unicode/segmentation coordinates, standoff layers, provenance, reanchoring/refusal, lowering,
read-back, conflicts, and manifest coverage. Until then text is immutable evidence.

### Q106. Does “modern reincarnation of fwdata” require full export?

**Disposition:** `RESOLVED_FACT`; retain per-domain compatibility proof and do not interview.

**Recommendation:** Selective bidirectional compatibility is mandatory per promoted domain. Full
CRDT-only creation of a brand-new `.fwdata` is a conditional product decision.

### Q107. Which reviews may an AI satisfy?

**Disposition:** `OWNER_DECISION`; ask last, after authority, risk, and sign-off policy are known.

**Recommendation:** Advisory by default. Autonomous apply requires an explicit,
operation-family-scoped, versioned policy with independent audit and no impersonation of human roles.
## Interview sequence

Use the **Active architecture-first grilling lineup** at the top of this document. The numbered gates
below it are an evidence catalog, not the interview order.
