# Grill queue — FWLite grammar + PanGloss decisions

Status: ordered questions for a one-question-at-a-time owner grill.

Use this with `docs/fwLite-pangloss-verification-synthesis.md`. Ask exactly one question, record the
answer and rationale, then move to the next unblocked question. Facts should be resolved from source
or experiments rather than asked of the owner.

## Dependency order

```text
purpose and authority
  -> meaning of "better"
  -> comparison artifact
  -> review experience
  -> proposal/acceptance lifecycle
  -> canonical storage and sync
  -> privacy/AI
  -> proof slice
  -> ownership and release
```

## Q1 — What is the decision being made?

When PanGloss shows that a proposed grammar change alters word analyses, what exactly does the
reviewer decide?

Recommended answer: the reviewer decides whether to **accept the grammar proposal as a whole**.
Per-word judgments are evidence attached to that decision, not independent edits silently generated
by PanGloss. Allow “revise and rerun” and “insufficient evidence.”

Why first: the artifact and UI cannot be designed until the decision unit is known.

## Q2 — Who has authority to accept?

May an AI bot ever authorize the canonical grammar change without a human?

Recommended answer: not in v1. An AI may propose, explain, search, and recommend. Only a
project-authorized human accepts. A later project policy may delegate narrowly defined low-risk
decisions, but delegation must be explicit and auditable.

Dependency: Q1.

## Q3 — What does “better” mean?

Which dimensions count, and what happens when they disagree?

Recommended answer: use a scorecard, not one scalar:

- reviewed correctness/acceptability;
- coverage gain/loss;
- analyses added/removed;
- ambiguity increase/decrease;
- regressions on protected words;
- incomplete/time-limited analyses; and
- technical readiness/performance as a separate PanGloss report.

Do not automatically trade a known regression for a larger coverage gain.

Dependency: Q1.

## Q4 — What is a “differently analyzed word”?

Which changes put a word into the review set?

Recommended answer: any occurrence for which the normalized **set of analysis identities** differs,
including none→some, some→none, added alternatives, removed alternatives, or changed ambiguity.
Display-only ordering differences do not count. Incomplete/error outcomes form a separate bucket.

Dependency: Q3.

## Q5 — What is an analysis identity?

Which fields make two baseline/candidate analyses the same analysis?

Recommended answer: a versioned structural identity based on the linguistic analysis—morpheme
identities and roles, gloss/feature decisions where semantically material, and derivation—excluding
unstable display order and incidental runtime IDs. PanGloss must own and test this definition.

Dependency: Q4.

## Q6 — Which word unit is reviewed?

Is review by distinct surface form, corpus occurrence, sentence context, or all three?

Recommended answer: compute by occurrence, group by normalized surface form for triage, and retain
sentence/source context for judgment. A single form can require different decisions in different
contexts.

Dependency: Q4.

## Q7 — What can a native speaker actually judge?

Should the native speaker see formal parse structures or language-facing questions?

Recommended answer: show the word in context, proposed segmentation/gloss in accessible notation,
baseline/candidate contrast, and questions such as “Is this word possible here?” and “Does this
breakdown match its meaning?” Preserve “unsure / ask a linguist.” Formal traces remain available to
specialists.

Dependency: Q5–Q6.

## Q8 — What does an AI reviewer do?

For an internet-present language, what role is assigned to the AI?

Recommended answer: retrieve corroborating sources, explain contrasts, identify suspicious
regressions, cluster similar deltas, and recommend review priority. It does not impersonate a native
speaker or silently convert its recommendation into approval.

Dependency: Q2 and Q7.

## Q9 — What data may leave the device?

May corpus sentences, dictionary entries, grammar, reviewer comments, or community metadata be sent
to an external AI?

Recommended answer: deny by default, opt in per project and provider, preview the exact payload,
support redaction/minimization, and record consent, provider/model, retention policy, prompt/tool
provenance, and output digest.

Dependency: Q8.

## Q10 — Does PanGloss compare projects or accept a change?

Should PanGloss receive baseline+candidate artifacts, or baseline+semantic proposal?

Recommended answer: v1 compares two pinned materialized grammar artifacts. This keeps PanGloss
storage-agnostic and makes the comparison contract testable before FWLite grammar CRUD exists.
Later orchestration may accept a proposal and materialize the candidate outside PanGloss.

Dependency: Q4–Q5.

## Q11 — Which outputs are normative?

Is Markdown the record, or is it derived from structured data?

Recommended answer: a versioned machine-readable assessment is the record. Markdown/HTML, changed
word lists, and UI views are deterministic projections. Existing readiness reports remain separate
linked evidence.

Dependency: Q10.

## Q12 — When is an evaluation stale?

Which changed inputs invalidate review?

Recommended answer: any change to baseline project digest, proposal digest, PanGloss/engine version,
comparison schema, corpus/word-set digest, tokenization/normalization policy, or scoring policy marks
the evaluation stale. Reviewer comments remain; approval cannot apply without rerun or explicit
override.

Dependency: Q10–Q11.

## Q13 — Where does a rejected proposal live?

Should draft/rejected proposals be Harmony commits in canonical project history?

Recommended answer: keep proposal/review state separate from the accepted grammar projection until
an experiment proves that Harmony commits can represent rejection without polluting canonical
grammar history or requiring history removal. They may still be synchronized application entities,
but only after their schema/version rules define how older clients handle unknown proposal changes
and any later changes that depend on them.

