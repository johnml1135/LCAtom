# `E19` — does Chorus safely merge Motif's applied-change log?

*Research of 2026-08-03. **Headline: the answer is "probably, but Phase 0 item 8 was never actually
closed, and the failure mode if it is wrong is worse than documented."***

## What the log physically is

`ProjectAppliedLog.WriteEntry` (`src/SIL.Motif.Runner/AppliedLog/ProjectAppliedLog.cs:66-78`) creates
one `CmResource` in `LangProject.LexDbOA.ResourcesOC` per applied Proposal, using exactly two fields:

- `Version` (`Guid`) — the stable `proposalId`, and **the only field read for identity**
  (`TryFindByProposalId`, `:53-57`).
- `Name` (`Unicode`) — `Motif|<format>|<timestamp>|<user>|<intentDigest>|<description>`, capped at 256
  chars by `AppliedLogFormat` (validated, not truncated).

In `.fwdata` this is **not** a nested structure. Verified against a real file
(`liblcm/artifacts/.../NewLangProj.fwdata`): every model object of every class is a **flat sibling**
`<rt class="..." guid="..." ownerguid="...">` under `<languageproject>`; ownership is an attribute, not
nesting. So "how does Chorus match Motif's `CmResource`" is not a per-collection question — it is the
same question as matching *any* top-level `<rt>` in the whole document.

## The claim, and how solid it is

`docs/implementation-plan.md:49-52` (Phase 0 item 8) says Send/Receive union behaviour was *"confirmed
at the LibChorus level, to be re-confirmed in FLExBridge."*

- **The LibChorus half is real.** Verified independently in the local `chorus` checkout:
  `ChorusNotesAnnotationMergingStrategy.cs:24-27` registers `annotation` and `message` with exactly the
  keyed strategy ADR 0003 cites. The mechanism exists and works as advertised.
- **The FLExBridge half never happened.** The implementation plan's own "Landed" paragraph does not
  list item 8. There is no test, spike record, or artifact anywhere in `motif` exercising a real Chorus
  3-way merge of a `.fwdata`.

**And the evidence cannot be gathered from what is on this machine.** No FLExBridge source checkout
exists. `FieldWorks` carries only `sil.flexbridge.ipcframework` (IPC only, no merge logic) and a
175 MB compiled installer. The two packages that actually contain the FieldWorks-model merge strategy
registration — **`SIL.ChorusPlugin.LfMergeBridge` and `SIL.Chorus.ChorusMerge`**, both referenced by
`languageforge-lexbox/backend/FwHeadless/FwHeadless.csproj:11-13` — are **not in the local NuGet cache**
(verified: only `sil.chorus.app`, `sil.chorus.l10ns`, `sil.chorus.libchorus`, `sil.chorus.mercurial`,
`sil.flexbridge.ipcframework` are present).

## What the generic engine does — traced, not assumed

Verified in `chorus/src/LibChorus`:

- **The default strategy is the dangerous one.** `MergeStrategies()`
  (`merge/xml/generic/ElementStrategy.cs:33-36`) sets `_defaultElement` to `new ElementStrategy(true)`
  — *order relevant* — with `MergePartnerFinder = new FindByEqualityOfTree()`.
  `FindByEqualityOfTree.GetNodeToMerge` (`FindNodeToMerge.cs:646-675`) matches only on **exact recursive
  XML equality** (`XmlDiff(...).Compare().Equal`). `DefaultElementToMergeStrategyKeyMapper` keys purely
  on element name — `"rt"` — for every object in the file.
- **Consequence.** If a GUID-keyed strategy *is* registered, two differently-edited copies of the same
  `<rt guid=X>` are matched and routed into a proper recursive 3-way merge of the object's fields
  (`MergeChildrenMethod.cs:154`). If it is **not**, `FindByEqualityOfTree` returns `null` for both
  sides, each is classified as a fresh addition (`:178-182`), and the merged file ends up with **two
  `<rt>` elements sharing one `guid`.**
- **Order-ambiguity is separable.** `AmbiguousInsertConflict` fires unless `OrderIsRelevant = false`.
  Distinct-GUID additions are **never dropped** by the generic algorithm regardless of key strategy —
  the order flag only governs whether a spurious note lands in `.ChorusNotes`.
- **The fallback of last resort is worse still.** If no `*-ChorusPlugin.dll` claims the file,
  `DefaultFileTypeHandler.Do3WayMerge` (`DefaultFileTypeHandler.cs:42-59`) does no XML-aware merge at
  all — whole-file "keep ours" or "take theirs," discarding one side's entire project.

