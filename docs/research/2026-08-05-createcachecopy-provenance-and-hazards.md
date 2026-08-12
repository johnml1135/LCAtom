# `A1` — what `CreateCacheCopy` is, where it came from, and what it does not copy

*Research of 2026-08-05, against the `liblcm` and `FieldWorks` checkouts, grounding
[ADR 0016](../adr/0016-scratch-cache-copy-not-undo.md). Every claim below carries a citation; the two
findings marked **NEW HAZARD** are not mentioned anywhere in ADR 0016 and are the reason this note exists.
Nothing was modified. Claims spot-checked against source rather than taken on the investigation's word,
and one figure a previous investigation propagated is corrected.*

**Verdict: the cost model's *shape* is confirmed from source; its *ratio* is not, and a spike is still
required. But timing is no longer the most interesting question.**

> **The spike has since been built and run — see §10 for measured results.** Headline: hazard (a) is
> **confirmed at real scale**; hazard (b) did not reproduce; and the two strategies each win on a different
> axis, so the choice is not "which is faster."

## 1. Provenance — and why nobody can honestly cite the original intent

Both repositories' histories are **truncated at their repo-split boundaries**. `git log -S` for
`CreateCacheCopy`, `InitializeFromSource`, `RegisterInactiveSurrogate`, and `DoPortWithoutBootstrapping`
all bottom out at synthetic roots titled *"Initial commit with truncated history"* — liblcm's six
(`70dfa7e4`, `8201d9b4`, `309428a2`, `7b807ef2`, `8bc2c1da`, `f543ea59`) all authored by Eberhard Beilharz
and dated 2012-10-02; FieldWorks has the same pattern. **The true origin predates tracked history in both
repos and cannot be attributed to an author, commit, or message.** That is itself the finding: any claim
about original intent from `git blame` is unsupportable.

The surrounding evidence is strong, though, and it converges:

- **liblcm `7713f7c7` "Remove Db4o BEP"** (2015-03-03, Randy Regnier): *"Besides removing Db4o, this
  change also removes the '-app' command line switch. It is all FLEx now."* FieldWorks/FDO once had
  **multiple production backend providers** — XML and Db4o — which is exactly the scenario
  `DoPort`/`DoPortWithoutBootstrapping` exist to bridge.
- **liblcm `447fbef5` "Refactor BEPPortTests"** (2013-04-26): pure test reorganization, no functional
  change.
- **FieldWorks `f0d837288` "LT-18657 — Remove Deadwood (Round 6): Remove obsolete 'Samples' folder"**
  (2017-09-22) deleted `Samples/ImportExport/ImportExport.cs` (360 lines) alongside a companion document
  named **`Samples/ImportExport/HowToAddNewBEP.html`**.

So: `CreateCacheCopy` was **developer-facing SDK sample infrastructure for teaching "how to add a new
backend provider,"** used to benchmark XML ↔ Db4o porting. It was never a shipped end-user feature; it
did once have a real caller, in Samples rather than product code; its reason for existing (a second
production BEP) was removed in 2015, and the sample was deleted as deadwood in 2017.

### The harness we need was already written, and it is recoverable

The deleted sample **is exactly the spike**, including an average-of-N mode. Verified retrievable:

```
git -C ../FieldWorks show f0d837288^:Samples/ImportExport/ImportExport.cs
```

```csharp
private void ConvertToTargetBEP(FdoCache originalCache, FDOBackendProviderType bepType,
                                string targetPathname, string msg)
{
    TimeSpan elapsedTime;
    DeleteBEP(bepType, targetPathname);
    var start = DateTime.Now;
    using (var targetCache = FdoCache.CreateCacheCopy(bepType, new object[] { targetPathname }, "en", originalCache))
    {
        elapsedTime = DateTime.Now - start;
    }
    MessageBox.Show(this, msg + elapsedTime.TotalMilliseconds);
}
```

plus an `m_timingRun` mode looping `runCount` times and reporting *"Average time to import from XML to
{0}: {1}"*. **The people who built the API built this harness and measured it. No numbers survive.**
Start the spike from this file rather than from scratch.

## 2. Callers — "zero" is exact, with one important qualification

