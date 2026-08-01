# Motif

Motif is the semantic vocabulary and tooling for proposing, evaluating, and applying changes to a
FieldWorks language project — its lexicon and, above all, its grammar. It sits between FieldWorks
(which owns the data), Harmony (which merges changes), and PanGloss (which parses with the result).

## The unit of change

**Motif operation**:
A small, named, reusable unit of linguistic intent — `MergeLexicalEntries`, `SplitSense`,
`CreateAffixProcessRule`. Named for the musical sense: the smallest recognizable unit that recurs and
is developed across a work.
_Avoid_: command, CRUD+ operation, mutation, edit

**Lowering**:
Turning one Motif operation into the concrete changes that realize it against a particular store.
_Avoid_: compilation, translation, expansion

**Proposal**:
A stored, named set of Motif operations that is reviewable as one unit. It owns its attached
Assessments, has a lifecycle, and can be applied to a language project or discarded. A Proposal does
not combine unrelated changes.
_Avoid_: PR, change set, change group, patch, branch

**Construct**:
One of the ~30 grammar things a Proposal can be about — a stratum, a natural class, an affix template,
a phonological rule. The unit in which grammar support is staged and delivered.
_Avoid_: entity, class, feature

## Evaluating a Proposal

Two different evaluations, deliberately named apart. One asks *does the grammar parse better?*; the
other asks *what would this do to the project?*

**Assessment**:
An immutable PanGloss run: one grammar against one frozen set of evaluation words, producing parse
results and diagnostics. PanGloss owns this word — it has a `pg-assess` crate and uses the term in its
own contracts. Motif stores Assessments and compares them; it never renders a verdict from one.
_Avoid_: parse report, evaluation, score, verdict

**Grammar Delta**:
The exact structural difference between two Assessments — which analyses were added, removed,
retained, or became incomplete.
_Avoid_: diff, score change, improvement

**Dry Run**:
What a Proposal would do to a live LibLCM model, computed without mutating it: before-state, planned
effects, warnings, conflicts, and applicability. Named for lexbox's existing `DryRunMiniLcmApi`, which
records what *would* have been written.
_Avoid_: assessment, preview, plan, simulation

**Drift**:
The condition where the project has moved since a Dry Run was computed, so the Dry Run no longer
describes what applying the Proposal would do.
_Avoid_: staleness, conflict, merge failure

**Receipt**:
The record that one Proposal was applied to one project, naming the before and after state. The
durable edge in a project's history.
_Avoid_: application receipt, success result, audit log

## Where state lives

**Canonical data**:
The FieldWorks language project itself — `.fwdata` on disk, or the live LibLCM model loaded from it.
The authority on model invariants, ownership, and validity.
_Avoid_: source of truth, database, backing store

**Live model**:
A loaded, in-memory LibLCM model representing the project as it is right now, against which Dry Runs
are computed and Proposals applied. A Motif tool never opens or owns a project's lifecycle; it is
handed one.
_Avoid_: cache, session, connection

**Change store**:
The Harmony/LcmCrdt commit log holding Proposals as CRDT changes, so they merge between replicas
rather than conflicting.
_Avoid_: database, repository, queue

**HC interpretation**:
The rules by which grammar in a language project becomes a HermitCrab grammar — the semantics
FieldWorks' `HCLoader` implements and PanGloss ports. An authority on meaning, not a stored format.
_Avoid_: HC XML, export, projection

## Coverage and generation

**Manifest**:
The reviewed classification of every field in LibLCM's model — what is in scope, which Construct it
belongs to, how it compares, and which verbs it supports. Human judgement that exists nowhere else.
_Avoid_: inventory, schema, spec

**Name map**:
The hand-maintained correspondence between MiniLcm type names and LibLCM class names, which do not
match and cannot be derived from either side.
_Avoid_: mapping table, alias list

**Ordered grammar**:
The grammar whose meaning depends on sequence — phonological rule order encoding feeding and
bleeding, and alpha variables using position as identity. The part that cannot ride on a
last-writer-wins order value.
_Avoid_: sequences, lists, sorted fields