Dependency: Q1, Q11–Q12.

## Q14 — Is grammar canonical in Harmony?

After acceptance, is FWLite's Harmony/MiniLcm grammar the source of truth, with `.fwdata` generated
or reconciled?

Recommended answer: treat this as the target hypothesis, not a settled fact. Accept it only after
one representative grammar property survives two-client sync, `.fwdata` materialization, LibLCM
reopen/read-back, FieldWorks editing, reverse sync, and PanGloss equivalence.

Dependency: Q13.

## Q15 — What is `.fwdata` in the loop?

Is it canonical, a continuously reconciled peer, or an on-demand evaluation/export artifact?

Recommended answer: for the first proof, it is an isolated on-demand artifact. Do not add
cross-store “atomicity” claims. Decide continuous FieldWorks interoperability only after measuring
and testing round-trip behavior.

Dependency: Q14.

## Q16 — What is the first proof slice?

Should the first implementation create a grammar construct or edit an existing one?

Recommended answer: first compare two existing `.fwdata` fixtures in PanGloss. Treat an existing
affix-template slot's `Optional` value, with referring irregular inflection types, as the leading
candidate because `HCLoader` uses it to decide whether to add null-affix rules. Select it only if a
manual `.fwdata` baseline/candidate experiment proves PanGloss imports it and emits the intended
nonempty delta. Then implement that value path before create/delete closure and generator breadth.

Dependency: Q10–Q15.

## Q17 — Which hard concurrency case gates expansion?

What must be proven before generating many grammar constructs?

Recommended answer: require at least:

- concurrent scalar edits;
- add/remove of a shared reference;
- delete with inbound references;
- cross-owner move; and
- concurrent rule insertion/reorder where order changes grammar behavior.

Dependency: Q14 and Q16.

## Q18 — What may the manifest generate?

Where is the human/generator boundary?

Recommended answer: generate DTO/property shapes, repetitive registrations, basic reference
enumeration/removal scaffolding, serializers, and test inventories. Hand-author and review create
semantics, validation, reference policy, ownership/cycle rules, semantic ordering, and LibLCM
lowering. Require construct-level conformance fixtures for generated output. Report raw manifest
rows, expanded `(construct, field)` pairs, and deduplicated LibLCM members separately; do not use
the `2 feeding + 3 index-as-identity` count as a proxy for implementation difficulty.

Dependency: Q17.

## Q19 — How is approval bound to evidence?

Does Harmony's existing commit hash suffice?

Recommended answer: no. Define canonical semantic serialization and a cryptographic content digest
covering the exact proposal, evaluation, policy, and decision. Bind approval to an authenticated
actor—by signature or an explicit trusted-server audit model. Harmony's current XxHash64 chain
covers commit ID and parent hash, not payload or approver identity.

Dependency: Q11–Q13.

## Q20 — What happens when the reviewer chooses “revise”?

Does revision mutate the proposal or create a new one?

Recommended answer: create a new proposal revision with an explicit predecessor. Preserve the old
evaluation and decisions. Never rewrite the artifact that a reviewer saw.

Dependency: Q12–Q13 and Q19.

## Q21 — Can decisions be partial?

Can a reviewer approve the grammar proposal but reject individual changed analyses?

Recommended answer: word judgments are annotations unless there is a separately authored lexical or
grammar revision that resolves them. A grammar change is accepted or rejected atomically; “accept
except these effects” means revise and rerun.

Dependency: Q1 and Q20.

## Q22 — What is the offline contract?

Which actions must work without a server or internet?

Recommended answer: author/save proposal, materialize locally, run PanGloss, inspect deltas, record
human judgments, and queue the decision must work offline. External AI augmentation is optional and
queued. Sync is eventually consistent and may invalidate stale evaluations.

Dependency: Q8–Q15.

## Q23 — Who owns each component?

Which maintained repository/team owns:

- grammar domain/API;
- Harmony primitives;
- LibLCM bridge and conformance;
- paired PanGloss assessment;
- review UI/workflow; and
- AI-provider integration?

Recommended answer: MiniLcm/FWLite owns grammar and review; Harmony owns only generic CRDT
primitives; PanGloss owns analysis identity and comparison; FwLiteProjectSync owns `.fwdata`
materialization; AI integration belongs at the review/orchestration boundary. Record named
maintainers before broad generation.

Dependency: architecture questions Q10–Q18.

## Q24 — What constitutes v1?

What must ship before this is useful to a real language team?

Recommended answer:

1. one meaningful FWLite grammar edit;
2. deterministic paired PanGloss assessment;
3. changed-word review with context;
4. explicit accept/reject/revise;
5. stale evaluation handling;
6. atomic accepted Harmony/proposal-state update; separately recorded and retriable `.fwdata` materialization;
7. restart and second-device sync;
8. an offline human-only path; and
9. one regression fixture proving the system can say “worse.”

Dependency: all preceding decisions.

## Suggested first live question

Start with Q1:

> When PanGloss shows that a proposed grammar change alters word analyses, are we deciding whether
> to accept the grammar proposal as a whole, or are we deciding word-by-word changes that can be
> independently applied?

Recommended answer: accept/reject/revise the grammar proposal as a whole; word judgments are
evidence, and any corrective edits become a new proposal revision.


