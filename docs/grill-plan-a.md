# Grill queue — Plan A

*Created 2026-08-01, replacing the five grill queues deleted with the Harmony-routed plan. Questions
carried forward from those files are marked **(carried)**; the rest came out of adopting
[Plan A](plan-motif.md) and cross-reviewing it against
[plan-cross-repo.md](plan-cross-repo.md), [plan-lcmcrdt.md](plan-lcmcrdt.md),
[plan-product-architecture.md](plan-product-architecture.md), and
[motif-overall-plan.md](motif-overall-plan.md).*

**Ordering rule:** measurements first, because three later answers depend on them. Then the questions
that block M2, then M3, then M4. IDs are stable; do not renumber.

---

## A — Measure before deciding (blocks M2)

**A1. What does `CreateCacheCopy` actually cost?**
[ADR 0016](adr/0016-scratch-cache-copy-not-undo.md)'s entire value is the ratio between one copy from
a hot live cache and N copies from a pristine scratch. Both are asserted from the code path
(`ToXmlString()` per reconstituted object versus a surrogate copy-construct), neither is measured, and
`CreateCacheCopy` has **zero callers** in liblcm or FieldWorks. If a copy from a hot Sena-3-scale cache
takes ten seconds, the warm-scratch strategy changes shape. *Half-day spike. Nothing in M2 should be
designed before this number exists.*

**A2. Do two `LcmCache` instances coexist in one FieldWorks process?**
The service locator is per-cache and `InitializeWritingSystemManager` runs on the copy, which looks
sound. ICU initialisation is more global and was not traced. If they do not coexist, ADR 0016 needs a
different home for the scratch.

**A3. Is `IProjectIdentifier` publicly constructible with `Type = kMemoryOnly`?**
`BackendProviderType.kMemoryOnly` is public and `MemoryOnlyBackendProvider` is internal. If the
identifier cannot be built from outside liblcm, the scratch has to live on disk and A1's numbers
change.

**A4. Does `System.Text.Json` land cleanly in FieldWorks' `net48` graph?** *(`MOT-13`)*
FieldWorks has no STJ reference today. We would add STJ 8.0.5 plus six transitive `System.*` packages
to a runtime where binding redirects are historically painful, and its `Directory.Packages.props`
already pins `System.Memory 4.6.3` around a ParatextData conflict. Newtonsoft is **not** an escape
hatch — [ADR 0007](adr/0007-cross-language-digest-determinism.md) requires byte-identical canonical
JSON across runtimes. *If this fails, M3 needs a different answer and we should know early.*

## B — Scope and vocabulary (blocks M2)

**B5. Which family is M2's first generated family, and on what criterion?**
Plan A says "one family" without naming it. The possibility-list family is the obvious candidate — 37
in-scope rows, all `unordered` or `positional`, zero `AssessPoisonsCache=yes` — but that was chosen to
prove *generation into LcmCrdt*, and the target has changed. Is the cheapest family still the right
one when the acceptance test is now a LibLCM round trip?

**B6. Construct naming is not mechanical, and 17 manifest rows are multi-construct.** **(carried,
B19/B20)**
Both block `MOT-6` and neither is resolved by the generator. Unchanged by Plan A; still unanswered.

**B7. Roughly 300 of 473 in-scope rows were classified by heuristic, not by citation.** **(carried,
B17/B18)**
This matters more under generation than it did under hand-authoring, because the generator reads
`ComparisonClass` and `Verbs` directly and emits from them. Verify-lazily versus dedicated-audit is
still undecided, and Plan A did not change the answer — only the cost of being wrong.

**B8. L0's object-creation closure is uncomputed.** **(carried, B21)**
Blocks `MOT-7` sequencing.

**B9. What is the versioning contract for the public intent surface?**
`contractVersions` maps group → major/minor, but nothing yet says what a minor bump may change, what
forces a major, or how long a runner must accept an older group version. This is now more urgent, not
less: the intent contract is the public surface and the lowered plan is private, so the public half
carries all the compatibility obligation.

## C — Engine behaviour (blocks M2/M5)

