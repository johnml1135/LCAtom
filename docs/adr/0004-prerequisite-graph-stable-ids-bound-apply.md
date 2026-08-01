# ADR 0004 — Prerequisite graph, stable uniquely-minted identity, and bound apply

Status: accepted (2026-07-24)

## Context

A plan review compared Motif against Terraform, Django/EF Core migrations, Git/IPFS content
addressing, and Flyway/Liquibase. Three findings warranted decisions.

## Decisions

### 1. Prerequisites are a directed acyclic graph, not a single-parent tree

The earlier design let a Change Set declare one prerequisite, forming a tree of chains. A tree cannot
express two independently-authored Change Sets — one lexical (Linguistic Assistant), one grammar
(PanGloss) — later jointly required by a third without imposing a false order between them. That is
the diamond dependency that pushed Django migrations to a DAG and that EF Core's linear history is
the documented failure mode of. `requires` is therefore a set of `changeSetId`s. The reachable
prerequisite graph must be acyclic (topological-sort cycle detection); a dependent is assessed and
tested against the state with its full closure applied in topological order. The cost is identical to
the tree — a set instead of a scalar, the same "apply prerequisites first" fixture technique.

### 2. changeSetId is uniquely minted at creation then frozen; intent digest is the live content hash

Both pure content-addressing (id = hash of content) and content-seeding were rejected: a Change Set is
a mutable authoring artifact whose id must survive edits (or `requires` links and applied-log entries
dangle), *and* content-derived ids collide for degenerate content — ten Change Sets that each start
empty `{}` and diverge would share one id. Identity is therefore unique by construction, not content:

- `changeSetId` is a **128-bit id minted when the Change Set is created** — content-independent, unique
  by construction (a time-ordered value: millisecond timestamp plus random bits, in the standard
  suffix convention) — and then **frozen**. It never changes on edit or rebase, and is the sole
  linkage target.
- The **intent digest** is the live content hash (full SHA-256), recomputed on every edit. Identical
  authored intent yields an identical digest, so content equality and tamper-evidence are queries on
  the digest, not the id.
- The applied-log records both: it matches on the stable `changeSetId` (*was this exact Change Set
  applied?*) and stores the intent digest (*was this content already applied?* — the Flyway/Liquibase
  checksum pattern), surfacing a later apply whose content differs from the recorded one.

The intent digest stays a pure function of content — it excludes `changeSetId` — and rebase is
coherent: an amendment moves the intent digest and keeps the id.

### 3. Apply is bound to a prior Assessment

Terraform's default-safe workflow binds `apply` to a saved plan and refuses if state moved
underneath. Motif's apply previously treated a prior Assessment as optional, leaving the
assess→apply window a caller-managed race. Apply now requires a prior Assessment; its footprint digest
binds apply to a specific evaluated baseline, and a moved footprint stops apply with a drift
diagnostic. A bare apply with no bound Assessment is a hard error.

## Consequences

- `requires` becomes an array; the contract, conflict taxonomy, and fixtures speak of a prerequisite
  graph and closure.
- Identity and content are two fields with distinct lifecycles; the applied-log carries an added
  packed field for the intent digest.
- Apply gains a mandatory bound-Assessment precondition, closing the TOCTOU race by default.
