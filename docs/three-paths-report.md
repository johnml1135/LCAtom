# Three paths to one cross-platform API — synthesis

> **SUPERSEDED (2026-07-27) by [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md).** This
> document was written without `SIL.Harmony` having been read — it was not checked out, and the
> analysis reasoned around the gap instead of closing it. Harmony already provides semantic change
> objects, hash-chained commits, per-object snapshots, before/after state at any commit, validation,
> and `OpaqueChange` for changes a client cannot yet interpret. **The architectural recommendations
> below do not stand.** The platform findings do.


*2026-07-27. Synthesis of [path 1](path-1-minilcm-extended.md), [path 2](path-2-motif-one-surface.md),
and [path 3](path-3-liblcm-crossplatform.md), each researched independently. Claims below were
re-verified against source before inclusion; where an underlying report was wrong, it is marked.*

## The finding that reframes the question

**"Cross-platform" is already achieved everywhere except mobile.** LibLCM runs on Linux and macOS
today, in production, shipped by SIL. Three independent confirmations:

1. **LibLCM's own CI runs its full test suite on `ubuntu-22.04`** and **publishes the official NuGet
   packages from the Linux leg** (`liblcm/.github/workflows/ci-cd.yml:24,72-79,92-94`). The
   `SIL.LCModel` package you consume was built on Ubuntu.
2. **Lexbox's server runs LibLCM on Linux.** `FwHeadless` references `FwDataMiniLcmBridge`
   unconditionally and ships in `mcr.microsoft.com/dotnet/aspnet:10.0`
   (`backend/FwHeadless/FwHeadless.csproj`, `backend/FwHeadless/Dockerfile`).
3. **FwLiteWeb ships LibLCM as a desktop binary on Linux and macOS.** It references
   `FwDataMiniLcmBridge` unconditionally (`FwLiteWeb/FwLiteWeb.csproj:36-38`, no `Condition`), and the
   release workflow publishes **`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`**
   (`.github/workflows/fw-lite.yaml:309-313,341-342`).

The `IncludeFwDataBridge` Windows gate that started this whole line of enquiry lives only in
`FwLiteMaui.csproj:26-27` — the **MAUI** shell, whose targets are android / ios / maccatalyst /
windows. There is no MAUI Linux target. That gate excludes **mobile**, not Linux.

> This corrects my own earlier `one-api-problem.md`, which implied LibLCM was Windows-bound. It is not.
> Both path 1 and path 3 caught this independently, which is why they were dispatched separately.

**So the requirement "Android, Linux, etc." is not one gap, it is one gap.** Linux is done. The
question is Android (and iOS, which is strictly harder). Every path below should be judged on that
and nothing else.

## The landmine that sits underneath all three paths

LibLCM binds to a **custom SIL-built ICU**, not stock ICU:

```csharp
private const string IcuucDllName = "icuuc" + Version + ".dll";
[DllImport(IcuucDllName, EntryPoint = "SilIcuInit", …)]
```
— `liblcm/src/SIL.LCModel.Core/Text/CustomIcu.cs:32,260`

`SilIcuInit` is not a stock ICU export. It loads FieldWorks-specific normalization tables (`nfc_fw`,
`nfkc_fw`). This is the `icu-fw` apt package the Linux CI installs from `linux.lsdev.sil.org`
(`ci-cd.yml:42-46`).

**Path 2's report called this a hard Android blocker. It is not — and what it actually is is worse.**
There is an explicit graceful fallback (`CustomIcu.cs:224-247`): `DllNotFoundException` →
`HaveCustomIcuLibrary = false` → *"Falling back to default ICU"* → return normally. Normalization then
silently switches from `nfc_fw` to `nfc` (`:409-419`).

So on a platform without the custom ICU, LibLCM **does not crash. It normalizes differently.** And
per `TsStringSerializer.cs:38-39`, the on-disk `.fwdata` form is **NFSC** — FieldWorks' own
normalization mode (`Kernel.cs:2408-2411`). Normalization determines string identity in the data file.

**A phone and a laptop would silently disagree about whether two strings are the same string** — no
exception, no log, no user-visible symptom, across a sync boundary. This is a data-integrity hazard,
not a portability one, and **it is shared by all three paths**: anything running LibLCM on a phone
must ship the custom ICU, and anything *not* running LibLCM must reimplement NFSC exactly. It is not
in Motif's issues register and should be.