**C10. Does `AssessPoisonsCache` still have a consumer?**
ADR 0016 retires `DerivedCachePoisoningOperationKinds`, which was the column's only reader. The column
is still the honest answer to "does this operation touch a forward-only derived cache," and the liblcm
upstream fix would want it. Keep it, retire it, or repoint it — but decide, rather than leaving a
manifest column that nothing reads.

**C11. Is the liblcm `Rollback`/`Undo` hook asymmetry worth an upstream PR?**
Not blocking — ADR 0016 routes around it by never reverting. It is still the correct fix, it would let
C10 resolve cleanly, and the Avalonia/`net10.0` migration already has people in that codebase. Raise
it now or accept the workaround permanently?

**C12. Does a reviewer actually see that a phonological reorder changed the grammar's meaning?**
*(`MOT-8`, re-scoped)* Effects carry identity-keyed moves rather than positional rewrites, which is
necessary but may not be sufficient. This is the surviving half of the old ordered-grammar question,
and it is a **review** question now, not a convergence one.

**C13. What refuses an alpha-variable edit that would exceed 24 per rule?**
The ceiling is a fixed 24-entry array, it throws, and it kills the whole grammar load. A pre-apply
check must simulate the exact first-appearance traversal rather than counting distinct constraints.
Where does that check live — generated per field, or hand-written per construct?

## D — Product and boundaries (blocks M4)

**D14. Two review surfaces, or one?**
FwLite already ships comment threads in `LcmCrdt/Changes/Comments/`. `MOT-10` builds a review domain.
Is that a deliberate second surface for a different audience, or duplication we should notice now?

**D15. Does review state need to work offline?**
Proposals and Receipts are immutable and need only an object store. Review state — comments,
approvals, decisions — is mutable. If offline review is required, that is the one place a CRDT would
genuinely earn its cost, and the answer changes `MOT-14`'s shape.

**D16. What does "optional per project" mean operationally?**
An unshared project never leaves the machine. Who flips the switch, can it be flipped back, and what
happens to Receipts already pushed?

**D17. Is grammar authoring genuinely desktop-only?**
Plan A puts grammar on the FieldWorks/LibLCM path, which cannot reach Android because LibLCM's native
ICU dependency has never been cross-compiled. That is a product decision falling out of an
architecture choice. Make it explicitly.

**D18. Who owns keeping the two vocabularies aligned?**
The [adoption report](harmony-adoption-report.md) recommends one intent vocabulary and two lowerings.
Nothing mechanical enforces that. Is a generated cross-check worth building, or is human review of the
correspondence sufficient — and whose review?

## E — Standing risks, not blockers

**E19. Chorus merges the applied log and does not understand it.**
`ProjectAppliedLog` writes into `LexDb.Resources` inside the `.fwdata`; Chorus three-way-merges the
`.fwdata` with generic field-level rules. Approval continuity therefore cannot be shared through the
project file. Neither proposal created this and neither fixes it; `MOT-14`'s Lexbox receipt store is
the answer. *Worth a disposable-project test to learn exactly how it fails.*

**E20. PanGloss has no release pipeline at all.**
CI is `ubuntu-latest` only, no artifact upload, no publish job, no binary for any OS. This blocks
`MOT-15` step 2 and, separately, the smallest clean local install. Not our repo.

**E21. `motif` CLI is process-per-command.**
`dry-run` then `apply` pays two full project loads. Nothing structural prevents a long-lived session
holding one cache and one pristine scratch — the Runner already takes a cache it does not own. Worth
doing once A1 says what a load costs.

---

## Deliberately not asked

These were live questions under the previous plan and are moot under Plan A. Recorded so they are not
rediscovered as gaps:

- how two people concurrently reordering phonological rules converge — single writer, Chorus between
  people, so it is Chorus's question;
- what the MiniLcm↔LibLCM crosswalk should contain — no crosswalk;
- whether `SetOrderChange` can carry feeding order — nothing rides on it;
- when to bump the `SIL.Harmony` pin — no Harmony dependency;
- whether Harmony's hash should cover the payload — Motif's intent digest already does, for Motif's
  own contract. It remains a real gap *in Harmony*, for FwLite, if FwLite ever needs tamper-evidence.
