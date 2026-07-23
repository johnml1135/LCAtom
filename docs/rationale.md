# Rationale and alternatives

## Why semantic CRUD+ instead of low-level complete CRUD

Low-level property operations could cover the model quickly but would make review meaningless,
expose LibLCM internals, permit invalid intermediate states, and couple Change Sets to generated
property layouts. Semantic operations preserve intent; the private mutation plan provides exact
coverage without becoming public input.

## Why one canonical C# runner

LibLCM and FieldWorks are C# systems. One compiled runner can be called in-process by FieldWorks,
through a process boundary by Python, and by test/worker hosts. Multiple implementations would
drift on ownership, text normalization, delete closure, and undo behavior.

## Why Change Set atomicity

The meaningful unit of work is the reviewed set of semantic operations. Per-operation transactions
permit half-realized intent. LibLCM already provides mature undo/redo, so the first implementation
uses one outer unit of work rather than constructing a second transaction system.

## Why warnings rather than universal enforcement

The runner is low-level infrastructure used by interactive FieldWorks, automated workers, and
analysis tools. It must diagnose baseline drift, overwrite, and changed effects consistently, but
the host has the context and authority to decide whether a warning requires approval, rebase, or
rejection. Invalid or ambiguous semantics remain conflicts/errors.

## Why mechanical diff refuses fuzzy matching

Matching entities from different origins is a linguistic/application decision. Embedding
fingerprints or similarity in the canonical engine would make identical inputs dependent on
heuristics and could silently conflate distinct entries. External tools may author mappings; the
runner validates and executes the resulting exact operations.

## Why Git is not part of the core

Change Sets resemble Git commits because they are content-addressed intent that can be re-assessed
and rebased. That does not imply that this library should own repositories, branches, permissions,
or storage. Assessments and Receipts provide the lineage graph needed by Git or a database later.

## Why `.fwdata` remains an opaque artifact

The saved file is LibLCM persistence, not a useful semantic review format. Exact byte hashes are
valuable checkpoints, but semantic snapshots and typed Change Sets are the diff/review layer.
Never text-merge `.fwdata`.

## Why custom fields do not receive invented GUIDs

Current LibLCM exposes a project-local `flid` and resolves definitions by class/name; its public
creation path does not persist a caller-selected custom-field GUID. An invented ID would not
survive independent project creation reliably. The physical locator therefore mirrors LibLCM.
A possible logical contract key remains non-semantic client metadata until explicitly approved.

## Why no CRDT

This runner provides deterministic sequential changes and explicit three-way conflict reporting.
CRDT convergence does not guarantee LibLCM or linguistic validity, particularly for ordered,
referential model structures. Lexical applications may use CRDTs outside this library, but
canonical application remains ordered and validated.
