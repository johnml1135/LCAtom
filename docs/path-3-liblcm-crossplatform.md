# Path 3 — Fix LibLCM itself: make it genuinely cross-platform and give it a modern API

*Researched 2026-07-27. Scope: `liblcm` as the primary subject, with `libpalaso`,
`languageforge-lexbox`, and `FieldWorks` read for corroborating/contradicting evidence. One of
three candidate paths to a single cross-platform API over FieldWorks language data; the other two
are covered elsewhere. This document does not cover MiniLcm-vs-Motif API unification — see
`docs/one-api-problem.md` and `docs/minilcm-evaluation.md` for that, separate, question.*

## Bottom line, up front

**Linux is not a hypothesis to test — it is already true, in production, verified two independent
ways.** LibLCM's own CI runs its full 2,609-test suite on `ubuntu-22.04` with the same `dotnet
test` invocation as Windows, and a real downstream consumer (`FwDataMiniLcmBridge`, which wraps
`LcmCache` and does real reads/writes/deletes) is built and tested on `ubuntu-latest` in a
different repository's CI, and ships in a Linux Docker container in production
(`languageforge-lexbox/backend/FwHeadless`). The folklore that "LibLCM is Windows-only" is false
today, not just weak.

**Android is genuinely unproven — but the evidence does not show it blocked either.** Nobody has
tried it: there is no Android target anywhere in `liblcm`, and `FwLiteMaui.csproj` excludes
`FwDataMiniLcmBridge` from every non-Windows MAUI target (Android included) by a blanket
`IncludeFwDataBridge` gate with no comment explaining why, and no failed-attempt evidence either.
LibLCM's own code contains no `Reflection.Emit`/`DynamicMethod`/`AssemblyBuilder` (confirmed by
inspection, not just absence-of-grep-hit). But **one of its dependencies does**: `structuremap.patched`
4.7.3, the dependency-injection container LibLCM uses to wire up `LcmCache`, contains real IL
referencing `System.Reflection.Emit`, `System.Reflection.Emit.Lightweight` (`DynamicMethod`), and
`System.Linq.Expressions` (`Expression.Compile`), verified directly from the cached NuGet DLL, with
no `IsDynamicCodeSupported`-style fallback guard found. That is a real risk for iOS-grade full-AOT,
and an unquantified risk for Android trimming/AOT specifically — genuinely open, not resolved by
anything in this repository set.

## Method note: verified vs. inferred

Every claim below is tagged. **VERIFIED-IN-SOURCE** means I read the file/line myself in this
session, or ran a command against a real artifact (a workflow file, a cached DLL, a test count).
**INFERRED** means reasoned from verified facts plus general knowledge of .NET/Android/iOS
runtime behavior, not read directly. Where a sibling document (`one-api-problem.md`) makes a claim
I also independently verified, both citations are given.

---

## 1. Does LibLCM actually work on Linux today?

### 1a. LibLCM's own CI

**VERIFIED-IN-SOURCE**, `liblcm/.github/workflows/ci-cd.yml`:
- Matrix is `[windows-latest, ubuntu-22.04]` (`:24`).
- On Ubuntu, CI installs `mono-devel` (`:41-43`) and a bespoke SIL ICU package, `icu-fw`, from
  `linux.lsdev.sil.org` (`:44-48`) — **not** stock distro ICU.
- Build step (`:70-71`) runs on both OSes unconditionally.
- **Tests actually execute on Linux**, not just build: `:73-77` runs
  `dotnet test --no-restore --no-build ... --configuration Release --logger:"trx;..."` gated
  `if: matrix.os == 'ubuntu-22.04'`, with a separate near-identical step for Windows (`:79-81`).
  Both legs upload TRX results (`:82-87`) that feed a shared `publish-test-results` job (`:109-125`)
  that fails the build (`action_fail_on_inconclusive: true`, `:125`) if either leg's tests don't
  report cleanly.
- The NuGet packages that FieldWorks and everyone else actually consumes are **published from the
  Ubuntu leg**, not Windows: `if: github.event_name == 'push' && matrix.os == 'ubuntu-22.04'`
  (`:92-94`).
- The repo's own `environ` file (`liblcm/environ:1-9`) sets `MONO_PREFIX`, `LD_LIBRARY_PATH`
  including `/usr/lib/fieldworks/lib`, and `PATH` including `/usr/lib/fieldworks/icu-bin` — this is
  sourced before `dotnet test` on the Linux CI leg (`ci-cd.yml:75`), confirming the test run
  genuinely depends on, and gets, the native ICU library.
- `README.md:43-45,67-82` documents `build.sh` and a Linux/Mono test-running procedure as a
  first-class, maintained path, not an afterthought.

This is not new or accidental: `git log --oneline` on `liblcm` shows deliberate, fairly recent
work to get this matrix solid — `c3b1d644 run unit tests in CI against dotnet 8 (#306)`,
`73e1f66c Ensure tests run on ci (#305)`, `46255131 Build on Ubuntu 22.04 until new icu-fw packages
are available (#320)` — alongside 1,648 total commits and a most-recent commit dated 2026-07-17
(**VERIFIED-IN-SOURCE**, `git log -1 --format=%ad`), i.e. ten days before this research, so the
repository is actively maintained, not dormant.

### 1b. How much of the test suite is actually Windows-gated?

**VERIFIED-IN-SOURCE**, counted directly:

| Metric | Count |
| --- | --- |
| Total `[Test]` methods | 2,609 |
| Total `[TestCase(...)]` rows | 253 |
| `[Ignore(...)]` attributes | 23 |
| `[Platform(...)]` attributes | 35 |

Of the 23 `[Ignore]`s, **zero** are platform-related — they are all "not implemented yet,"
"expensive," "by design non-deterministic," or "broken pending a fix" (e.g.
`ScriptureSideEffectsTests.cs:198`, `ArrayPtrTests.cs:45`, `MergeObjectsTests.cs:130`). The one
exception that is Linux-adjacent, `CustomIcuFallbackTests.cs:19-24`, reads:

```csharp
[Platform(Exclude = "Linux",
    Reason = "These tests require ICU4C installed from NuGet packages which isn't available on Linux")]
```

This excludes tests of the **Windows NuGet-packaged** ICU4C distribution mechanism specifically —
it is not a claim that ICU/normalization is broken on Linux. Linux gets its ICU a different way
(the `icu-fw` apt package, `ci-cd.yml:44-48`), and that path is exercised by every other test that
touches `CustomIcu`, which is most of the text-processing suite.

Of the 35 `[Platform(...)]` attributes, essentially all of them (in `FileUtilsTest.cs`) are **paired
Windows/Linux behavioral assertions** — e.g. `[Platform(Include = "Win")]
IsFilePathValid_Windows()` immediately followed by `[Platform(Include = "Linux")]
IsFilePathValid_Linux()` (`FileUtilsTest.cs:84,100`), and similarly for
`ActualFilePath_...DifferentCase_{Windows,Linux}` (`:288,302,317,331,346,360`) and a dozen
`ChangeWindowsPathIfLinux_.../ChangeLinuxPathIfWindows_...` pairs (`:1345-1524`). These are tests
*of* path-semantics differences (Windows case-insensitivity vs. Linux case-sensitivity, drive
letters vs. POSIX paths) — evidence the two platforms are each tested on their own terms, not
evidence Linux is second-class.

**Net: under 1% of the suite (23 Ignore + ~1 genuinely platform-caused exclusion, out of 2,609
tests) reflects anything not working on a given OS, and that one exclusion is about a Windows-only
NuGet artifact, not a Linux failure.**

### 1c. Does a real consumer load a real project and run, not just build?

**VERIFIED-IN-SOURCE**, `languageforge-lexbox`:
- `backend/FwLite/FwDataMiniLcmBridge/FwDataMiniLcmBridge.csproj:9` references
  `PackageReference Include="SIL.LCModel"` unconditionally — no OS condition.
- `backend/FwLite/FwDataMiniLcmBridge.Tests/` contains 20+ test files exercising real `MiniLcm`
  operations against a real `LcmCache` — `MiniLcmTests/CreateEntryTests.cs`,
  `UpdateEntryTests.cs`, `QueryEntryTests.cs`, `SortingTests.cs`, `HomographNumberTests.cs`, etc.
  `Fixtures/FwDataTestsKernel.cs:8-24` wires up `AddFwDataBridge()` (the real bridge, not a fake)
  for these tests.
- `languageforge-lexbox/.github/workflows/fw-lite.yaml:44` runs a job on `runs-on: ubuntu-latest`
  that (`:76-77`) does `dotnet build FwLiteCore.slnf` and (`:95`) `dotnet test FwLiteCore.slnf
  ...`. `FwLiteCore.slnf` (`languageforge-lexbox/FwLiteCore.slnf:4-15`) explicitly includes
  `FwDataMiniLcmBridge.Tests.csproj` and `FwDataMiniLcmBridge.csproj`.
- The repo's own CI documentation states this plainly: `.github/AGENTS.md:84`, "Core .NET
  build/tests run on Linux (`FwLiteCore.slnf`); MAUI build/tests on Windows only" — i.e. the
  LibLCM-backed layer runs on Linux; only the MAUI *shell* is Windows-gated, and (separately) that
  gate is about the .NET MAUI Windows SDK requirement, not LibLCM (`AGENTS.md:236-238`, ".NET MAUI
  Windows builds require the Windows SDK. Can't build Windows MAUI targets on Linux.").
- In production, `backend/FwHeadless/FwHeadless.csproj` references
  `../FwLite/FwDataMiniLcmBridge/FwDataMiniLcmBridge.csproj` with **no OS condition**
  (**VERIFIED-IN-SOURCE**, confirmed directly, corroborating `one-api-problem.md:39-41`), and its
  Dockerfile's base image is `FROM mcr.microsoft.com/dotnet/aspnet:10.0` (**VERIFIED-IN-SOURCE**,
  `backend/FwHeadless/Dockerfile:2`) — Microsoft's default ASP.NET runtime image, which is Linux
  unless a `nanoserver`/`windowsservercore` tag is explicitly used (none is here). This is a
  LibLCM-backed service handling real Send/Receive project processing, in a Linux container, today.

This is a stronger claim than "the library builds on Linux" — it is "a real consumer opens real
`.fwdata` projects, does CRUD through `LcmCache`, and the whole stack runs in a Linux container in
production."

### 1d. What is genuinely Windows-flavored, and where does it sit?

Three small, isolated pockets, none on the load/save/CRUD path:

1. **`ImportFrom6_0.cs`** — the *only* file using `System.ServiceProcess.ServiceController`
   (`liblcm/src/SIL.LCModel/DomainServices/DataMigration/ImportFrom6_0.cs:11,240-247`) and 2 of the
   3 `Registry.*` call sites (`:232,276`, `Registry.LocalMachine`/`Registry.ClassesRoot`). Its own
   doc comment: "Handles import of FW 6.0X data from a zip file containing an XML backup"
   (`:19-22`) — this is one-time legacy migration code for FieldWorks 6.0's old SQL-Server-backed
   format (`MSSQL$SILFW` service, `:240`), a format nobody creates anymore. It is peripheral by
   construction, not core.
2. **`RegistryHelper.cs`** (`liblcm/src/SIL.LCModel.Utils/RegistryHelper.cs`) — the 3rd Registry
   site. Pure `Microsoft.Win32.Registry` wrapper for user settings (company/product key
   conventions). Not called from `XMLBackendProvider`, `LcmCache`, or any load/save path found in
   this session (only from `ImportFrom6_0.cs` per the grep above and its own definition).
3. **`SpellingHelper.GetShortPathName`** (`liblcm/src/SIL.LCModel.Core/SpellChecking/SpellingHelper.cs:320-334`)
   — the sole `kernel32.dll` `DllImport`. Already guarded: `if (Platform.IsWindows) { ... }  return
   input;` (`:325-333`) — a graceful no-op fallback on other OSes, already shipping.

None of these three sit on the path that loads or writes a `.fwdata` project.

---

## 2. What are the real blockers, ranked — and Linux vs. Android split sharply

| Candidate | Linux verdict | Android verdict | Evidence |
| --- | --- | --- | --- |
| **Registry / `ServiceController`** | Non-issue | Non-issue (same code path never runs) | Confined to `ImportFrom6_0.cs`, one-time FW6 SQL-Server migration; peripheral (§1d). Trivial to `#if`/strip without touching core. |
| **`SpellingHelper` `kernel32.dll`** | Non-issue, already guarded | Non-issue, already guarded | `Platform.IsWindows` check with fallback already in place (`SpellingHelper.cs:325`). |
| **File locking (`SIL.IO.FileLock`)** | Non-issue, already solved | Likely non-issue | `libpalaso/SIL.Core/IO/FileLock/SimpleFileLock.cs` is a portable PID-based lock file — no native/Win32 API. It already special-cases Mono-on-Linux process detection (`:110-122`, checking for a `mono`/`mono-` process name and matching `.exe` module name) — this is **existing, shipped Linux-awareness**, not a gap. Lives in `SIL.Core` (netstandard2.0), not `SIL.Core.Desktop`. Used from LibLCM's core path at `XMLBackendProvider.cs:13` (`using SIL.IO.FileLock`). Android risk is about the storage sandbox (can you even get a stable writable path for the lock file), not the locking logic itself — untested here. |
| **`CustomIcu.cs` / native ICU** | Solved via `icu-fw` apt package; already CI-tested | **Real, unresolved work item** | `CustomIcu.cs:226-247` already has a soft-fail path: `SilIcuInit` P/Invoke wrapped in `try/catch (DllNotFoundException)`, falling back to non-SIL-customized ICU normalization tables (`nfc`/`nfkc` instead of `nfc_fw`/`nfkc_fw`) if the native lib isn't found (`:228-233,409-429`). On Linux this SIL-customized native lib is provided by installing `icu-fw` (`ci-cd.yml:44-48`) — a bespoke SIL package hosted at `linux.lsdev.sil.org`, not stock distro ICU. **Android has no equivalent package and no NDK build of it was found anywhere in this repo set** — this is a real, unquantified engineering task: cross-compile or source a `libicuuc70`-equivalent for Android ABIs, or accept the graceful-degradation fallback (works, but loses SIL-specific normalization behavior). |
| **`structuremap.patched` (DI container)** | Non-issue — proven in production (FwHeadless, a Linux container, uses it via `LcmServiceLocatorFactory`) | **Genuinely unresolved, concrete risk found** | LibLCM's own registration code is hand-written (`registry.For<T>().Use<T>()`, `LcmServiceLocatorFactory.cs:72-244`) — **not** assembly scanning, so the usual "trimmer can't see what Scan() needs" failure mode is largely avoided. But the *container itself* is not clean: `strings` on the cached `structuremap.patched/4.7.3/lib/netstandard2.0/StructureMap.dll` shows real references to `System.Reflection.Emit`, `System.Reflection.Emit.Lightweight` (`DynamicMethod`), and `System.Linq.Expressions`, including a method named `GetDynamicMethod` — verified directly, not inferred — with no `IsDynamicCodeSupported`/fallback guard found. This is real IL-emit-at-runtime for fast object construction, a known pattern in that generation of DI containers. **Confirmed non-blocking under Android's default JIT/interpreter execution mode** (Android does not forbid JIT the way iOS does) — **INFERRED**, general .NET-for-Android knowledge, not tested here. **Unverified under Android's optional aggressive-trimming/NativeAOT publish modes**, where it could be a hard failure. |
| **`protobuf-net`** | Non-issue | Soft, low-priority | Confined to `SharedXMLBackendProvider.cs`, `CommitLogMetadata.cs`, `CommitLogRecord.cs` — the multi-user "shared XML" backend (`BackendProviderType.kSharedXML`), one of three backend types (`LcmServiceLocatorFactory.cs:141-163`). `FwDataMiniLcmBridge` (the actual mobile/cross-platform consumer in evidence) uses `.fwdata` files, i.e. the plain `kXML` backend — `SharedXMLBackendProvider` is not obviously on that path. protobuf-net v2's default runtime model can also use `Reflection.Emit`, same caveat as StructureMap — **INFERRED**, not independently verified from the cached DLL in this session. |
| **`SIL.Core.Desktop`** | Non-issue | Needs checking, likely non-issue | LibLCM's *only* verified imports from the `SIL.Core.Desktop` package's namespace surface are `using SIL.IO.FileLock` (`XMLBackendProvider.cs:13`, portable, see above) and `using SIL.Reporting` (13 files, e.g. `CmObject.cs:30`, `UnitOfWorkService.cs:15`) — but `SIL.IO.FileLock` is actually defined in `SIL.Core` (`libpalaso/SIL.Core/IO/FileLock/*.cs`), not `SIL.Core.Desktop`, despite the package reference. **Could not fully verify** which specific `SIL.Reporting` members are called or whether they pull in the `SIL.Core.Desktop` package's Windows-only bits (that package also ships `SIL.UsbDrive.Windows`/`SIL.UsbDrive.Linux` split namespaces — `libpalaso` namespace scan shows `SIL.Core.Desktop` already contains **both** Windows and Linux implementations for at least USB-drive detection, so "Desktop" in that package's name means "not mobile," not "Windows-only"). This warrants a follow-up grep pass this session did not complete. |
| **Spell checking (Hunspell/`WeCantSpell.Hunspell`)** | Non-issue | Untested, likely fine | `WeCantSpell.Hunspell` is a managed reimplementation (no native P/Invoke to real Hunspell found in `SIL.LCModel.Core.csproj`'s reference list). Peripheral to core CRUD. |
| **The `.fwdata` XML model itself** | Non-issue | Non-issue | Plain XML on disk (`XMLBackendProvider.cs`); no platform coupling in the format. Case-sensitivity handling between Windows and Linux paths is explicitly tested (`FileUtilsTest.cs`, §1b). |

**Ranked summary:** the only two items with a real, *unresolved* engineering cost are **(1) a
proper Android-native ICU distribution** (a packaging/build problem, not a code problem — the
graceful-fallback code path already exists) and **(2) `structuremap.patched`'s runtime IL
generation under Android's optional aggressive-trim/NativeAOT modes** (an unknowns-until-tested
problem, and one with an obvious mitigation: swap the DI container, since LibLCM's own
registration code is already hand-written rather than reflection-scanned, so the swap is
mechanical, not architectural). Everything else on the original candidate list is confirmed
peripheral, already-guarded, or already-solved.