| Symbol | Occurrences |
| --- | --- |
| `CreateCacheCopy` | 2 files: the definition (`liblcm/src/SIL.LCModel/LcmCache.cs:177`) and one test call (`liblcm/tests/SIL.LCModel.Tests/Infrastructure/Impl/PersistingLayerTests.BEPPortTests.cs:181`) |
| `InitializeFromSource` | 4 files: `LcmCache.cs`, `BackendProvider.cs` (`:847`, `:878`), the interface declaration, and one test call (`BEPPortTests.cs:246`, via the `BackendStartupParameter` overload — *not* the overload `CreateCacheCopy` uses) |
| Either, anywhere in **FieldWorks** | **zero** |

**The qualification matters.** `RegisterInactiveSurrogate` — the primitive underneath
`DoPortWithoutBootstrapping` — is called from `XMLBackendProvider.cs:840` for **every object loaded on
every normal project open.** The surrogate/identity-map machinery is battle-tested at production scale.
What has never run outside one blank-project NUnit test is specifically the **cache-to-cache port path**:
`DoPortWithoutBootstrapping` and `CmObjectSurrogateFactory.Create(ICmObjectOrSurrogate)`. That is a
narrower and less alarming risk than "the whole mechanism is untested."

## 3. `A2` — two live caches coexist, and the scratch avoids the one shared singleton

`BEPPortTests.PortAllBEPsTestsUsingAnAlreadyOpenedSource` holds `sourceCache` open (`:166`) while
`targetCache` is created and used inside it (`:181-190`) — **two `LcmCache` instances live in one process,
confirmed.** The source is `CreateCacheWithNewBlankLangProj`, i.e. a blank project (688 objects, ~295 KB),
so *coexistence* is proven and *coexistence at scale* is not.

No unsafe shared state was found:

- **ICU init is process-global and idempotent by design.** `CustomIcu.cs:208-210` says so in its own
  comment: *"ICU docs say to do this after the directory is set… And it can be called n times with little
  hit."* Multiple caches sharing one ICU init is how every multi-project FieldWorks session already works.
- **`CmObjectId` interning is per-cache, not global** — `CmObjectId.FromGuid(Guid, IdentityMap)` interns
  through the passed map, and `IdentityMap` is a per-cache member (`BackendProvider.cs:30`). No static id
  dictionary exists in the file.
- **`SingletonsContainer`** holds `CoreGlobalWritingSystemRepository`, but it is only touched inside
  `InitializeWritingSystemManager` when `ProjectId.ProjectFolder` is non-empty (`BackendProvider.cs:552`
  guard). **A memory-only scratch with an empty project path never touches it** — which is a design
  requirement, not an accident. See §5.
- `CmObjectSurrogate`'s two statics (`s_classToConstructorInfo`, `s_canonicalClassNames`) are
  lock-guarded reflection caches keyed by class name, identical for any two caches on the same model.

## 4. The cost model — shape confirmed, ratio not

**Hot source pays `ToXmlString()` per object.** `RawXmlBytes` is set to `null` the moment an object is
fluffed (`CmObjectSurrogate.cs:519`). The copy constructor (`CmObjectSurrogate.cs:176-192`) then falls to
`Xml = sourceSurrogate.XML`, whose getter (`:441-455`) does
`((ICmObjectInternal)Object).ToXmlString()`. So every object the user has browsed pays full
serialization.

**Dormant source is a byte-array reference copy.** With `RawXmlBytes != null`, the same constructor does
`RawXmlBytes = surr.RawXmlBytes` — a reference assignment. No parse, no allocation.

**The work is O(n objects)** — not per-field, not a whole-project byte scan.
`DoPortWithoutBootstrapping` (`BackendProvider.cs:923-945`) is one surrogate-factory call plus one
`RegisterInactiveSurrogate` (amortized O(1) dictionary insert) per object.

**`kMemoryOnly` genuinely does zero disk I/O.** In `MemoryOnlyBackendProvider.cs`, `StartupInternal`
(`:38-43`), `ShutdownInternal` (`:48-51`), `CreateInternal` (`:70-74`) and `UpdateVersionNumber`
(`:97-100`) are no-ops; `Commit` (`:130-135`) reduces to
`m_identityMap.FinishUnregisteringObjects()`, itself a no-op (`CmObjectIdentityMap.cs:573-575`). No
`File`/`Stream` reference in the class. `InitializeFromSource(LcmCache)` also skips data migration
entirely (`BackendProvider.cs:878-891`), correctly — the source is already at the current version.

### Correction carried forward

The "identity map costs 12M for a middling-large project" figure, cited by an earlier investigation as a
cost, is **memory saved** by a deliberate SRP violation — one dictionary serving two purposes. The text is
at `IdentityMap`/`CmObjectIdentityMap.cs:43`, and the class is `IdentityMap` (the filename differs from
the type name). Do not carry this number as a cost estimate; it was already corrected once and reappeared.

