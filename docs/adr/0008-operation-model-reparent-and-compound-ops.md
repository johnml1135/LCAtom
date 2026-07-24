# ADR 0008 — Operation model: reparent and compound graph operations

Status: accepted (2026-07-24)

## Context

A stress test of the operation vocabulary against real LibLCM writes showed the flat "one field on
one owner" family model cannot express several core edits. Evidence:
[stress-test findings](../stress-test-findings.md).

## Decisions

1. **Add a reparent (move-between-owners) family.** `ILcmOwningSequence.MoveTo` (`Vectors.cs:2077`)
   is a generic primitive that re-owns an owned subtree onto a *different* owner, used across the
   codebase (`OverridesLing_Lex.cs:9789`, `LexSenseOperations.MovePicture`,
   `FwDataMiniLcmApi.InsertSense`). It is expressed as one operation — move from `(ownerA, fieldA)` to
   `(ownerB, fieldB)` — not as `insert`+`remove`, which would double-count in the effect model.

2. **Add a compound/graph-operation category.** Some edits have a reach knowable only by running them
   — merge (`MergeObject`), subclass-convert-with-redirect (`ConvertLexEntryType`). A compound
   operation never claims a static [comparison footprint](../change-set-contract.md#comparison-footprint);
   it always forces full re-assessment, and its footprint and effects are the **read-back-derived**
   delta. This amends the footprint premise (static reach) for these operations only; read-back-based
   effect capture (ADR 0006 §1) is exactly what makes them tractable. Simple operations keep the
   static footprint.

3. **Changing an entity's GUID is not an in-place primitive.** LibLCM identity *is* the GUID, so the
   faithful decomposition is: create a new entity with the target GUID, then merge the original into
   it (`newEntry.MergeObject(original)`), which triggers the whole chain — homograph renumbering,
   project-wide reference repointing, cascade — as one compound operation (§2). This is also the
   building block for semantically merging two lexicons: create the canonical entry, merge the
   duplicates in. It is recorded because the design principle is to decompose a command into real
   LibLCM steps, capture every effect, and break nothing — a naive "set the GUID" primitive would do
   the opposite.

4. **Per-construct validation obligations** for the eventual v1 catalog (captured in the stress-test
   findings): `LexReference`'s implicit floor-of-two deleting the whole relation; the five parallel
   slot sequences over one unordered pool; object-valued custom fields blurring the schema/data
   family line; `sig="CmObject"` heterogeneous collections whose legal targets are context-dependent;
   ordering-sensitive multi-writes that must not be split across independent operations.

## Consequences

- Two new operation categories: reparent, and compound/graph operations.
- The footprint model is static for simple operations and read-back-derived for compound ones.
- GUID change and cross-lexicon merge have a specified decomposition rather than a lossy primitive.
- The v1 catalog carries per-construct validation rules, not just generic family semantics.
