# Inventory: What Runs Locally, Where, With What Footprint

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).

Scope per brief: establish platform truth for FwLite from build files, CI workflows, and code — not
prose. Every claim below is marked **VERIFIED** (read directly from a build file, workflow, or
source line, cited `path:line`) or **INFERRED** (reasoned from verified facts but not itself
directly observed — e.g. because it would require running a build). Repos referenced:

- `languageforge-lexbox` = `C:\Users\johnm\Documents\repos\languageforge-lexbox`
- `liblcm` = `C:\Users\johnm\Documents\repos\liblcm`
- `harmony` = `C:\Users\johnm\Documents\repos\harmony`
- `PanGloss` = `C:\Users\johnm\Documents\repos\PanGloss`

All lexbox-repo paths below are relative to `languageforge-lexbox` unless a full path is given.

---

## 0. Headline corrections to prior (wrong) claims

- **"LibLCM is Windows-only" — FALSE.** `liblcm\src\SIL.LCModel\SIL.LCModel.csproj:4` and
  `liblcm\src\SIL.LCModel.Utils\SIL.LCModel.Utils.csproj:4` both target
  `netstandard2.0;net462;net8.0`. `SIL.LCModel.Utils.csproj:13` references `Mono.Unix` for
  Unix P/Invoke, and both csproj files carry a `CheckWinForms` MSBuild target
  (`SIL.LCModel.csproj:113`) that **hard-errors the build** if `System.Windows.Forms` is
  referenced — i.e. the library is deliberately kept UI-framework-free and cross-platform.
  **VERIFIED.**
- **"Linux is unsupported" — FALSE.** `liblcm\.github\workflows\ci-cd.yml:16` runs CI on
  `[windows-latest, ubuntu-22.04]` and installs `mono-devel` + the SIL `icu-fw` apt package
  specifically for the Ubuntu leg (`ci-cd.yml:31-38`). Separately, `languageforge-lexbox`'s own
  `.github\workflows\fw-lite.yaml` builds and **publishes** `FwLiteWeb` for `linux-x64` and
  `linux-arm64` (`fw-lite.yaml:338-342`) and runs an executable smoke test on the Linux binary
  (`fw-lite.yaml:344-357`). **VERIFIED.**