---

## 3. Which blockers are actually in libpalaso, not liblcm?

**None of the real ones.** Specifically:

- **`SIL.WritingSystems.csproj`** (**VERIFIED-IN-SOURCE**,
  `libpalaso/SIL.WritingSystems/SIL.WritingSystems.csproj`) multi-targets
  `$(TargetFrameworks);netstandard2.0` (`:5`) with a portable package set (`icu.net`, `Spart`,
  `Markdig.Signed`, `System.Memory`) and **no** `SIL.Core.Desktop` reference. Clean.
- **`SIL.Core.Desktop.csproj`** (`libpalaso/SIL.Core.Desktop/SIL.Core.Desktop.csproj`) does
  multi-target `$(TargetFrameworks);netstandard2.0` too (`:8`), and conditionally references
  `NDesk.DBus` only under `NETFRAMEWORK` (`:19`) — i.e. it already avoids pulling Linux D-Bus
  bindings into the netstandard2.0/net8.0 builds. As noted in §2, the actual LibLCM-facing surface
  of this package (`FileLock`) turns out to live in `SIL.Core` proper, not `Desktop`, and the
  `Desktop` package itself contains parallel Windows/Linux implementations for the one namespace
  inspected (`SIL.UsbDrive.{Windows,Linux}`), so the name is not evidence of Windows-only design.
