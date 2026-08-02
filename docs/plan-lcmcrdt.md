# Withdrawn — the LcmCrdt plan

*Superseded 2026-08-01 by [Plan A](plan-motif.md), adopted from
[harmony-adoption-report.md](harmony-adoption-report.md) proposal 2. Kept as a record of what was
planned and why it is no longer needed. `CRDT-*` numbers are not reused.*

## Why it is withdrawn

The plan existed to route grammar through the CRDT: generate LcmCrdt entities and change classes from
`MasterLCModel.xml`, teach MiniLcm about grammar, and reconcile CRDT state back to `.fwdata`. Plan A
targets **LibLCM objects directly** in the process that owns the live cache, so none of that routing
exists any more, and the work it required goes with it.

Nothing here was requested by FwLite or its users. Every item was *necessitated by the previous plan*.
Withdrawing them takes nothing away from anyone.

## Disposition

| Item | Was | Now |
| --- | --- | --- |
| `CRDT-1` — accept generated possibility-list output | M2 | **Withdrawn.** Generation targets LibLCM operations, not MiniLcm change classes |
| `CRDT-2` — bump the `SIL.Harmony` pin for `HAR-3/5/6` | M3 | **Withdrawn.** There are no `HAR-*` items |
| `CRDT-3` — model the 3 alpha-variable fields as keyed maps | M3 | **Available, not blocking.** A genuine FwLite modelling fix, independent of Motif — `JsonPatchValidator` already rejects index paths, so this is arguably a latent bug regardless |
| `CRDT-4` — `IMiniLcmGrammarApi` | M5 | **Withdrawn.** Grammar does not pass through MiniLcm |
| `CRDT-5` — construct-1 selective reconciler pair | M5 | **Withdrawn** |
| `CRDT-6` — generated output for the remaining 29 constructs | M6 | **Withdrawn** |
| `CRDT-7` — EF migrations for generated entities | M6 | **Withdrawn.** No generated entities land in LcmCrdt |
| `CRDT-8` — CRDT → brand-new full `.fwdata` materialization | conditional | **Available, not blocking.** This served FwLite's own export workflow and never depended on grammar |
| `CRDT-9` — authority/fencing, baseline-bound save/read-back/recovery | M4 | **Split.** The FieldWorks-adapter half is now `MOT-12`; the `FwLiteProjectSync` half is FwLite's own concern |

## What LcmCrdt keeps

Everything it has. LcmCrdt remains FwLite's substrate — the right tool for a mobile, offline,
multi-device product that cannot load LibLCM at all, because LibLCM's native ICU dependency has never
been cross-compiled for Android. Plan A does not touch it, deprecate it, or ask anything of it.

The measured cost this plan was going to remove is still real and still unaddressed: 38 change files
of which 36 are concrete one-offs, and hand-typed registration in `LcmCrdtKernel.ConfigureCrdt`. If
FwLite ever wants that generated, the manifest and the generator from
[Plan A](plan-motif.md) `MOT-3` are reusable — but it would need the MiniLcm↔LibLCM crosswalk that
Plan A deleted, and it would be FwLite's requirement to justify.