## The three paths, scored against the actual gap

### Path 1 — Extend MiniLcm to grammar

**Verdict: fails the requirement, and is the most expensive of the three.**

The path has two sub-options and both fail:

- **Grammar in `FwDataMiniLcmBridge`** — inherits LibLCM, so it works on Linux/macOS/Windows and
  is excluded from Android by the MAUI gate. Grammar becomes desktop-only. Fails.
- **Grammar in `LcmCrdt`** — reaches Android, but requires reimplementing grammar semantics *and*
  referential integrity from scratch, growing the reference graph from 13 model classes to ~66.

The cost is not speculative; path 1 priced it from git history rather than estimating. Adding
`Publication` — close to the simplest possible construct — took **42 files and ~1,290 insertions** in
one PR (`4c3e5d51`, verified). `MorphType` took a PR plus a partial revert, because its write API had
to be *removed* when the generic playbook didn't fit its closed-taxonomy semantics. That is the
**floor**, from the easiest constructs. Grammar has ~30 constructs, all structurally harder.

The deeper objection is representational. Of four ordering mechanisms checked against `HCLoader` and
HermitCrab, **only one** (affix template slots) fits the existing CRDT primitive. Rule feeding-order,
alpha-variable index-as-identity (24-per-rule ceiling), and `MoAffixProcess.Output` position-resolution
each require non-local cascading logic that abandons the CRDT contract. `SetOrderChange.ApplyChange`
is `entity.Order = Order` (`LcmCrdt/Changes/SetOrderChange.cs:14-18`) — a last-writer-wins scalar. A
scalar cannot encode a feeding relation, and Harmony's log cannot fix that, because the representation
is chosen above Harmony.

**What it gets right, fairly:** MiniLcm ships. 612 tests (recounted — higher than the 431 previously
cited), five deployment targets, shared cross-backend conformance suites, working comment threads and
offline sync, and a real, repeatable construct-addition playbook. None of the other paths have shipped
anything comparable.

### Path 2 — Motif becomes the one API surface

**Verdict: cannot reach Android without a client/server split that undermines the premise — and it is
not honestly a "one API surface" in the first place.**

`Runner` and `Host` are bound to `SIL.LCModel`/`LcmCache`. On Android that means LibLCM on the phone,
which is the same unsolved problem path 3 owns. Absent that, Motif must run elsewhere and be reached
across a process boundary — which is **issue B13, and B13 is not merely unresolved, it is unstarted**:
zero gRPC, JSON-RPC, HTTP, or named-pipe code exists. The only consumer surface is an in-process CLI.

More fundamentally: Motif is a **change-authorship contract, not a CRUD API**. It has no answer for
"show me this entry" or "set this gloss as I type." Motif's own `one-api-problem.md` says exactly
this. So "Motif is the one API surface" is only true if you redefine the surface to exclude
interactive editing — which is most of what FieldWorks Lite does all day.

The generated-kinds bet — 332 kinds from ~12 handlers — is **sound in direction but unproven**: no
generator has been built or run. And the leverage figure applies only to the HC-reachable slice; the
full manifest needs ~25 handlers, and the exceptions that defeat generation (construct naming per
B19, the 17 multi-construct rows per B20, feeding order, index-as-identity, positional Output/Input)
are precisely the grammar cases that motivate the whole project.

**What it gets right, fairly:** the Change Set / Assessment / Receipt separation, atomic apply, intent
digests, semantic closed operations instead of raw property mutation, and a `Contract` layer that is
`netstandard2.0` and genuinely LibLCM-free. Nothing in MiniLcm does any of this, and the "propose →
review evidence → approve → apply" product needs all of it.

### Path 3 — Make LibLCM itself cross-platform with a good API

**Verdict: the only path that addresses the actual gap, and the blockers are far smaller than folklore
holds. Android is unproven, but nothing found shows it blocked.**

Every candidate blocker was checked and most evaporated:

| Candidate blocker | Reality |
| --- | --- |
| Linux support | **Already done**, three ways (above). <1% of 2,609 tests platform-gated |
| `Reflection.Emit` in LibLCM | **None** anywhere in `src/` |
| Registry, `ServiceController`, `SIL.Core.Desktop`, `protobuf-net`, file locking | Peripheral or already guarded; none on the `.fwdata` load/save/CRUD path. Registry use is confined to FieldWorks-6.0 migration code |
| `kernel32.dll` P/Invoke | Spell-checking only, peripheral |
| **`structuremap.patched`** | **Real risk, confirmed.** Byte-inspection of the netstandard2.0 assembly finds `Reflection.Emit`, `DynamicMethod`, `ILGenerator`, `Expression`, `Lambda` |
| **Custom ICU** | **Real, and the landmine described above** |

Two important nuances on StructureMap that the underlying report got in opposite directions:

- **Android is probably fine; iOS is not.** .NET for Android runs Mono with JIT available, so
  `Reflection.Emit` generally works. iOS prohibits JIT outright — there, `DynamicMethod` cannot work.
  Whether this is a gate depends on whether iOS is inside your "etc."
- **The fix is contained.** StructureMap appears in **5 files** in all of LibLCM, one of which is the
  NVelocity template generating a sixth. Swapping to `Microsoft.Extensions.DependencyInjection` is a
  bounded change, not surgery on 360k lines.

**Cost and blast radius, honestly:** ~360k LOC, ~44% generated from the XMI model, and ~120 consumer
projects in FieldWorks alone. Changes must stay backward-compatible with FieldWorks. You do not own
the repo. Against that: `GrammarJsonServices.ExportGrammar`
(`liblcm/src/SIL.LCModel/DomainServices/GrammarJsonServices.cs:40-72`) is a recent, additive, modern
API that landed without touching the DI container or the generated model — evidence that upstream has
appetite for exactly this shape of change.

> *Correction to path 3's report:* it claimed Motif depends on `GrammarJsonServices` today. It does
> not — the only occurrences of that symbol in Motif are inside the report itself. The precedent is
> real; the dependency was invented.

**Critically: API modernization and portability are separable.** They are routinely conflated as one
scary project. They are two, and portability can ship first, alone.

## What I would actually do

**These paths are not mutually exclusive, and the honest answer uses two of them at different layers.**

1. **Do path 3's portability work, and only the portability work.** It is the only path that closes
   the real gap, it is far smaller than assumed (two blockers, both contained), and **every consumer
   benefits at once** — MiniLcm's FwData backend, Motif, Flexicon, FieldWorks. This is the highest
   leverage per unit of work available anywhere in this analysis. Do not bundle "a good modern API"
   into it; that is a separate project and bundling them is how it dies.
2. **Take path 2's contract, not path 2's claim to be the whole surface.** The change set is the right
   universal vocabulary for "here is a proposed change, here is its effect, approve it." Let MiniLcm
   keep interactive CRUD, where it is already good and already shipping.
3. **Do not build path 1's grammar-in-CRDT.** Three of four grammar ordering mechanisms cannot be
   represented, and the git-history cost floor is prohibitive even for the easy constructs.

**Sequence matters.** Path 3's portability work is a prerequisite that makes both other paths' Android
stories real; both other paths are stuck behind it. It is also the one piece that needs an upstream
owner and therefore the longest lead time. Start it first, and start the conversation with whoever
owns `liblcm` this week.

## The two decisions that are yours, not mine

1. **Does grammar need to be *edited* on a phone, or only *reviewed and synced* there?** These have
   wildly different costs on every path. If review-and-sync is enough, a phone never needs to decide
   string identity, the normalization divergence mostly stops mattering, and grammar can ride to
   mobile as a synced reviewable object without porting phonology anywhere.
2. **Is iOS inside "etc."?** It is the only place StructureMap is a hard gate rather than a soft one,
   and it changes whether path 3's DI swap is required work or optional cleanup.

## Confidence

**High** on the platform findings — Linux support and the mobile-only gap were each verified from
multiple independent sources, and the ICU fallback path was read directly. **Medium-high** on the cost
figures for path 1 (measured from real commits, but extrapolated from the two easiest constructs).
**Medium** on path 3's total cost, which depends on upstream ownership and appetite that no amount of
source reading can settle. **Lowest** on the Android verdict itself: no one has tried, so "unproven,
not blocked" is the honest ceiling. A one-week spike — build `SIL.LCModel` for `net10.0-android`, try
to open a `.fwdata` — would convert the largest remaining unknown in this entire analysis into a fact,
and should precede any commitment.