## 5. NEW HAZARD (a) — a memory-only scratch's writing systems are default-synthesized

`BootstrapExtantSystem` (`BackendProvider.cs:529-533`) calls `EnsureWritingSystemsExist` for
`AnalysisWss`/`VernWss`, which does `wsManager.GetOrSet(wsId, out ws)` per tag (`:535-546`). For a
`kMemoryOnly` target, `InitializeWritingSystemManager` (`:548-564`) **returns early** — no
`WritingSystemStore` is attached — so `GetOrSet` can never find an existing definition and always falls
through to `Create(identifier)` (`WritingSystemManager.cs:301-321`), which synthesizes a fresh
`CoreWritingSystemDefinition` **from the bare language tag alone.**

So the scratch's writing systems carry ICU/CLDR defaults, **not the project's**: custom collation and
sort rules, valid-character lists, keyboards, fonts, and spell-check settings are absent. Anything in a
dry run that depends on writing-system behaviour beyond the tag — collation-sensitive sorting, valid-char
validation, homograph ordering — can diverge between live and scratch **silently.**

**Motif is currently safe, and the reason it is safe must become a stated invariant.** `SetGlossLowering`
resolves per cache by tag (`cache.WritingSystemFactory.GetWsFromStr(writingSystemTag)`), and
`LexSenseSnapshotter` converts handle → tag via `GetStrFromWs` before storing (`:45`) rather than
persisting a raw handle. Both are exactly right — handles are per-cache and must never be persisted or
carried across caches. Today's one operation writes a `MultiUnicode` string and reads it back, which is
collation-independent. **The first collation- or valid-char-sensitive operation makes this hazard real.**

## 6. NEW HAZARD (b) — custom-field `flid`s are re-derived, not preserved

`DoPortWithoutBootstrapping` does port custom-field *definitions* (`BackendProvider.cs:929`,
`m_mdcInternal.AddCustomFields(...)`) — but `AddCustomFields` (`LcmMetaDataCache.cs:1132-1142`) **ignores
the source's `CustomFieldInfo.m_flid`** and calls `AddCustomField(className, fieldName, …)`, which assigns
a fresh flid in the target:

```csharp
// LcmMetaDataCache.cs:936-948
if (m_clidToNextCustomFlid.TryGetValue(clid, out flid))
    flid += 1;                  // bump
else
    flid = (clid*1000) + 500;   // first custom field on this class
```

Deterministic *given a fixed enumeration order* — but the source enumerated is `GetCustomFields()`
(`:1148-1151`) over `m_customFields`, a **`HashSet<MetaFieldRec>`** (`:66`). `HashSet<T>` enumeration
order is an implementation detail, not a contract. With two or more custom fields on one class, **the same
custom field can receive a different flid in the scratch than in the live cache.**

**Motif is currently safe by rule, not by luck.** `AGENTS.md` rule 11 already states that custom-field
`flid` values are cache-local implementation details and never portable identity, and custom fields
resolve by `(ownerClass, internalName)`. Verified: **there is no `flid` reference anywhere in
`src/`**, and no custom-field code exists yet. This finding gives rule 11 a second, sharper reason — it is
no longer only about portability between machines, but about **two caches in one process disagreeing.**
Any future custom-field work must resolve by name against each cache's own metadata cache, never carry a
flid across the live/scratch boundary.

## 7. Not hazards, on inspection

- **`ReferringObjects` / back-references.** `m_incomingRefs` (`CmObject.cs:59`) is a per-fluffed-object
  runtime field rebuilt on demand by `EnsureCompleteIncomingRefs()` (`:3555-3573`). Since the port copies
  *every* surrogate unfiltered, the data needed to rebuild it is present — the scratch is in the same
  state as any freshly opened project. Not specific to the copy path.
- **Undo stack absent.** Each target gets a fresh service locator (`LcmCache.cs:193-204`), so
  `UndoStack` starts empty. ADR 0016 already treats this as intentional.
- **`LangProject` settings.** `LangProject` is itself a `CmObject`, ported via its own surrogate.

## 8. What a motif harness needs

1. **A public `IProjectIdentifier`** — the interface is fully public with 7 trivial members
   (`IProjectIdentifier.cs:64-120`), but liblcm's only implementation, `SimpleProjectId`, is **`internal`**
   (`Infrastructure/Impl/SimpleProjectId.cs:21`) and unusable. Write ~15 lines returning
   `Type => kMemoryOnly` and **`Path`/`ProjectFolder` null or empty** — that last part is what keeps the
   scratch clear of the global writing-system repository singleton (§3). This is grill item `A3`, now with
   a constraint attached.