## The actual risk split

| Scenario | GUID-keyed + order-irrelevant registered | Falls through to the default |
| --- | --- | --- |
| Two replicas each add a **distinct** `CmResource` — *the common case, every reviewer's independent apply* | Clean union, no note | **Still a clean union** (additions are never dropped) — cost is a spurious `.ChorusNotes` order note |
| Two replicas write the **same** `proposalId` differently — `applied-log.md`'s "sole collision" | Matched; one `CmResource` survives with a resolved `Name`. Costs provenance only, which `applied-log.md:66-70` already accepts | **Duplicate `<rt>` sharing one GUID** — a `.fwdata` structural anomaly LibLCM's loader was never designed to see, since GUIDs are the model's primary key. Load-time crash, or silent retention of whichever duplicate the loader's dictionary happens to keep |

**Only the bottom-right cell is a genuine open question — and it is precisely the one
`docs/applied-log.md:101-105` asserts is safely handled.**

## The argument that substitutes for the missing evidence

Because `.fwdata` is flat, **one** generic "match `rt` by `guid`, order-irrelevant" registration would
cover every FieldWorks class uniformly. Ordinary collaborative Send/Receive — two people each adding a
`LexEntry` offline, or each editing the same `LexSense` — has been load-bearing in FieldWorks for over
a decade. Were top-level `rt` matching *not* guid-keyed, that everyday usage would already produce
duplicate-GUID corruption and reorder-conflict spam on every concurrent sync, which is not a documented
FieldWorks failure mode.

That is **inference from necessity, not observation.** It is strong, and it is why the honest headline
is "probably safe" rather than "unsafe" — but it is not a substitute for running the check.

## The experiment that settles it

Cheap, because every piece already exists:

1. **Repro** — take `NewLangProj.fwdata` (already on disk), commit as ancestor in a bare Mercurial repo,
   branch twice. Branch A adds a `CmResource` with GUID `G1`; branch B adds `G2`. Separately, branch a
   second pair where **both** sides add the **same** GUID `G3` with different `Name` text.
2. **Merge with the shipped tools, not a hand-rolled harness** — drive it the way `FwHeadless` does,
   via `SendReceiveHelpers.SendReceive`/`CallLfMergeBridge`
   (`languageforge-lexbox/backend/FwHeadless/Services/SendReceiveHelpers.cs:73-83,192-215`), which
   invokes the real `LfMergeBridge.Execute("Language_Forge_Send_Receive", ...)`. That loads the real
   FieldWorks-model merge strategies **without needing FLExBridge source**.
3. **Assert** — distinct-GUID case: `G1` and `G2` each present exactly once, and **zero** `.ChorusNotes`
   entries (that is what proves order-irrelevance, not merely union). Same-GUID case: **exactly one**
   `<rt guid="G3">`, its `Name` one side's or the other's and not a hybrid, and `TryFindByProposalId`
   returning exactly one hit.
4. **Prerequisite** — restore `SIL.Chorus.ChorusMerge` and `SIL.ChorusPlugin.LfMergeBridge`. Their
   absence is itself the reason item 8 cannot currently be closed.

## What this means for the plan

**`MOT-14` is necessary but does not resolve `E19`.** Moving Receipts and effects to Lexbox
(`plan-motif.md:253-256`) fixes the *product* consequence. It does not change the fact that the log
physically lives in `.fwdata` and goes through Chorus on every Send/Receive regardless.

Two actions, in order:

1. **Run the experiment before relying on item 8.** Until then, `implementation-plan.md:49-52` should
   read as *not yet confirmed*, and ADR 0003 decision 2 should carry a caveat rather than be cited as
   settled.
2. **If the same-GUID race can produce duplicate GUIDs, the log's shape changes** — independent of
   `MOT-14`. Either (a) make same-`proposalId` writes idempotent at the *application* level, so two
   replicas never mint two `CmResource` records for one `proposalId`; or (b) stop depending on
   FLExBridge's opaque registration entirely — treat `LexDb.Resources` as strictly append-only and never
   let `WriteEntry` overwrite, so a collision can only ever be a clean duplicate append rather than a
   same-GUID edit collision whose outcome depends on merge fidelity.

If the experiment confirms the documented benign behaviour, no shape change is needed: thin,
append-only, GUID-identity-only is sound, and `MOT-14` is the right complete mitigation for everything
that matters semantically.