- **The one place `SIL.Core.Desktop` is unconditionally required by a downstream MAUI consumer**
  is `Microsoft.ICU.ICU4C.Runtime`, and even that consumer already conditions it out on non-Windows:
  `languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/FwDataMiniLcmBridge.csproj:9`:
  `<PackageReference Include="Microsoft.ICU.ICU4C.Runtime" Condition="$([MSBuild]::IsOsPlatform('Windows'))" />`
  — this is the consumer, not LibLCM itself, already doing the right thing, and it is further
  evidence that the ICU distribution mechanism (not ICU support itself) is the only real moving
  part.

**Conclusion: the portability work, to the extent it exists, is inside `liblcm` (specifically its
`structuremap.patched` dependency and its Android-ICU distribution story), not hiding in
`libpalaso`.** `libpalaso`'s relevant pieces (`SIL.Core`, `SIL.WritingSystems`) are already clean;
`SIL.Core.Desktop`'s footprint in LibLCM is smaller and more portable than its name suggests, though
one thread (`SIL.Reporting`'s exact call sites) was not fully run to ground this session.

---

## 4. What would "a good modern API" look like, and does it conflict with portability?

**They are separable, and there is already a working precedent for adding new API surface without
touching portability.**

**VERIFIED-IN-SOURCE**: LibLCM's `README.md:11-20` documents a *recently added*, purely additive
feature — **LCM Grammar JSON** — "a deterministic, GUID-keyed JSON projection of the
parser-relevant subset of a project ... for external morphological-parser tooling," exported via a
single static call, `SIL.LCModel.DomainServices.GrammarJsonServices.ExportGrammar(cache, writer)`
(`:16`), with a spec and JSON Schema checked into the repo (`doc/lcm-grammar.md`,
`doc/lcm-grammar.schema.json`) and enforced by unit tests (`:19-20`). This is exactly the shape of
"modern facade over the old model" work, shipped upstream, independent of any portability change,
and this session's LibLCM checkout is what Motif itself already depends on for grammar JSON —
i.e. this is not speculative, it is in production use by a sibling project today.

**Characterizing the existing API (VERIFIED-IN-SOURCE, from direct reading):** entry points like
`LcmCache.CreateCacheFromLocalProjectFile(string projectPath, string userWsIcuLocale, ILcmUI ui,
ILcmDirectories dirs, LcmSettings settings, IThreadedProgress progressDlg)`
(`liblcm/src/SIL.LCModel/LcmCache.cs:156-163`) require implementing four collaborator interfaces
(`ILcmUI`, `ILcmDirectories`, `LcmSettings`, `IThreadedProgress`) just to open a project headlessly
— real ceremony, though workable (both `FwDataMiniLcmBridge` and, per `minilcm-evaluation.md:290-293`,
Motif itself have already built headless adapters for it — "Motif already copy-adapted ~1,000-1,200
lines of MiniLcm's project-load plumbing"). The DI wiring (`LcmServiceLocatorFactory.cs:72-244`) is
StructureMap 4.x's fluent-registry style, itself a decade-old pattern by current .NET idiom. The
object model is XMI/UML-generated (`GeneratedClasses.cs`, `GeneratedInterfaces.cs`, etc.,
`SIL.LCModel.csproj:99-108`) with `ICmObjectRepository`/`IFwMetaDataCacheManaged`-flavored naming
throughout.

**Are they separable?** Yes, on the evidence:
1. Portability work (structuremap swap-out, Android ICU packaging) touches the **DI wiring and
   native-library loading layers** — `LcmServiceLocatorFactory.cs`, `CustomIcu.cs`.
2. API modernization (a fluent/async facade, better ergonomics for headless consumers) would touch
   **a new, additive layer on top of `LcmCache`/`ICmObjectRepository`** — exactly the pattern
   `GrammarJsonServices.ExportGrammar` already demonstrates as viable without touching the
   generated model or the DI container.
3. They could ship on **independent timelines**: portability first (smaller, more mechanical:
   dependency swap + native packaging), API modernization second (larger, more design-heavy,
   already has upstream precedent and demonstrated appetite).

Nothing found in this session suggests these two projects are entangled. The main risk to sequencing
them is organizational (who reviews/merges/owns the roadmap, see §5), not technical.

---

## 5. What is the honest cost?

**Size (VERIFIED-IN-SOURCE, counted directly):**

| | Files | LOC |
| --- | --- | --- |
| `src/` (all of LibLCM's production code) | 573 `.cs` | 360,686 |
| ...of which XMI-generated (`Generated*.cs`, `*.vm.cs`) | — | 157,474 (~44%) |
| ...hand-maintained | — | ~203,000 |
| `tests/` | 299 `.cs` | 101,970 |

This is a large, mature codebase, almost half of it mechanically generated from
`MasterLCModel.xml` via the `GenerateModel` MSBuild target (`SIL.LCModel.csproj:99-108`) — i.e.
adding a *field* to the model is a generator-driven, low-risk change; changing the *DI container*
or *native library loading* touches hand-maintained infrastructure code directly, a much smaller
and more tractable surface than the LOC total suggests.

**Consumers / blast radius (VERIFIED-IN-SOURCE):**
- `grep -rl "SIL.LCModel\b" --include=*.csproj` in the `FieldWorks` repo returns **120 project
  files** — the historical, large, Windows-native desktop application. FieldWorks' own `ReadMe.md`
  (`FieldWorks/ReadMe.md:13-19`) now states plainly: **"Builds, tests, installer work, and
  developer-environment setup are Windows-only and are intentionally disabled on non-Windows
  hosts"** — FieldWorks-the-application has *dropped* Linux support at the tooling level (the
  historical Wasta/Ubuntu-packaged FieldWorks relied on Mono plus a native C++/COM/ATL Views engine
  — `FieldWorks/Src/views`, 8 `.vcxproj` files found — that is a *different*, and now abandoned,
  compatibility layer from LibLCM, which was split out as an independently-portable library). Its
  `.github/workflows/CI.yml` matrix confirms: the only `ubuntu-latest` job (`:174`) is
  `publish_test_results`, a downstream artifact-processing step, not a build/test leg (`:21` is the
  sole `windows-2022` build/test job). **So FieldWorks itself would not directly benefit from or
  need to validate LibLCM portability changes for its own build** — but any LibLCM API/behavior
  change must stay backward-compatible for FieldWorks' 120 consuming project files regardless,
  since FieldWorks pins a `SIL.LCModel` NuGet version and is the reason a from-scratch rewrite
  (Path 1, not this document's path) is expensive.
- `languageforge-lexbox` depends on it via `FwDataMiniLcmBridge` (one package reference) and
  `FwHeadless` (transitively) — a much smaller, actively co-evolving surface, already exercising
  Linux support in CI and production (§1c).

**Ownership:** `liblcm` is a `sillsdev` GitHub org repo, MIT/LGPL-licensed
(`README.md`/package metadata; `LGPL, version 2.1 or later` headers throughout, e.g.
`RegistryHelper.cs:2-3`), published to public NuGet from CI (`ci-cd.yml:92-94`) — i.e. it is not a
single company's private fork; changes go through the same review process as any other SIL open-source
contribution, and the org already accepts and merges CI/portability-flavored PRs regularly
(the commit history in §1a).

**Other known defects (Motif `docs/issues.md`, C-series "upstream hazards we must defend
against"):** these are correctness/semantics defects in LibLCM's data-migration and grammar-load
paths, **orthogonal to portability** but relevant to "what does a consumer building a good API on
top of LibLCM still have to guard against": `C1` (`AddCustomField` inside an open unit of work
corrupts the project — lost 1,392 senses in Flexicon), `C2` (single-writer is not enforced by
LibLCM itself), `C15` (headword/homograph caches have no bulk-invalidation hook and a poisoned
cache instance must be discarded, not repaired), `C3` (`ReferringObjects` has a first-touch
whole-project cost), `C4`/`C6` (reference/MPR-integrity gaps that crash real projects, e.g.
`GenerateHCConfig.exe` on the Amharic project), `C5` (a hard 24-alpha-variable ceiling that throws
and kills a grammar load), `C7`-`C14` (assorted silent-data-loss and dead-field hazards). None of
these are cross-platform issues — they would need defending against on Windows too — but a "good
modern API" that markets itself as safer than the raw `LcmCache` surface would need to wrap or
validate around all of them, which is real, separately-sized work (see `issues.md` for the current
"detect, disclose, or refuse" mitigation posture Motif has adopted for each).

**Net honest cost:** portability (Linux: ~done; Android: DI-container swap + native-ICU packaging,
both bounded, neither touching the 157k generated LOC) is a **small, mechanical** project relative
to the codebase's total size. A "good modern API" layer is a **larger, open-ended, design-heavy**
project that already has upstream appetite and precedent (`GrammarJsonServices`) but no committed
scope. Defending against the C-series correctness hazards is a **third, separate** body of work
that any serious API layer over LibLCM — on any platform — has to do regardless of this document's
question.

---

## 6. What does this path get right that the others don't?

If Linux is already solved and Android turns out to be the bounded problem the evidence in §2
suggests (container swap + ICU packaging, not a fundamental rewrite), this path has a property
neither of the other two candidate paths can claim by construction: **it is not a second store.**

`one-api-problem.md:65-93` and `minilcm-evaluation.md` (both read this session, though their
subject is the MiniLcm/Motif API question, not this one) independently document the cost that a
second, non-LibLCM store already pays today: `FwLiteProjectSync/CrdtFwdataProjectSyncService.cs`
maintains **seven hand-written, bidirectional reconcilers** (`WritingSystemSync`, `PublicationSync`,
`PartOfSpeechSync`, `SemanticDomainSync`, `ComplexFormTypeSync`, `MorphTypeSync`, `EntrySync`) just
to keep a SQLite/CRDT replica consistent with `.fwdata` for the **lexical-only** subset LibLCM
already models — and referential integrity has to be independently re-implemented per-type for the
CRDT store (`MiniLcm/Models/IObjectWithId.cs:19-29`) because it has no LibLCM underneath to inherit
`CmObject.Delete()`'s cascade from (`liblcm/src/SIL.LCModel/DomainImpl/CmObject.cs:1728-1733`,
`Vectors.cs:782-785,1836`, verified in this and the sibling session). If LibLCM itself runs
everywhere, none of that reconciliation exists to write, and grammar — 230 of 473 in-scope fields,
per the coverage manifest cited in `minilcm-evaluation.md:26-28` — is reachable on every platform
by construction, because it is LibLCM's native model, not a second model that would need to grow to
match it.

That is the case this path gets to make and the others structurally cannot: **one data engine, one
set of referential-integrity guarantees, one grammar model, everywhere** — rather than "one engine
plus a permanently-behind lexical-only replica requiring hand-written sync for every new
construct." The cost of that property, per §5, is bounded and mostly already paid on Linux; what
remains genuinely unpriced is Android, and specifically whether `structuremap.patched`'s
`Reflection.Emit` usage survives Android's trimming/AOT publish modes in practice — which nobody in
this evidence set has tested.

---

## Verdict

**The premise that LibLCM is Windows-locked is false for Linux (already shipping in production,
independently verified twice) and unresolved-but-not-contradicted for Android** — the one concrete
technical risk found, `structuremap.patched`'s runtime `Reflection.Emit`/`Expression.Compile` usage
verified directly in its compiled IL, has an obvious mitigation (LibLCM's own DI registration is
already hand-written, not reflection-scanned, so swapping the container is mechanical) and has never
been tested against Android's trim/AOT modes by anyone in this evidence set. The remaining candidate
blockers — Registry, `ServiceController`, spell-check `kernel32.dll`, file locking, `protobuf-net`,
`SIL.Core.Desktop` — are each confirmed peripheral, already-guarded, or already-solved, and none sit
on the `.fwdata` load/save/CRUD path. A modern API layer is separable from the portability work and
has a working, shipped precedent (`GrammarJsonServices.ExportGrammar`) demonstrating that new
surface can be added without touching the generated model or the DI container. The honest cost is a
large but mostly-generated codebase (203k hand-maintained LOC out of 360k), a 120-project blast
radius in FieldWorks that constrains backward compatibility but does not block portability work
(FieldWorks itself has *dropped* non-Windows builds, so it is not a co-testing burden), and a
separate, already-documented body of correctness hazards (issues.md C-series) that any API layer
over LibLCM must defend against regardless of platform.

**Confidence: high on Linux (multiple independent, verified, production-grade evidence sources);
medium on Android (one real risk identified and characterized precisely, but genuinely untested by
anyone in this evidence set — the honest answer is "probably tractable, not proven"); medium-high
on the separability of portability from API modernization (one strong shipped precedent, no
contrary evidence found).**

## What I could not verify

- **Whether `structuremap.patched`'s `Reflection.Emit` usage actually fires at runtime for
  LibLCM's specific registration graph**, or is dead/rarely-hit code for this particular usage
  pattern. I verified the DLL *contains* the IL; I did not instrument a running `LcmCache`
  construction to confirm the dynamic-method path is hit in practice, on any platform.
- **Whether that usage would actually fail under Android's default JIT/interpreter execution
  mode**, vs. only under an explicit NativeAOT/full-trim publish profile. This is asserted as
  low-risk based on general knowledge of Android's default (non-full-AOT) execution model, not
  verified by building and running an actual `net8.0-android`/`net10.0-android` target against
  LibLCM — no such target exists anywhere in the evidence set to test.
- **Whether Android has (or could get) a usable native ICU build compatible with `CustomIcu.cs`'s
  `SilIcuInit` P/Invoke** (`icuuc70`-equivalent for `android-arm64`/`android-x86` etc.), or whether
  the codebase would have to permanently accept the existing DllNotFoundException fallback path
  (functional, but loses SIL's custom normalization tables) on that platform. No such build was
  found referenced anywhere in `liblcm`, `libpalaso`, or `languageforge-lexbox`.
- **The exact members of `SIL.Reporting` that LibLCM's 13 importing files actually call**, and
  whether any of them reach into Windows-only code inside `SIL.Core.Desktop`. I confirmed the
  `using` statements and confirmed `SIL.IO.FileLock` (the only other Desktop-namespaced import
  found) is actually defined in the portable `SIL.Core` package, but did not trace `SIL.Reporting`'s
  call graph to the same depth.
- **`protobuf-net`'s actual runtime codegen behavior in this version (2.4.6)** — flagged as a
  same-shape risk as `structuremap.patched` by general knowledge of protobuf-net v2's architecture,
  but not independently confirmed against the cached package DLL the way `structuremap.patched` was.
- **Whether `FwLiteMaui.csproj`'s `IncludeFwDataBridge` Android exclusion reflects a known failed
  attempt** (undocumented in-repo) **or simply "nobody has tried yet."** No commit message, comment,
  or issue reference explaining the exclusion's rationale was found in `FwLiteMaui.csproj` itself;
  I did not search `languageforge-lexbox`'s issue tracker or git blame history for this line, which
  could resolve this directly.
- **I did not compile or run any part of LibLCM myself in this session** (the ground-truth CI
  evidence was read from workflow files and cross-checked against a real downstream consumer's CI
  and Dockerfile, which I judged sufficient and lower-risk than attempting a fresh build of a
  573-file, XMI-generated solution within the research budget) — so all "tests pass on Linux"
  claims rest on reading CI configuration and its enforced pass/fail gating
  (`action_fail_on_inconclusive: true`), not on an execution I personally observed.
