# ADR 0015 — Proposal, Assessment, and Dry Run: one word per concept

Status: accepted (2026-07-31)

Two different things were both called **Assessment**: an immutable PanGloss parser run
(`motif-overall-plan.md`) and the runner-side evaluation of proposed changes against a live LibLCM
model (`linguistic-assistant/CONTEXT.md`, and `Assessment`/`ChangeSetAssessor` in this repo's code).
The collision was live in two repositories at once. We resolved it by **giving each word to whoever
already owned it** rather than inventing new terms: **Assessment** is PanGloss's parse run (it ships a
`pg-assess` crate and uses the term in its own contracts), and the LibLCM-side evaluation becomes
**Dry Run** (lexbox already ships `DryRunMiniLcmApi`, which "records what would have been written").
The stored, reviewable unit of changes is a **Proposal**, and **Change Set** retires with the contract
ADR 0013 withdrew. The full glossary is [CONTEXT.md](../../CONTEXT.md).

## Considered options

- **Invent unambiguous new words for both** (e.g. *Parse Report* and *Impact Preview*). Rejected: it
  would have put Motif's vocabulary at odds with two codebases that already had working terms, and
  PanGloss's `pg-assess` would have needed renaming to follow.
- **Keep "Assessment" for the LibLCM side** (its original meaning in this repo) and rename PanGloss's.
  Rejected: PanGloss is a separate engine with its own published contracts and the weaker claim is
  ours — the LibLCM-side meaning belongs to the runner ADR 0013 withdrew.
- **Qualify both** — *PanGloss Assessment* and *Change Set Assessment*. Rejected: a vocabulary that
  requires a qualifier to be unambiguous is one people will drop the qualifier from.

## Consequences

**This is not a docs-only rename; that is why it is an ADR.**

- **It changes another repository's committed domain model.** `linguistic-assistant/CONTEXT.md` defines
  *Canonical Change Set*, *Change Group*, *Change Set Application*, *Change Set Assessment*,
  *Application Receipt*, and *CRUD+ Operation*. All six move. Same owner, so this is coordination, not
  negotiation — but it is a change in a repo whose AI tooling is built on those words.
- **It changes public CLI verbs.** `motif assess` becomes `motif dry-run`. The CLI is the wave-1
  interface for AI tooling, so settling this before that tooling exists is cheaper than after.
- **It changes a JSON property**: `changeSetId` becomes `proposalId`. Verified safe: the intent
  projection excludes the id, so the frozen conformance digests do not move. The digests were re-run
  and are unchanged.
- **Three terms in `linguistic-assistant` are left alone deliberately** — *Conforming Runner*,
  *LibLCM Mutation Plan*, and *Semantic Baseline*. They encode an architecture ADR 0013 withdrew, not
  merely a name. Renaming them would disguise a design question as a vocabulary one. They stay until
  that question is answered.
- **The manifest column `AssessPoisonsCache` keeps its name.** It is a data-file column across 898
  rows, cited by name with row counts in several documents. Renaming it is a schema migration with no
  vocabulary benefit, since the column is not domain prose.