2. `kMemoryOnly` resolves correctly inside liblcm — `LcmServiceLocatorFactory.cs:151-156` switches on
   `projectId.Type` and registers `MemoryOnlyBackendProvider`. Verified.
3. `LcmCache.CreateCacheCopy(projectId, userWsIcuLocale, ui, dirs, settings, sourceCache)` is public
   static (`LcmCache.cs:177-182`).

### Fixtures — and a trap

| File | Location | Size | `<rt>` objects |
| --- | --- | ---: | ---: |
| `MyProject.fwdata` *(real 152,222-object project)* | `FieldWorks/DistFiles/Projects/MyProject/` | 55.9 MB | **152,222** |
| `Amharic.fwdata` | `FieldWorks/DistFiles/Projects/Amharic/` | 11.3 MB | 25,840 |
| `integration_test_data.fwdata` | `FieldWorks/DistFiles/Projects/integration_test_data/` | 5.1 MB | 10,427 |
| `NewLangProj.fwdata` | `SIL.LCModel 11.0.0-beta0150` package, `contentFiles/Templates/` | 295 KB | 688 |
| `MyProject.fwdata` *(pangloss-cli stub, same file name)* | `%TEMP%/pangloss-cli-test-fwdata-name-*/` | 14.9 KB | **50** |

**The trap is the last row:** a 50-object stub that reuses the real project's file name. Note also that
`NewLangProj`'s 688 objects is the scale at which the *only* existing test has ever exercised this path —
221× smaller than the real 152,222-object project.

## 9. What would falsify ADR 0016

The spike must be built to break the design, not to produce a number:

1. **Cost falsification.** If a hot-cache → scratch copy at ~152k-object scale is slow enough that doing it once
   per session is not cheaper than mutate-and-rollback on the live cache, the "serialize heavily once"
   premise collapses.
2. **Ratio falsification.** If the *cheap* scratch → derived copy is not meaningfully cheaper than the hot
   copy — e.g. per-object registration overhead dominates at 152K objects regardless of whether
   `ToXmlString()` runs — then fan-out buys nothing and the two-tier design is unjustified complexity over
   re-copying from live each time.
3. **Correctness falsification, independent of timing.** Round-trip a project with **≥2 custom fields on
   one class** and compare flid numbers live vs. scratch; compare a customized writing system's
   valid-chars and sort rules live vs. scratch. If either drifts, a "pristine scratch" is **not equivalent
   to the live cache for LibLCM's own purposes**, and that matters whether the copy takes 200 ms or 20 s.

Item 3 is the one that would change the architecture rather than its parameters, and it is the one ADR
0016 does not currently anticipate.

## 10. Measured — the spike was built and run

*Harness: `spikes/SIL.Motif.Spikes.ScratchCache` (timing + equivalence at any scale) and
`tests/SIL.Motif.Tests/Runner/ScratchCacheEquivalenceTests.cs` (equivalence as ordinary assertions on the
small fixture). The project is copied to temp first; the source project is never opened or modified.*

**Fixture: real 152,222-object project — 53.3 MB, 4 writing systems, 3 custom fields.** Run on
2026-08-05, Release build, warm OS file cache.

| Measurement | 152,222-object project |
| --- | ---: |
| Copy the project's files to temp (control — plain I/O) | **49 ms** |
| Open the copy, cold | 1,816 ms |
| **A:** in-memory copy from a **cold** live cache | **209 ms** |
| **A:** derived copy from the **pristine scratch** — ADR 0016's fan-out | **140 ms** |
| *(setup: fluff all 152,222 objects to force the worst case)* | 2,852 ms |
| **A:** in-memory copy from a **fully hot** live cache | **4,445 ms** |
| **B:** file copy + open — the XML path | **580 ms** |

### Criterion 1 — the cheap fan-out: HOLDS, decisively

140 ms from a pristine scratch against 4,445 ms from a hot cache — **31.8×**. The two-cost model in the
Context section is real and the asymmetry is large. ADR 0016's central mechanism works.

### Criterion 2 — in-memory versus the proven path: DEPENDS, and the crossover is low

This is where the naive framing misleads. Strategy A's cost scales with how much of the live cache is
already fluffed, and B's does not:

- cold live cache: **209 ms vs 580 ms** — in-memory wins by ~2.8×;
- fully hot live cache: **4,445 ms vs 580 ms** — the file path wins by ~7.7×;
- **break-even at roughly 9% of objects fluffed.**

Nine percent is a low bar. A linguist who has been browsing for an hour is well past it, which means **in
a real interactive FieldWorks session the XML path is likely the cheaper one** — the opposite of what ADR
0016 assumed. What rescues strategy A is not raw speed but the fan-out: pay 4.4 s once, then 140 ms per
dry run.

### Criterion 3 — equivalence: PARTIAL, and this is the finding that matters

| Axis | A (in-memory) | B (file copy) |
| --- | --- | --- |
| Object count | 152,222 = live | 152,222 = live |
| Lexical entries | 1,462 = live | 1,462 = live |
| Sense text (50 sampled) | all match | all match |
| Custom fields | 3, **no flid drift** | 3, no flid drift |
| **Writing systems** | **0 of 4 value-equal** | **4 of 4 value-equal** |

**Hazard (a) is confirmed at scale, on every in-memory variant:**

```
en                 : character sets 2 -> 0; font 'Times New Roman' -> 'Charis SIL'; spell-check id differs
pt                 : character sets 2 -> 0; font 'Times New Roman' -> 'Charis SIL'
vern               : collation rules lost; character sets 2 -> 0; font 'Doulos SIL' -> 'Charis SIL'
vern-fonipa-x-etic : character sets 2 -> 0; spell-check id differs
```

Every writing system loses its **valid-character sets** (2 → 0) and its font, and the vernacular writing
system loses its collation rules. The file path loses nothing. So the choice between strategies is **not a
performance question at all**: it is whether the operation being dry-run cares how the project sorts and
validates characters.

**Hazard (b) did not reproduce.** Both fixtures carry **two custom fields on one class**
(`LexEntry.Plural`, `LexEntry.Singular`), which is the condition the hazard requires, and flids matched
exactly across all three in-memory variants. It is not disproven — `HashSet<T>` enumeration order remains
uncontracted, so the invariant "resolve by `(ownerClass, internalName)`, never carry a flid across caches"
stays as cheap insurance rather than as a response to an observed failure.

**Two predicted asymmetries turned out not to bite.** Skipping `cache.Initialize()` — and therefore
`DataStoreInitializationServices.PrepareCache` — produced **no object-count difference** on this fixture,
and the missing default-writing-system check raised nothing. Worth knowing, since both were flagged as
plausible risks from source reading alone.

### The follow-up that removed the choice

The obvious hybrid — build the pristine scratch from the XML path for real writing systems, then fan out in
memory for speed — **was measured, and it does not work.**

An in-memory copy taken from the **file-loaded** scratch, whose four writing systems are provably intact,
came back **0 of 4 value-equal**, in 78 ms. The loss belongs to **the target being `kMemoryOnly`**, not to
the source: `useMemoryWsManager` is hardwired true for that backend type (`BackendProvider.cs:263-265`),
`InitializeWritingSystemManager` returns early, and the target re-synthesizes its writing systems from
`AnalysisWss`/`VernWss` tags regardless of how complete the source's were.

So the two properties are mutually exclusive through this API, and no choice of source recovers it.

### What this means for ADR 0016 — one canonical path

1. **Every scratch is built by the XML path**: copy the project's files (~50 ms), open the copy (~550 ms).
   Equivalent to live on every axis measured.
2. **`CreateCacheCopy` is withdrawn from the Dry Run design.** It stays in the tree as the comparison point
   and is marked non-canonical in code.
3. **The fan-out is given up deliberately** — ~600 ms per scratch rather than ~120 ms. Real for an agent
   loop, not an interactivity problem, and it buys the removal of a whole class of "is this scratch
   equivalent enough?" defect.
4. **Uncommitted live edits are the one loss, and it fails closed.** A file copy reads the saved project, so
   an unsaved edit inside a footprint makes the Dry Run's anchor mismatch at apply time and **apply refuses**
   — the drift mechanism already covers it. The precondition is explicit: save before dry-running.

**Why not keep both and choose per operation?** Because that asks every future operation's author to answer
"does this depend on writing-system behaviour?" correctly, forever, with a silent wrong answer as the failure
mode. The project's own vernacular writing system loses its collation rules, and collation underpins ordering,
homograph numbering, and form comparison. Nobody can enumerate every place LibLCM consults a writing system
during a write and read-back, so the design should not require it. The reasoning burden is the defect.