- **macOS is a real target too**, just via a different artifact than Windows. `fw-lite.yaml:287-320`
  (`publish-mac`, `runs-on: macos-latest`) publishes `FwLiteWeb` for `osx-x64` and `osx-arm64`.
  **VERIFIED.** (No macOS runner exists in liblcm's own CI — see §7 below for what that gap does
  and doesn't imply.)
- Net correction: **which artifact** covers which OS is not uniform (Windows ships a native MAUI
  app; Linux/macOS ship a self-hosted local web app instead of a native MAUI build) — that
  distinction, not "some OS is unsupported," is the real nuance the brief asked me to get right.
  See §1.

---

## 1. Per-artifact platform matrix

### 1a. `FwLiteMaui` — native desktop/mobile app

Source: `backend\FwLite\FwLiteMaui\FwLiteMaui.csproj`.

- `TargetFramework` is blanked (`:4`) and replaced with a `TargetFrameworks` list built up
  conditionally:
  - `net10.0-android` — added `Condition=" '$(BuildAndroid)' != 'false' "` (`:5`), i.e. included
    unless explicitly turned off.
  - `net10.0-ios;net10.0-maccatalyst` — added
    `Condition="$([MSBuild]::IsOSPlatform('osx')) And '$(BuildApple)' != 'false'"` (`:6`) — **only
    when the machine doing the build is macOS.**
  - `net10.0-windows10.0.19041.0` — added `Condition="$([MSBuild]::IsOSPlatform('windows'))"`
    (`:7`) — **only when the build machine is Windows.**
- `SelfContained` = `true` unconditionally (`:24`).
- `PublishSingleFile`: `false` in Debug (`:51`); in Release, `false` by default (`:55`, comment:
  "single file disabled as it's less efficient for updates") but flipped back to `true`
  specifically `Condition="... And $(WindowsPackageType) == 'None'"` (`:57-59`) — i.e. only the
  **portable** (non-MSIX) Windows Release build is single-file.
  `EnableWindowsTargeting Condition="$([MSBuild]::IsOSPlatform('linux'))"` = `true` (`:25`) exists
  purely so a Linux dev machine can design-time-evaluate the `windows` TFM in an IDE; it does not
  cause the windows TFM to actually be added to the build (that's gated by `:7` alone).
- **What actually gets built in CI, per `fw-lite.yaml`, is narrower than the csproj's conditionals
  allow:**
  - `publish-win` (`fw-lite.yaml:418-514`, `runs-on: windows-latest`) publishes
    `net10.0-windows10.0.19041.0` twice: once portable (`WindowsPackageType=None`, single-file,
    `:462-467`) and once as an MSIX (`:469-477`), then bundles + Trusted-Signing-signs the MSIX
    (`:479-500`).
  - `publish-android` (`fw-lite.yaml:366-416`, `runs-on: ubuntu-latest`) publishes
    `net10.0-android`, keystore-signed.
  - **There is no `publish-mac`/`publish-ios` job for `FwLiteMaui` at all**, even though the
    csproj's `IsOSPlatform('osx')` condition means an `net10.0-ios`/`net10.0-maccatalyst` build
    would be picked up on a macOS build machine. macOS/iOS coverage is delivered by a *different*
    artifact (`FwLiteWeb`, §1b) — **as shipped today, no native MAUI app exists for macOS or iOS.**
    **VERIFIED** (absence confirmed by reading the full workflow file, `fw-lite.yaml:1-715`).
- `IncludeFwDataBridge` (`FwLiteMaui.csproj:26-28`): `false` by default, flipped `true`
  `Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'"`.
  Only when true does the csproj pull in `FwDataMiniLcmBridge` + `FwLiteProjectSync`
  (`:98-101`) and define `INCLUDE_FWDATA_BRIDGE` (`:28`), which gates
  `FwLiteMauiKernel.cs:58-63` (`#if INCLUDE_FWDATA_BRIDGE` wraps
  `FwDataBridgeKernel.AddFwDataBridge`, `FwLiteProjectSyncKernel.AddFwLiteProjectSync`, and the
  `IFwLinker`/`FwLinker` registration). **Net effect: the Android build of FwLiteMaui has NO
  `.fwdata` support at all — it is CRDT-only.** Only the Windows MAUI build can open `.fwdata`
  projects. **VERIFIED.**

### 1b. `FwLiteWeb` — self-hosted local web app (Kestrel + Blazor, browser front end)

Source: `backend\FwLite\FwLiteWeb\FwLiteWeb.csproj`. `Sdk="Microsoft.NET.Sdk.Web"` (`:1`),
`SelfContained=true` (`:5`), no OS-conditional `TargetFramework` — this project only carries
`net10.0` (from the shared `Directory.Build.props:12` default) and is published per-RID via the
`dotnet publish -r <rid>` CLI flag, not via `TargetFrameworks`.
`ProjectReference` to `FwDataMiniLcmBridge` and `FwLiteProjectSync` is **unconditional**
(`:37-38`) — unlike `FwLiteMaui`, every OS build of `FwLiteWeb` compiles in `.fwdata` support.
- **Linux**: `fw-lite.yaml:322-364` (`publish-linux`, `ubuntu-latest`) — `dotnet publish -r
  linux-x64 ... -p:PublishSingleFile=true` and the same for `linux-arm64` (`:341-342`); the job
  then runs the produced binary for 10s as a smoke test (`:344-357`) and ships
  `install-launcher.sh` / `fwlite.desktop.template` (`FwLiteWeb.csproj:29-30`) for desktop-menu
  integration — see `FwLiteWeb\README-linux.md:1-20`.
- **macOS**: `fw-lite.yaml:287-320` (`publish-mac`, `macos-latest`) — `dotnet publish -r osx-x64`
  and `-r osx-arm64` (`:309,313`), no `PublishSingleFile` flag passed (so it stays multi-file per
  the csproj's Release default, `FwLiteWeb.csproj:12`), comment `#todo sign the app` (`:314`) — **the
  macOS build is explicitly unsigned/unnotarized today.**
- **Windows**: no dedicated `publish-*` job publishes `FwLiteWeb` for Windows in the release
  pipeline (Windows users get `FwLiteMaui` instead) — but nothing in the csproj prevents it;
  `README-sdk.md:3` ("start FwLiteWeb.exe") documents running it on Windows too, evidently for
  dev/SDK use rather than the signed release channel. **INFERRED** that this is a deliberate
  product decision (one native app per platform where a native app is easy, a web app where it
  isn't) rather than a technical limitation.
- macOS-only ICU bundling block, `FwLiteWeb.csproj:43-62`
  (`Condition="$([MSBuild]::IsOsPlatform('macOS'))"`): copies
  `/opt/local/lib/libicu*.??.dylib` (MacPorts path) if present, and
  `/opt/homebrew/Cellar/icu4c/*/lib/libicu*.??.dylib` gated on
  `Condition="Exists('/opt/homebrew/Cellar/icu4c/74.2/lib/')"` (**hardcoded version 74.2** — a
  version-pin footgun: a homebrew icu4c bump past 74.2 silently stops being bundled). The comment
  at `:44-50` states outright: *"We have to bundle the icu4c libs somehow unless macOS starts to
  include them by default."* No equivalent Linux bundling block exists in this csproj — see §2.
  **VERIFIED.**

### 1c. `FwLiteProjectSync` — CRDT ↔ FwData sync engine

Source: `backend\FwLite\FwLiteProjectSync\FwLiteProjectSync.csproj`. `Sdk="Microsoft.NET.Sdk"`,
**no `<OutputType>` element at all** — contrast with `FwLiteMaui.csproj:16`
(`<OutputType>Exe</OutputType>`) and `LcmDebugger.csproj:3` (same), which both set it explicitly.
Per .NET SDK defaults, the absence of `OutputType` means this project builds as a **class
library**, not an executable, despite `Program.cs` defining a full `System.CommandLine` CLI (see
§5). **VERIFIED** (grep across all FwLite `.csproj` files for `OutputType` —
`grep -rn OutputType backend\FwLite --include=*.csproj` — returns only `FwLiteMaui.csproj`,
`FwLiteMaui\build\Linq2DbCctorPatcher\Linq2DbCctorPatcher.csproj`, and `LcmDebugger.csproj`).
No OS conditionals of any kind in this csproj; it's plain managed code depending only on
`FwDataMiniLcmBridge` and `LcmCrdt` (`:6-7`), so it's exactly as cross-platform as those two are.

### 1d. Everything else (`MiniLcm`, `LcmCrdt`, `FwLiteShared`, `FwDataMiniLcmBridge`)

- `MiniLcm.csproj` — plain library, no platform conditionals.
- `LcmCrdt.csproj:12` — `<PackageReference Include="Microsoft.ICU.ICU4C.Runtime"
  Condition="$([MSBuild]::IsOsPlatform('Windows'))" />` — note this condition tests **the build
  machine's OS**, not the publish target RID. This only matters in practice because Windows
  publishes happen from `windows-latest` and Linux/macOS publishes happen from their own native
  runners (per §1a/§1b), so build-machine-OS and target-OS line up for every artifact CI actually
  produces — but it would silently do the wrong thing under cross-compilation (e.g. building
  win-x64 output from a Linux CI runner). **VERIFIED** condition text; **INFERRED** that this
  hasn't bitten anyone because CI always builds native-to-target.
- `FwDataMiniLcmBridge.csproj:11-13` — depends on `SIL.Core` and `SIL.LCModel` (the liblcm NuGet
  package) unconditionally, plus the same Windows-gated `Microsoft.ICU.ICU4C.Runtime` reference
  (`:12`).
- `FwLiteShared.csproj` — plain library, no platform conditionals; hosts the Blazor UI shared
  between `FwLiteMaui` and `FwLiteWeb` (wwwroot content built by the separate `frontend/viewer`
  pnpm/Vite project and copied in — see `fw-lite.yaml:39,214-227` `VIEWER_BUILD_OUTPUT_DIR`).

### 1e. Summary table

| Artifact | Windows | Linux | macOS | Android | iOS |
|---|---|---|---|---|---|
| FwLiteMaui | ✅ native, MSIX + portable single-file | — | — (csproj allows maccatalyst but no CI job builds it) | ✅ signed APK/AAB | — (csproj allows it but no CI job builds it) |
| FwLiteWeb | not in release pipeline, but buildable (README-sdk.md) | ✅ self-contained single-file, linux-x64 + linux-arm64 | ✅ self-contained multi-file, osx-x64 + osx-arm64, **unsigned** | — | — |
| FwLiteProjectSync | library only, no OS gating, not independently shipped | | | | |

---

## 2. Native / external dependencies, per artifact

### ICU — two layers of dependency, easy to conflate

1. **.NET's own globalization ICU** (used for `CultureInfo`, string comparison, etc.) — supplied by
   the .NET runtime itself on Linux/macOS via the OS's system ICU, or via the
   `Microsoft.ICU.ICU4C.Runtime` NuGet package on Windows (bundled because Windows historically
   lacked a usable system ICU for .NET). This layer is unrelated to FieldWorks.
2. **LibLCM's custom "SIL ICU" layer** — `liblcm\src\SIL.LCModel.Core\Text\CustomIcu.cs`. This is
   the one that actually matters for FieldWorks data:
   - `CustomIcu.cs:32`: `IcuucDllName = "icuuc" + Version + ".dll"` where `Version = "70"`
     (`:30`) — a **P/Invoke `DllImport` for `SilIcuInit`** (`:260-264`), a **SIL-specific custom
     export** that vanilla ICU4C does not have.
   - `CustomIcu.cs:224-247`: calling `SilIcuInit` is wrapped in `try/catch (DllNotFoundException)`
     and `catch (BadImageFormatException)` — if the custom SIL ICU library isn't present, it sets
     `HaveCustomIcuLibrary = false` and falls back to vanilla ICU normalization forms (`"nfc"`
     instead of `"nfc_fw"`, etc. — `GetIcuNormalizer`, `:401-437`). **It does NOT catch
     `EntryPointNotFoundException`** — if a library named `icuuc70`/`libicuuc70` IS resolvable but
     lacks the `SilIcuInit` export (e.g. a vanilla-ICU `.so` on Linux happens to satisfy the
     DllImport's name resolution), this would be an unhandled exception, not a graceful fallback.
     Whether this edge case is ever hit in practice depends on exactly how .NET's native library
     resolution and `icu.net`'s bundled/config-mapped natives behave on each OS — **not verified
     without running it** (see final section).
   - `CustomIcu.cs:42-58`: `DefaultDataDirectory` resolves to
     `%CommonApplicationData%\SIL\Icu70\icudt70l` (Windows) or the platform equivalent — this is
     where the actual `nfc_fw.nrm`/`nfkc_fw.nrm` data files must live. This directory is populated
     by installing the **`icu-fw`** package (SIL's custom ICU4C build + data), not by the vanilla
     ICU4C runtime.
   - liblcm's own CI installs `icu-fw` from a SIL-hosted apt repo (`linux.lsdev.sil.org`) for its
     Ubuntu leg only (`liblcm\.github\workflows\ci-cd.yml:31-34`); **there is no equivalent
     `icu-fw` install step anywhere in `languageforge-lexbox`'s `fw-lite.yaml`.**
   - `languageforge-lexbox`'s own build only ever references vanilla `icu.net`
     (`FwLiteWeb.csproj:17`) and, on Windows, the vanilla `Microsoft.ICU.ICU4C.Runtime`
     (`LcmCrdt.csproj:12`, `FwDataMiniLcmBridge.csproj:12`) — **neither is confirmed to be the
     SIL custom `icu-fw` build.** Whether Windows/Linux/macOS FwLite builds actually have working
     `SilIcuInit`/`nfc_fw` normalization, or silently run in vanilla-ICU fallback mode, **could not
     be determined by reading code alone** — this is one of the open items in the final section.
   - **What breaks without it**: per `CustomIcu.cs`'s own fallback design, nothing crashes —
     normalization silently degrades from FieldWorks' custom PUA/private-use-aware forms to
     standard Unicode NFC/NFD/NFKC/NFKD. This could produce subtly different string comparisons /
     sort orders than classic FieldWorks for writing systems that rely on the custom overrides, but
     it is not a hard failure. **VERIFIED** (from the code path itself), contingent on the
     unhandled-`EntryPointNotFoundException` caveat above never firing.
   - `liblcm\AGENTS.md:48`: *"ICU data generation requires ICU binaries (CI installs icu-fw on
     Ubuntu)"* — confirms this is a build-time/test-time dependency for liblcm itself, separate
     from what FwLite bundles at publish time.

### Mercurial / `hg` and `chorusmerge`

- **Bundled only in `FwHeadless` (the server-side component), not in any FwLite artifact.**
  `backend\FwHeadless\FwHeadless.csproj:11-13`:
  ```
  <PackageReference Include="SIL.ChorusPlugin.LfMergeBridge" />
  <PackageReference Include="SIL.Chorus.Mercurial" />
  <PackageReference Include="SIL.Chorus.ChorusMerge" GeneratePathProperty="true" />
  ```
  and `:33-48` copies `Mercurial\**`, `MercurialExtensions\**`, and a `chorusmerge` binary +
  `ChorusMerge.runtimeconfig.json` into the FwHeadless output directory.
  `FwHeadless.csproj:44` explicitly pins to the `net8.0` build of `ChorusMerge` with the comment
  *"Do not update this net8.0 path when we change frameworks; instead, this should be updated
  if/when Chorus acquires a net10.0 build."*
- **Nothing in `FwLiteMaui.csproj`, `FwLiteWeb.csproj`, or `FwLiteProjectSync.csproj` references
  Mercurial, hg, or chorusmerge at all** (verified by grep across the three csproj files —
  no matches). Send/Receive-style Mercurial merging is entirely a `FwHeadless` (server-side, Docker
  or server-hosted) concern; a local FwLite install never invokes `hg`/`chorusmerge` directly. It
  can, however, sync CRDT-to-FwData through `FwLiteProjectSync`'s `CrdtFwdataProjectSyncService`
  (a pure-C#/LCM read-write path, no Mercurial involved — see §5/§6).
  **VERIFIED.**

### SQLite

- `LcmCrdt` uses `Microsoft.Data.Sqlite`/`linq2db` (native `e_sqlite3` bundled transitively via the
  standard Microsoft.Data.Sqlite NuGet package, which ships prebuilt natives for win-x64/x86/arm64,
  linux-x64/arm64, osx-x64/arm64 — this is a mainstream, fully cross-platform NuGet dependency, not
  a bespoke SIL build). **INFERRED** (not independently re-derived from the .csproj/lock file in
  this pass, but this is standard, well-documented Microsoft.Data.Sqlite behavior).

### `Mono.Unix`

- `FwLiteMaui.csproj:109`: `<PackageReference Include="Mono.Unix" ExcludeAssets="all" />` with a
  comment explaining it's pulled in transitively (MiniLcm → SIL.WritingSystems) and excluded
  because it lacks 16 KB page-size support needed for Android/Google Play, and has "very minimal
  usage in SIL.LCModel." Confirms Unix P/Invoke plumbing exists in the dependency graph even on
  the mobile/Android target, where it's deliberately stripped.

---

## 3. Local storage layout

### `.fwdata` projects (FwDataMiniLcmBridge)

`backend\FwLite\FwDataMiniLcmBridge\FwDataBridgeConfig.cs`:
- Windows (`:18-22`, `[SupportedOSPlatform("windows")]`): reads
  `HKEY_CURRENT_USER\Software\SIL\FieldWorks\9\ProjectsDir`, falling back to
  `HKEY_LOCAL_MACHINE\...`, falling back to `C:\ProgramData\SIL\FieldWorks\Projects`.
- Unix (`:12-16,30-33`): `$XDG_DATA_HOME` or `~/.local/share`, then joined with
  `fieldworks/Projects` → effectively `~/.local/share/fieldworks/Projects`.
- `TemplatesFolder` (`:41`) similarly resolves to `<ProgramFolder>/Templates`, where
  `ProgramFolder` is `C:\Program Files\SIL\FieldWorks 9` on Windows (registry-overridable,
  `:25-28`) or `~/.local/share/fieldworks` on Unix.
- Project layout on disk, per `FieldWorksProjectList.cs:23-33`:
  `<ProjectsFolder>/<projectName>/<projectName>.fwdata` (one subfolder per project, containing a
  file with the same base name and a `.fwdata` extension). `EnumerateProjects()` (`:23-33`) walks
  every subdirectory of `ProjectsFolder` and only recognizes it as a project if that exact file
  exists.

### CRDT (`.sqlite`) projects (LcmCrdt)

- `LcmCrdt\LcmCrdtConfig.cs:8`: `ProjectPath` defaults to `Path.GetFullPath(".")` — i.e. the
  process's current working directory unless overridden.
- `CrdtProjectsService.cs:237`: `sqliteFile = Path.Combine(request.Path ?? config.Value.ProjectPath,
  $"{code}.sqlite")` — **one flat `.sqlite` file per project**, named by project code, directly
  inside `ProjectPath` (no per-project subfolder, unlike `.fwdata`).
- `LcmCrdtKernel.cs:71`: `crdtConfig.LocalResourceCachePath =
  Path.Combine(lcmConfig.Value.ProjectPath, "localResourcesCache")` — media/resource cache lives
  under the same root, one subfolder per project name (`LcmMediaService.cs:194`:
  `Path.Combine(options.LocalResourceCachePath, project.Name)`).
- **FwLiteMaui override** (`FwLiteMauiKernel.cs:104-123`):
  `baseDataPath = fwLiteMauiConfig.BaseDataDir` — from `FwLiteMauiConfig.cs:10`:
  `FileSystem.AppDataDirectory` (MAUI's per-platform app-data dir — e.g. `%LOCALAPPDATA%\...` on
  Windows, app-private storage on Android) **unless** `IsPortableApp` is true, in which case it's
  `Directory.GetCurrentDirectory()` (the portable/unpackaged single-file Windows exe keeps its data
  next to itself). `IsPortableApp` (`FwLiteMauiKernel.cs:165,170`) is `true` on Windows exactly
  when the app is NOT running as a packaged (MSIX) app; hardcoded `false` on every other platform.
  `LcmCrdtConfig.ProjectPath`, `CrdtConfig.FailedSyncOutputPath`
  (`= Path.Combine(baseDataPath, "failedSyncs")`), and `CrdtConfig.LocalResourceCachePath`
  (`= Path.Combine(baseDataPath, "localResourcesCache")`) are all rooted under this one
  `baseDataPath` (`:110-123`). Auth token cache and log files sit alongside it
  (`FwLiteMauiConfig.cs:26-28`: `app.log`, `app1.log`, `msal.cache`).
- **FwLiteWeb** uses ASP.NET Core configuration binding instead (`appsettings.json`):
  `FwLiteWeb\appsettings.json:14-22` ships `LcmCrdt:ProjectPath` and `FwDataBridge:ProjectsFolder`
  commented out ("uncomment... not used in windows, use BaseDataPath above instead" — a stale
  comment referring to a MAUI-only setting that doesn't exist in FwLiteWeb's own config surface).
  `appsettings.sdk.json:9,12` (the SDK/dev profile) sets them explicitly to `./fw-projects` and
  `./fw-lite-projects` (relative to the working directory) — confirming that absent explicit
  configuration, FwLiteWeb's CRDT store defaults to the process CWD (per `LcmCrdtConfig.cs:8`) and
  its FwData store defaults to the OS-specific `FwDataBridgeConfig` path above.

---

## 4. What FwLite can do offline today, feature by feature

Grounded in `FwLiteShared\Sync\SyncService.cs`, `FwLiteShared\Projects\CombinedProjectsService.cs`,
and `LcmCrdt\MediaServer\LcmMediaService.cs`.

| Feature | Works offline? | Evidence |
|---|---|---|
| Open/browse/edit a local CRDT project | Yes | `CrdtProjectsService`/`DataModel` are pure local SQLite; no network calls in the read/write path. |
| Open/read/write a local `.fwdata` project | Yes | `FwDataMiniLcmApi` operates directly on the local LCM cache; `Save()` (`FwDataMiniLcmApi.cs:81`) is a local file write. |
| Create a brand-new project (UI "Create Project" action) | Yes, but **CRDT only** | `CombinedProjectsService.cs:210-216`, `CreateProject` JS-invokable → `crdtProjectsService.CreateProjectFromTemplate(...)`. No UI path creates a new `.fwdata` project (see §6). |
| Sync CRDT project with Lexbox server | **No — requires server + auth** | `SyncService.ExecuteSync` (`SyncService.cs:48-131`) requires `project.OriginDomain` set (`:51-56`), a resolvable server (`:58-63`), and a signed-in OAuth client (`:71-76`); returns `SyncStatus.Offline`/`NotLoggedIn`/`NoServer` rather than doing anything when any of those are missing. |
| List "my Lexbox projects" | **No — requires server** | `CombinedProjectsService.RemoteProjects()` (`:52-62`) calls `lexboxProjectService.GetLexboxProjects` per configured server; `LocalProjects()` (`:104-136`) is the offline-safe counterpart (walks local CRDT dirs + the `FwDataProjectProvider`, no network). |
| Upload/download media (images, audio) attached to entries | **No — requires the Lexbox media server** | `LcmMediaService`'s `DownloadFile`/`UploadFile` (`LcmMediaService.cs:161-206`) go through `MediaServerClient()`, an HTTP client to the remote server; `LocalResourceCachePath` (§3) is only a *cache* of already-downloaded files, not a store of record. |
| Sign in / auth | **No — requires network** (MSAL against Lexbox's identity provider) | `FwLiteMauiKernel.cs:38-57` configures `LexboxServers` (`https://lexbox.org`, etc.); auth is OAuth against those hosts. |
| CRDT ↔ FwData two-way sync (Send/Receive-equivalent for local `.fwdata`) | Yes, offline, but **not exposed in the FwLite UI** | See §5/§6 — `CrdtFwdataProjectSyncService` is a local, in-process operation requiring no network, but it is wired up only for `FwHeadless` (server-side) and dev/test tooling, not surfaced as a button in `FwLiteWeb`/`FwLiteMaui`. |

**Bottom line**: everything that touches *this device's own files* (open, read, edit, save, a
local CRDT project or a local `.fwdata` project) works fully offline. Everything that touches
*another device or a shared record* (server-hosted project list, CRDT sync, media hosting, auth) requires
the Lexbox server specifically — there is no server-agnostic remote sync path today (§8).

---

## 5. The existing CLI — `FwLiteProjectSync\Program.cs`

Full file read (`Program.cs:1-118`). Three commands under one `RootCommand("CRDT to FwData sync
tool")` (`:15`), sharing two required global options `--crdt <sqlite file>` and `--fwdata <fwdata
file>` (`:16-19`):

1. **`before-sync`** (`:20-28`) — handler just echoes the two file paths to stdout. No-op hook
   point.
2. **`after-sync`** (`:29-37`) — same: echoes paths, no-op hook point.
3. **`sync`** (`:38-84`) — the real command:
   - Options: `--create-crdt-dir` (bool flag, `:39`), `--dry-run` (bool flag, default `false`,
     `:40`).
   - Builds a small isolated DI container (`SyncServices`, `:88-116`) wiring
     `AddLcmCrdtClient()`, `AddFwDataBridge()`, `AddFwLiteProjectSync()`, and configuring
     `FwDataBridgeConfig.ProjectsFolder` / `LcmCrdtConfig.ProjectPath` from the two file
     arguments' parent directories.
   - Hard requirement: `if (!File.Exists(fwDataFile)) throw ...` (`:90`) — **the `.fwdata` file
     must already exist; this command cannot create one from scratch.**
   - Opens the FwData project (`FieldWorksProjectList.GetProject`/`OpenProject`, `:54-60`),
     looks up or creates the corresponding CRDT project (`:61-66`, creating one via
     `CrdtProjectsService.CreateProject` if it doesn't exist yet — but a CRDT project is cheap to
     create, an `.fwdata` project is not).
   - Runs either `CrdtFwdataProjectSyncService.Import` (first sync, no prior snapshot,
     `:70-73`) or `.Sync` (subsequent syncs, diffing against a stored `ProjectSnapshot`) and
     regenerates the snapshot afterward unless `--dry-run` (`:76-79`).
- **Distribution status**: `FwLiteProjectSync.csproj` has **no `<OutputType>Exe</OutputType>`**
  (§1c) — the project builds as a class library by default. Confirmed there is no
  `dotnet publish`/`dotnet run` invocation of `FwLiteProjectSync` anywhere in the CI workflows
  (`fw-lite.yaml`, `develop-fw-headless.yaml`) — grep for `FwLiteProjectSync` in both files returns
  only path-trigger filters (`fw-lite.yaml:127,130` invoke `FwLiteProjectSync.Tests.csproj`, never
  the main project). **In production, `FwLiteProjectSync` is consumed purely as an in-process
  library**: `FwLiteWebKernel.cs:23` (`services.AddFwLiteProjectSync()`),
  `FwLiteMauiKernel.cs:61` (same, inside `#if INCLUDE_FWDATA_BRIDGE`), and
  `FwHeadless.csproj:19` (`ProjectReference`, compiled directly into the FwHeadless server). The
  `Program.cs` CLI exists in source and is exercised by nothing in the shipped pipeline; it reads
  as either a developer convenience for manual CRDT↔FwData sync testing, or a vestigial/planned
  standalone tool that was never wired into a publish step. **VERIFIED** (absence of `OutputType`,
  absence of any build/run of the main project as an executable in either workflow file).

---

## 6. `.fwdata` handling locally, per artifact and platform

- **FwLiteWeb**: `ProjectReference` to `FwDataMiniLcmBridge` is unconditional
  (`FwLiteWeb.csproj:37-38`) — every published RID (`linux-x64`, `linux-arm64`, `osx-x64`,
  `osx-arm64`, and any Windows build) compiles in `.fwdata` support. `CombinedProjectsService.cs:50`
  (`SupportsFwData()`) reports `true` whenever an `IProjectProvider` with
  `DataFormat == ProjectDataFormat.FwData` is registered — which, for FwLiteWeb, is always.
- **FwLiteMaui**: `.fwdata` support is gated to the Windows TFM only (§1a,
  `FwLiteMaui.csproj:26-28`) — Android (and any hypothetical iOS/Mac Catalyst build) is CRDT-only.
- **Reading**: `FieldWorksProjectList.EnumerateProjects()`/`GetProject()`
  (`FieldWorksProjectList.cs:23-38`) + `FwDataFactory.GetFwDataMiniLcmApi` open the LCM cache
  directly off disk — this is genuinely local, no network, works the same way on every OS where
  the bridge is compiled in.
- **Writing**: `FwDataMiniLcmApi.Save()` (`FwDataMiniLcmApi.cs:81`) persists back to the same
  `.fwdata` file. Mutation methods throughout `FwDataMiniLcmApi.cs` and the `Api\UpdateProxy\*.cs`
  shim classes do carry numerous `NotSupportedException`/`NotImplementedException` throws (e.g.
  `FwDataMiniLcmApi.cs:615` "Morph types cannot be created in fwdata; they are predefined",
  `:719,736` unknown-object-type guards, and dozens of setter/getter stubs across
  `UpdateEntryProxy.cs`, `UpdateSenseProxy.cs`, `UpdateDictionaryProxy.cs`, etc.). These read as
  **intentional domain-model guards** (LCM's fixed vocabularies, or write proxies that only
  implement the specific properties the sync engine touches) rather than platform gaps — I did not
  have budget to individually verify each of the ~40 `NotImplementedException` sites is dead code
  vs. a live reachable gap; flagging as a specific follow-up in the final section.
- **Creating a brand-new `.fwdata` from scratch**: **supported at the library level, not exposed
  in the UI.** `FwDataMiniLcmBridge\LcmUtils\ProjectLoader.cs:78-100`
  (`IProjectLoader.NewProject` → `LcmCache.CreateNewLangProj`) genuinely creates a new empty LCM
  project on disk. But grepping `FwLiteShared`/`FwLiteWeb`/`FwLiteMaui` for `IProjectLoader` or
  `NewProject(` turns up **zero** call sites outside test fixtures
  (`FwDataMiniLcmBridge.Tests\Fixtures\ProjectLoaderFixture.cs:32`,
  `FwLiteProjectSync.Tests\ProjectTemplateTests.cs:128`,
  `FwLiteProjectSync.Tests\Import\FullImportTests.cs:57`,
  `FwLiteProjectSync.Tests\Fixtures\SyncFixture.cs:103`,
  `FwDataMiniLcmBridge.Tests\CanonicalMorphTypeTests.cs:30`). The UI's own
  "Create Project" action (`CombinedProjectsService.cs:210-216`) always creates a CRDT project via
  `ProjectTemplate.CreateNewSnapshot` (`LcmCrdt\Project\ProjectTemplate.cs:22-37`, an embedded
  JSON template of a blank project), never an `.fwdata` file. **VERIFIED: creating a fresh
  `.fwdata` project is a real, working code path, but it is currently reachable only from tests —
  not from any shipped FwLite entry point.**
- **Per-OS summary**: Linux and macOS `FwLiteWeb` builds have the `.fwdata` bridge compiled in and,
  per the code, should be able to open/read/write an existing `.fwdata` project the same way
  Windows does — **but this is untested by CI on those platforms** (see §7's gap below) and is
  contingent on the ICU caveat in §2.

---

## 7. PanGloss

Source: `PanGloss\rust\Cargo.toml`, `PanGloss\rust\crates\pg-cli\*`,
`PanGloss\rust\crates\pg-fwdata\*`, `PanGloss\.github\workflows\rust-ci.yml`,
`PanGloss\rust\README.md`, `PanGloss\README.md`.

### What it is

A pure-Rust workspace (`Cargo.toml:1-20`, 18 crates). The binary is `pg-cli`, built as
`pangloss` (`crates\pg-cli\Cargo.toml:8-10`: `[[bin]] name = "pangloss"
path = "src/main.rs"`). No FFI/C# dependency for the CLI path — `pg-ffi` is a separate `cdylib`
crate for embedding into FieldWorks/.NET hosts (`rust\README.md:32`: *"native C ABI... callable
from .NET Framework 4.8 (FieldWorks)"*), not something `pangloss` (the CLI) links against.

### Input / output (verified from `main.rs`'s own doc comments and dispatch table, lines 1-355)

- **Grammar input, three shapes, dispatched by file extension** (`main.rs:325-355`,
  `load_grammar`):
  - `.xml` — legacy HermitCrab XML export (`pg_grammar::load`).
  - `.json` — a `pg-snapshot` `Snapshot` (PanGloss's own format).
  - **`.fwdata` — a FieldWorks project file, imported in-memory and compiled on the fly, no
    intermediate file** (`main.rs:339-347`; the actual reader is `pg_fwdata::import_file`,
    `PanGloss\rust\crates\pg-fwdata\src\lib.rs:61-69`).
- **`pg-fwdata` reads `.fwdata` natively in Rust** — a streaming `quick_xml` parser
  (`pg-fwdata\src\lib.rs:8-12`: *"a flat sequence of `<rt class=... guid=...>` records"*, "never
  builds a DOM of the whole document," the larger project is "~54MB"). **No dependency on liblcm, .NET, or
  FieldWorks itself** — this is a from-scratch parser of the `.fwdata` XML schema. Malformed
  `.fwdata` data (dangling refs, unknown morph types, stale ad-hoc rules) is downgraded to
  warnings in an `ImportReport`, never a panic/hard error
  (`ImportError` enum, `lib.rs:40-48`, is scoped to I/O/XML-shape failures only).
- **Standalone `import` subcommand**: `pangloss import <project.fwdata> <out.json>`
  (`main.rs:281-315`) writes the extracted `Snapshot` as JSON, prints `ImportReport` + validation
  warnings to stderr.
- **The report artifact the brief asks about**: `pangloss make-report <grammar> <out.md>
  [--pack=<path>] [--words=<path>] [--corpus=<path> --attestor=<name> --attested-on=<date>]
  [--policy=<path>] ...` (`main.rs:253`, implemented in `crates\pg-cli\src\make_report.rs`) — a
  **Markdown** "PanGloss readiness report" (`make_report.rs:491`) with sections for verdict,
  capability, trust, a checks table, coverage attestation, build time, latency methodology, a
  Mermaid compilation-plan diagram, "what this report did NOT test," and pinned revisions/hashes
  for re-derivation (`make_report.rs:491-587`). Other CLI-produced artifacts: `.pgpack` (a signed,
  hashed "Language Pack" container — `pack.rs`, `pg-pack` crate), `build.json`/`assessment.json`
  (from `diagnose`), TSV (`batch`), plain stdout lines (`parse`).
- Every other subcommand: `batch`, `generate`, `parse`, `diagnose`, `fst-health`, `coverage`,
  `plan-diagram`, plus a hidden `__compile-worker-child` re-exec entry point used only by
  `pack --watchdog` (`main.rs:225-240`) for a sandboxed FST-compile worker process.

### Build / distribution — genuinely dev-only today

- `PanGloss\.github\workflows\rust-ci.yml:1-25`: triggers on push/PR to `main`; **every job
  (`fmt`, `clippy`, `test`, `coverage`) runs on `ubuntu-latest` only.** There is no Windows or
  macOS runner in this workflow, and **no release/publish/artifact-upload job exists at all** —
  contrast with `languageforge-lexbox`'s `fw-lite.yaml`, which has explicit `publish-*` jobs per
  OS. **VERIFIED** (full file read).
- `rust\README.md:79-80`: *"Requires the MSVC toolchain (`x86_64-pc-windows-msvc`); the linker is
  auto-located via Visual Studio's vswhere."* This describes the developer's local build
  environment for producing the FieldWorks-embeddable `cdylib` (`pg-ffi`), which targets a Windows
  .NET-Framework-4.8 host — **not** a hard requirement for the `pangloss` CLI itself, which the CI
  workflow above proves builds and tests fine on plain Linux (`cargo build --workspace
  --all-targets`, `rust-ci.yml:53`, no MSVC anywhere in that job).
- No `Cargo.toml` dependency in `pg-cli`, `pg-fwdata`, or any of their transitive workspace
  members is OS-specific (workspace deps at `Cargo.toml:23-49` are all portable crates.io crates:
  `quick-xml`, `serde`, `rayon`, `thiserror`, `sha2`, `ed25519-dalek`, `sysinfo`, etc.) —
  **INFERRED that a `cargo build --release` of `pg-cli` would produce a working native binary on
  Windows, Linux, and macOS alike**, since nothing in the dependency graph is platform-gated and CI
  already proves the Linux leg. Not independently verified by actually cross-compiling.
- **Net: PanGloss is not built or distributed as a release binary for any OS today.** The only way
  to run `pangloss` right now is `cargo build --release` from a local Rust toolchain checkout
  (`rust\README.md:74`) — there is no downloadable artifact, no CI-produced binary, and no install
  path documented for an end user. This is the single largest gap for the "smallest clean local
  install" scenario in the final section.

### What bundling it into a .NET desktop install would require

- The `pangloss` CLI itself is a single native executable with no runtime dependencies beyond libc
  (per the all-portable-crates dependency graph above) — bundling the **CLI** would be as simple as
  shipping the compiled binary alongside `FwLiteWeb`/`FwLiteMaui` and shelling out to it (similar to
  how `FwHeadless.csproj:40-43` ships the `chorusmerge` binary as `<None Include="chorusmerge">`
  content). This is a packaging/CI gap (needs a per-OS `cargo build --release` + artifact-upload
  step added to a workflow), not an architectural one.
- Bundling the **FFI `cdylib`** (`pg-ffi`) for a tighter in-process integration would require
  per-RID native library packaging into the .NET project (`runtimes/<rid>/native/*.{dll,so,dylib}`
  convention) plus a P/Invoke or source-generated binding layer — none of which exists in either
  repo today. **INFERRED** — no such binding code was found in `languageforge-lexbox` in this pass
  (not exhaustively re-searched given time budget, but no reference to `pg_ffi`/`pangloss` turned
  up anywhere in earlier greps of `backend\FwLite`).

---

## 8. Sync / distribution model — server-only today, but not architecturally locked to a server

- **What's actually wired up in FwLite: HTTP-to-Lexbox-server only.** `SyncService.ExecuteSync`
  (`FwLiteShared\Sync\SyncService.cs:48-131`) drives `dataModel.SyncWith(remoteModel)` where
  `remoteModel` comes from `CrdtHttpSyncService.CreateProjectSyncable`
  (`LcmCrdt\RemoteSync\CrdtHttpSyncService.cs:74-79`), which wraps a Refit-generated HTTP client
  (`ISyncHttp`, `CrdtHttpSyncService.cs:141-154`: `GET /api/crdt/checkConnection`,
  `POST /api/crdt/{id}/add`, `GET /api/crdt/{id}/get`, `POST /api/crdt/{id}/changes`) — i.e. a
  REST API against a specific Lexbox server's authority. This is the **only** production
  implementation of Harmony's `ISyncable` interface found anywhere in `languageforge-lexbox`
  (grepping the whole repo for `: ISyncable` returns exactly two hits:
  `LcmCrdt\RemoteSync\CrdtHttpSyncService.cs` (production) and `LcmDebugger\FakeSyncSource.cs`
  (a dev/debug-only stub, `LcmDebugger\FakeSyncSource.cs:25-90`)).
- **The CRDT substrate (`harmony`) is transport-agnostic, and already ships a second,
  file-based `ISyncable` implementation that FwLite does not use.**
  `ISyncable` itself (`harmony\src\SIL.Harmony\ISyncable.cs:3-11`) is a plain interface —
  `GetChanges`/`GetSyncState`/`AddRangeFromSync`/`SyncWith`/`SyncMany`/`ShouldSync` — with no
  transport assumption baked in. `harmony\src\SIL.Harmony\JsonSyncable.cs:1-150` implements it
  entirely over **a shared directory of newline-delimited JSON commit files**
  (`client_<clientId>.jsonl`, `JsonSyncable.cs:17-18`), with per-client file locking
  (`ClientLocks`, `:15,36`), append-only writes for new commits (`AddRangeFromSync`, `:28-48`), and
  head-state computed by scanning all client files (`GetSyncState`, `:50-60`). This is exactly the
  shape of a "shared folder / USB stick / Syncthing / git-style" sync mechanism — **the CRDT model
  does not require a central server; a shared filesystem location is sufficient by construction.**
  `JsonSyncable` is production code in the `SIL.Harmony` package (not a test helper), but grepping
  all of `languageforge-lexbox` for `JsonSyncable` returns **zero matches** — it is currently
  unused by FwLite/LcmCrdt/FwHeadless entirely.
- **Answering the brief's question directly**: two laptops **cannot** sync directly today without
  standing up a Lexbox-compatible HTTP endpoint, because FwLite only wires `ISyncable` to
  `CrdtHttpSyncService`. But this is a product/wiring gap, not a substrate limitation — Harmony
  already contains (and tests, per `harmony\src\SIL.Harmony.Tests\Syncable\*`) a working
  file-based `ISyncable`. Making two laptops sync "git-style" via a shared folder (Dropbox,
  Syncthing, a USB drive) would mean writing a new `ISyncable` adapter (or lightly adapting
  `JsonSyncable`) and wiring it into `SyncService`/`CombinedProjectsService` alongside
  `CrdtHttpSyncService` — a moderate, well-scoped addition, not a rearchitecture.
- **Separately, CRDT↔FwData sync (`CrdtFwdataProjectSyncService`, §5/§6) is already local-only,
  no server involved** — it diffs two local files (a `.sqlite` and a `.fwdata`) directly. This is a
  distinct mechanism from CRDT↔CRDT sync and already satisfies "no server" for that one pairing.

---

## 9. The 15% — smallest clean local install, and what's actually missing

### Smallest install that could run: open a project → edit → generate `.fwdata` → run PanGloss → read the report

Two independently-installable pieces cover the .NET side (per-OS, no Docker involved in any of
this — Docker only appears in the codebase for `FwHeadless`/server-side components, never for
FwLite):

- **Windows**: the signed MSIX or the portable single-file `FwLiteMaui` build
  (`fw-lite.yaml:462-477`) — native app, `.fwdata` bridge included (Windows TFM), self-contained
  (`FwLiteMaui.csproj:24`).
- **Linux/macOS**: the self-contained `FwLiteWeb` single-file/multi-file build
  (`fw-lite.yaml:307-313,338-342`) run from a terminal or via the `.desktop` launcher
  (`install-launcher.sh`), `.fwdata` bridge included unconditionally (`FwLiteWeb.csproj:37-38`).

Both are genuinely "clean install, no Docker, self-contained .NET" per the csproj/workflow
evidence in §1. What's missing to complete the chain:

1. **Open a project → make changes** — fully supported today, on every OS covered above, for both
   CRDT-native and (where the bridge is compiled in) `.fwdata` projects. **Not missing.**
2. **Generate a `.fwdata`** — this is the first real gap. A user's default "Create Project" flow
   only produces a CRDT `.sqlite` project (§6); turning that into a `.fwdata` requires
   `CrdtFwdataProjectSyncService`, which is real, tested, working code (`FwLiteProjectSync`), but:
   - it is not exposed as a UI action in `FwLiteWeb`/`FwLiteMaui` (no route or Blazor button calls
     it outside of `FwHeadless`/tests, per §6's grep results), and
   - its standalone CLI path (`Program.cs`) isn't built as an executable (§1c/§5), and
   - the `sync` command additionally **requires the target `.fwdata` file to already exist**
     (`Program.cs:90`) — it does not create one, so even a working standalone CLI wouldn't
     bootstrap a `.fwdata` from nothing. Creating one from scratch needs
     `IProjectLoader.NewProject` (`ProjectLoader.cs:78-100`), which is real and tested but has no
     call site outside test fixtures (§6).
   - **This is "unpackaged," not "missing."** All the pieces (create-fwdata, sync-crdt-to-fwdata)
     exist and are exercised by tests; nothing needs to be invented, only wired together and given
     a UI entry point (or, more simply, packaged as a genuine standalone CLI by adding
     `<OutputType>Exe</OutputType>` and a publish step).
3. **Run PanGloss on the resulting `.fwdata`** — mechanically ready:
   `pangloss import project.fwdata out.json` or directly `pangloss batch/parse
   project.fwdata ...` (dispatches on the `.fwdata` extension, §7) needs no export step from
   FieldWorks/FwLite at all — it reads the `.fwdata` XML itself. **The actual gap is
   distribution**: there is no built/published `pangloss` binary for any OS (§7) — a user would
   have to clone `PanGloss`, install a Rust toolchain, and `cargo build --release` themselves. This
   is the second real gap, and it's a bigger one than #2: it's not "unwired," it's "not built for
   end users at all."
4. **Read the report** — `pangloss make-report <grammar> <out.md>` produces a plain Markdown file
   (§7); no viewer/tooling gap here beyond "open the .md file," which every OS can do trivially.

### Genuinely missing vs. merely unpackaged

**Merely unpackaged** (code exists, tested, just not wired to an end-user entry point or a CI
publish step):
- CRDT→`.fwdata` sync as a UI action (exists as `CrdtFwdataProjectSyncService`, exposed only to
  `FwHeadless`/tests).
- `.fwdata`-from-scratch creation as a UI action (exists as `IProjectLoader.NewProject`, exposed
  only to tests).
- `FwLiteProjectSync` as a real standalone CLI (source is complete and functional; just needs
  `OutputType=Exe` + a publish step, plus removing/relaxing the "fwdata must already exist" check
  in `Program.cs:90` for a from-scratch flow).
- A file-based (no-server) sync path between two FwLite installs (`JsonSyncable` already exists in
  `harmony`, unused in `languageforge-lexbox`).
- A macOS-signed/notarized `FwLiteWeb` build (the `#todo sign the app` at `fw-lite.yaml:314`).

**Genuinely missing** (no working code/build path exists yet):
- A built, distributable `pangloss` binary for any OS — no release job exists in
  `PanGloss\.github\workflows\rust-ci.yml`, only CI checks on Linux.
- A native macOS/iOS `FwLiteMaui` app (macOS users get the web-app artifact instead — a different
  UX, not a gap in coverage, but not "the same app" either).
- Confirmed, verified delivery of the SIL custom `icu-fw` normalization library (vs. vanilla ICU4C)
  on Linux/macOS FwLite builds — see the open item below.

---

## What I could not determine without actually building or running something

1. **Whether `SilIcuInit`/the custom `nfc_fw`/`nfkc_fw` normalization actually resolves at runtime**
   on Linux and macOS `FwLiteWeb` builds, vs. silently falling back to vanilla ICU normalization
   (§2). This depends on exactly which native ICU binaries `icu.net`'s NuGet package bundles per
   RID, how .NET's `DllImport("icuuc70.dll")` resolves on non-Windows (name-mangling rules, any
   `icu.net.dll.config` remap file), and whether those bundled binaries are vanilla ICU4C or the
   SIL `icu-fw` variant. I could not resolve this by reading source and ran out of budget chasing
   the NuGet package contents directly (a filesystem-wide search for the installed
   `icu.net` NuGet package on this machine was inconclusive/too slow to complete in-session).
   **Would require**: actually running `FwDataMiniLcmBridge.Tests` on a Linux/macOS machine (or
   inspecting the resolved `icu.net` package's `runtimes/*/native` contents) and checking whether
   `CustomIcu.HaveCustomIcuLibrary` comes back `true` or `false`.
2. **Whether `FwDataMiniLcmBridge.Tests` — which IS included in `FwLiteCore.slnf` and IS run by
   `fw-lite.yaml`'s `build-and-test` job on `ubuntu-latest`** — actually exercises real
   `.fwdata`-file read/write via LCM on that Linux runner, or whether the tests that matter for
   this are mocked/skip that path. `fw-lite.yaml` has no explicit `icu-fw` install step for that
   job (unlike liblcm's own CI), so either (a) the tests pass without it because of the fallback
   path in point 1, or (b) they don't actually exercise the ICU-dependent code, or (c) something
   else provisions it that I didn't find. Would require actually running the test job (or reading
   its historical CI logs) to know which.
3. **Whether every `NotImplementedException`/`NotSupportedException` in `FwDataMiniLcmBridge\Api\
   UpdateProxy\*.cs` (§6) is dead/unreachable code vs. a live gap a user could actually hit while
   editing a `.fwdata` project through FwLite's UI.** I enumerated the sites but did not trace each
   one's call graph from the Blazor UI down; that would need either running the app interactively
   against each affected field, or a dedicated call-graph pass per property.
4. **Whether a `cargo build --release` of `pg-cli` genuinely succeeds unmodified on Windows and
   macOS** (§7). The dependency graph looks portable and Linux CI proves that leg, but I did not
   attempt an actual cross-platform build.
5. **Runtime behavior of the Windows `Microsoft.ICU.ICU4C.Runtime` build-machine-OS-conditioned
   package reference** (§1d) under any future cross-compilation scenario — today's CI always builds
   native-to-target so this has never been exercised, but that's an inference from workflow
   structure, not a test I ran.
