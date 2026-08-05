\

> **HISTORICAL — a path not taken.** This document evaluates routing Motif's changes through a CRDT merge
> layer, or inventories the repositories that would have been involved. **That design was assessed and
> rejected** ([adoption report](harmony-adoption-report.md)); operations target LibLCM directly and there is
> no merge layer. Kept as the evidence behind that decision. **It is not a plan, and nothing in it is
> scheduled.** For the live plan see [Plan A](plan-motif.md).
# Lexbox Repository — Exhaustive Capability Inventory

**Repo:** `C:\Users\johnm\Documents\repos\languageforge-lexbox`, branch `develop` @ `da284fa8e628a7acfa76a080dabfc324272ce64e` (2026-07-23).
**Method:** every `*.csproj`/`package.json` opened, every `Program.cs`/kernel/DI registration read, every file under `.github/workflows/` read in full, every controller/route file read or grepped for route attributes, every Dockerfile and `deployment/**` manifest referenced from a workflow opened. Nothing here is from a README summary alone unless marked INFERRED.

Legend: **VERIFIED** = read the source/config directly, cited `path:line`. **INFERRED** = reasoned from adjacent verified evidence, not read directly.

---

## 0. Repository map (top level)

VERIFIED (`ls` at repo root):

| Dir | Contents |
|---|---|
| `backend/` | All .NET projects (17 buildable projects + `Testing`) |
| `frontend/` | Main SvelteKit admin app (`frontend/src`), FwLite viewer SPA (`frontend/viewer`), dev-only https proxy (`frontend/https-proxy`) |
| `hgweb/` | Apache/hgweb container (Mercurial HTTP serving) |
| `platform.bible-extension/` | A Platform.Bible (Paranext) extension called **"lexicon"**, talks to a local FwLiteWeb instance over HTTP |
| `deployment/` | Kustomize manifests: `base/`, `develop/`, `staging/`, `production/`, `local-dev/`, `gha/`, `init-repos/`, `setup/`, `restore-scripts/` |
| `data/` | Trivial `busybox` image that just copies seed files (used by `deployment/init-repos`) |
| `otel/` | OpenTelemetry Collector config + its own kustomization |
| `proxy/` | `default.conf` — an nginx/proxy config (used by the UI container, see `frontend/Dockerfile`) |
| `.github/workflows/`, `.github/actions/` | 16 workflow files, 4 composite actions — read in full, §5 |
| `crowdin/` | l10n config |

Solution/filter files at repo root (VERIFIED, `find . -maxdepth 2 -iname "*.sln*"`): `LexBox.sln` (everything), `LexBoxOnly.slnf`, `FwLiteOnly.slnf`, `FwLiteCore.slnf` (FwLite minus MAUI — this is what CI builds/tests on Linux; MAUI builds only on `publish-win`/`windows-latest`, per `.github/workflows/fw-lite.yaml:77,446`).

---

## 1. Every backend project

VERIFIED from each `*.csproj` (`backend/**/*.csproj`, 20 files) plus `backend/Directory.Build.props:1-24` (default TFM `net10.0`, nullable+implicit-usings enabled repo-wide, `WarningsAsErrors=Nullable`) and `backend/Harmony*.props` (Harmony package/source-reference switch, see §7).

| Project | Kind | TFM(s) | Produces | Purpose (one line) |
|---|---|---|---|---|
| `LexBoxApi` | ASP.NET Core app (`Sdk.Web`) | net10.0 | Linux container `ghcr.io/sillsdev/lexbox-api` | Main server: GraphQL, REST controllers, auth, hg proxy front door, Quartz jobs |
| `LexData` | Library | net10.0 | dll | EF Core `LexBoxDbContext` over Postgres; migrations; Quartz-EFCore-Postgres store |
| `LexCore` | Library | net10.0 (`LangVersion=preview`) | dll | Domain entities, auth types, service interfaces — shared by API and (indirectly) FwHeadless |
| `LfClassicData` | Library | net10.0 | dll | MongoDB access layer for legacy Language Forge ("classic") projects; implements `IMiniLcmApi` over Mongo |
| `SyncReverseProxy` | ASP.NET Core app (`Sdk.Web`) | net10.0 | Linux container (built from `backend/Dockerfile`, no separate Dockerfile — INFERRED single multi-stage `backend/Dockerfile` builds whichever csproj context.dotnet-publish targets; workflow `lexbox-api.yaml:98` builds context `backend` with no `file:` override, i.e. the default `backend/Dockerfile`) | YARP reverse proxy for the raw Mercurial wire protocol (`hg` client ↔ hgweb), JWT-over-basic-auth translation |
| `WebServiceDefaults` | Library | net10.0 | dll | Aspire-style shared OTel/service-discovery/resilience wiring (`AddServiceDefaults`), consumed by FwHeadless |
| `FixFwData` | Console app | net10.0 (`OutputType=WinExe`) | apphost exe, co-published *inside* the FwHeadless container image | Wraps `SIL.LCModel.FixData.FwDataFixer` — repairs/validates a `.fwdata` file from the CLI; also smoke-tested at FwHeadless image build time (`backend/FwHeadless/Dockerfile:37-45`) |
| `FwHeadless` | ASP.NET Core app (`Sdk.Web`) | net10.0 | Linux container `ghcr.io/sillsdev/lexbox-fw-headless` | Server-side FieldWorks Send/Receive + Harmony/CRDT sync worker (full detail §8) |
| `Testing` | xUnit test project | net10.0 | test dll | Integration/unit tests for LexBoxApi, FwHeadless, LexData, LexCore, SyncReverseProxy |
| `MiniLcm` | Library | net10.0 (`preview`) | dll (NuGet-shaped, `FileVersion` set) | **The** lexicon domain-model abstraction: `IMiniLcmApi`/`IMiniLcmReadApi`/`IMiniLcmWriteApi`, models (`Entry`,`Sense`,…), JSON-Patch `UpdateObjectInput<T>`, filtering/query options |
| `MiniLcm.Tests` | xUnit | net10.0 | test dll | 353 `[Fact]`/`[Theory]` (VERIFIED count, `grep`) — model/serialization/filter tests |
| `LcmCrdt` | Library | net10.0 | dll | CRDT-backed `IMiniLcmApi` implementation on SQLite + `SIL.Harmony`; history/activity, comments, custom views, media |
| `LcmCrdt.Tests` | xUnit | net10.0 | test dll | 209 `[Fact]`/`[Theory]` |
| `FwDataMiniLcmBridge` | Library | net10.0 (`preview`) | dll | `IMiniLcmApi` implementation over **LibLCM** (`SIL.LCModel`) — the FieldWorks `.fwdata` reader/writer |
| `FwDataMiniLcmBridge.Tests` | xUnit | net10.0 | test dll | 46 `[Fact]`/`[Theory]`; pulls `SIL.LCModel` content templates |
| `FwLiteProjectSync` | Library **+ existing CLI** | net10.0 | dll, and (as `dotnet run`) a `System.CommandLine` console tool | Sync engine between CRDT and FwData; **ships a CLI today** (`before-sync`/`after-sync`/`sync` commands) — see §2 |
| `FwLiteProjectSync.Tests` | xUnit + BenchmarkDotNet | net10.0 | test dll | 107 `[Fact]`/`[Theory]`; also `Category=Benchmark` tests run in a separate CI job on `develop` |
| `FwLiteShared` | Razor class library | net10.0 | dll | Cross-host application services shared by FwLiteWeb and FwLiteMaui: auth (MSAL), SignalR client, sync orchestration, update checker, Reinforced.Typings TS codegen |
| `FwLiteShared.Tests` | xUnit | net10.0 | test dll | 52 `[Fact]`/`[Theory]` |
| `FwLiteWeb` | ASP.NET Core app (`Sdk.Web`), **self-contained** | net10.0 | Single-file self-contained executable for **linux-x64, linux-arm64, osx-x64, osx-arm64**; also a `Dockerfile` present but **not wired into any workflow** (see finding F1 below) | Local HTTP server + embedded static viewer SPA; the desktop/server-optional FwLite host for Windows/Linux/macOS |
| `LcmCrdt.Tests`,`FwLiteProjectSync.Tests`,`FwLiteShared.Tests`,`FwDataMiniLcmBridge.Tests`,`MiniLcm.Tests`,`FwLiteMaui.Tests` | — | — | — | (test projects, tabulated above/below) |
| `FwLiteMaui` | .NET MAUI app (`Sdk.Razor`) | `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0` (conditionally, per host OS building) | Android APK/AAB (signed), Windows MSIX + portable, (iOS/MacCatalyst targets exist in csproj but **no CI job publishes them** — F2) | Mobile/desktop MAUI shell hosting the same viewer SPA via `BlazorWebView`/WebView |
| `FwLiteMaui.Tests` | xUnit (multi-target) | mirrors FwLiteMaui's TFMs | test dll | 6 `[Fact]`/`[Theory]` (built/run only in the Windows `publish-win` CI job) |
| `LcmDebugger` | Console app | net10.0 | exe | Ad-hoc dev tool: opens a downloaded CRDT project and runs `SyncFwHeadlessProject` against a mocked server client — **not shipped**, dev-only |
| `Linq2DbCctorPatcher` (`FwLiteMaui/build/Linq2DbCctorPatcher`) | Console app (build-time tool) | net10.0 | dll invoked via `dotnet` at MSBuild time | IL-patches a `linq2db.EntityFrameworkCore` static constructor for Android (works around a broken `cctor`, tracked as issue #2291) |

Source-file volume (VERIFIED, `find *.cs \| wc -l`, excludes `obj/bin`): LexBoxApi 109, LexData 115, LexCore 57, LfClassicData 19, SyncReverseProxy 13, FwHeadless 33, FixFwData 1, WebServiceDefaults 1, MiniLcm 89, LcmCrdt 137, FwDataMiniLcmBridge 36, FwLiteProjectSync 9, FwLiteShared 62, FwLiteWeb 21, FwLiteMaui 29, LcmDebugger 3.

### Frontend projects

VERIFIED (`package.json` in each):

| Project | Stack | Produces | Purpose |
|---|---|---|---|
| `frontend` (main app) | SvelteKit 2 + Svelte, `@urql/svelte` GraphQL client, `graphql-codegen`, OpenTelemetry web SDK, `tus-js-client`, `mjml` (email templates) | Linux container `ghcr.io/sillsdev/lexbox-ui` | The Lexbox admin/project-management web UI (org/user/project CRUD) |
| `frontend/viewer` | SvelteKit-based SPA (Vite, Storybook, Playwright, `@lingui` i18n, Tailwind 4) | Static JS bundle embedded into `FwLiteWeb`/`FwLiteMaui` via `wwwroot/viewer`, and separately Playwright-tested standalone | The FieldWorks Lite lexicon editor UI — the actual data-entry/browse/sync/history/comments UI |
| `frontend/https-proxy` | Vite + `@vitejs/plugin-basic-ssl` | dev-only, not deployed | Local HTTPS dev proxy so MSAL/OAuth redirect flows work against `https://` locally |
| `platform.bible-extension` (`lexicon`) | Webpack, React/TS, talks to a **local FwLiteWeb** via `src/utils/fw-lite-api.ts` | `.zip`/extension package | Paranext/Platform.Bible in-editor lexicon panel (add/find word, related words) sourced from FwLite's HTTP API |

---

## 2. Every executable entry point

VERIFIED — every `Program.cs` in the repo was opened (`find backend -iname Program.cs`, 10 hits) plus every `RootCommand`/`Main`/`BackgroundService` grepped.

| Entry point | Type | What it exposes |
|---|---|---|
| `backend/LexBoxApi/Program.cs:1-207` | ASP.NET Core Minimal-Hosting app | See §3 for the full route table. Also handles two non-server CLI-like invocations checked *before* the web host builds: `MigrationKernel.IsMigrationRequest(args)` (`:27-31`, runs EF migrations and exits) and `DevGqlSchemaWriterService.IsSchemaGenerationRequest(args)` (`:33-37`, dumps the GraphQL SDL and exits — used by frontend codegen). |
| `backend/FwHeadless/Program.cs:1-103` | ASP.NET Core Minimal-Hosting app | Media routes, merge routes, 2 delete endpoints (§3, §8). |
| `backend/SyncReverseProxy/Program.cs:1-41` | ASP.NET Core Minimal-Hosting app | `AddSyncProxy()`/`MapSyncProxy()` — YARP-based hg wire-protocol proxy; no GraphQL, no controllers besides the proxy. |
| `backend/FwLite/FwLiteWeb/Program.cs:1-34` | Console/Kestrel host | Starts `FwLiteWebServer.SetupAppServer`, optionally opens a browser, and listens on **stdin for the literal string `"shutdown"`** as a cross-platform substitute for SIGINT (`:24-31`, explicit comment: "Windows doesn't allow sending SIGINT to a process"). |
| `backend/FwLite/FwLiteProjectSync/Program.cs:1-118` | **`System.CommandLine` CLI** | `RootCommand("CRDT to FwData sync tool")` with global options `--crdt`, `--fwdata`, and three subcommands: `before-sync`, `after-sync` (both currently stub `Console.WriteLine` only, `:20-37`), and `sync` (`--create-crdt-dir`, `--dry-run`) which does a **real** CRDT↔FwData import/sync via `CrdtFwdataProjectSyncService` (`:38-85`). **This is the pre-existing CLI the brief said prior analysis missed.** |
| `backend/FixFwData/Program.cs:1-70` | Console app | `Main(string[] args)` takes a single positional `.fwdata` path argument, runs `FwDataFixer.FixErrorsAndSave()`, returns exit code 1 if errors were found (`:14-23`). No flags, no help text. |
| `backend/FwLite/LcmDebugger/Program.cs:1-29` | Top-level-statement console app | Opens a hardcoded downloaded CRDT project id and calls `SyncFwHeadlessProject` with a **mocked** `IServerHttpClientProvider` (`Moq`) — a throwaway debugging harness, not a product surface. |
| `backend/FwLite/FwLiteMaui/Platforms/{iOS,MacCatalyst}/Program.cs` | MAUI native bootstrap | Standard MAUI `UIApplication.Main`/similar — platform glue, not app logic. |
| `backend/FwLite/FwLiteMaui/build/Linq2DbCctorPatcher/Program.cs` | Build-time console tool | IL-patches a DLL; invoked from MSBuild targets in `FwLiteMaui.csproj:551-603`, not a runtime entry point. |

### Hosted/background services (VERIFIED, grepped `: BackgroundService` / `AddHostedService` across `backend/`)

| Service | Host | Behavior |
|---|---|---|
| `SyncHostedService` (`backend/FwHeadless/Services/SyncHostedService.cs:18-99`) | FwHeadless | Unbounded `Channel<Guid>` job queue; one `SyncWorker` per dequeued project id; caches recent results 30s in `IMemoryCache`; exposes `QueueJob`/`IsJobQueuedOrRunning`/`AwaitSyncFinished`. This **is** FwHeadless's job-queue mechanism — in-process, not backed by Postgres/Quartz/Hangfire. |
| `HgService` (`backend/LexBoxApi/Services/HgService.cs`, registered `LexBoxKernel.cs:75,77`) | LexBoxApi | `BackgroundService` + `IHgService` — hg repo maintenance/health (also backs the `hgweb` health check, `LexBoxKernel.cs:87`). |
| `SwaggerValidationService` (`LexBoxKernel.cs:97-105`) | LexBoxApi, dev only | Forces Swagger doc generation 5s after startup to catch schema errors early. |
| `DbStartupService` (`backend/LexData/DbStartupService.cs`) | LexData (used by LexBoxApi) | INFERRED from filename + `IHostedService` grep hit — applies/validates DB readiness at startup. |
| `BackgroundSyncService`, `PushListenerRecoveryService`, `UpdateChecker`, `OAuthService` (`backend/FwLite/FwLiteShared/Sync|Projects|AppUpdate|Auth/*.cs`) | FwLiteWeb & FwLiteMaui (shared) | Client-side background sync trigger, SignalR push-listener reconnect/recovery, app-update polling, OAuth flow host. |
| `NetworkChangeSyncTrigger` (FwLiteWeb), `ConnectivitySyncTrigger` (FwLiteMaui) | host-specific | Platform-specific network-change → trigger-sync glue. |

### Quartz scheduled jobs (VERIFIED, `backend/LexBoxApi/ScheduledTasksKernel.cs:1-62`, `backend/LexBoxApi/Jobs/*.cs`)

Persistent store is **Postgres** (`options.UsePostgres`, table prefix `quartz.QRTZ_`, `:28-32` — same DB as `LexBoxDbContext`). Jobs: `CleanupResetBackupJob` (cron `0 0 2 ? * 1` = weekly Sunday 2am), `DeleteTempDirectoryJob`, `UpdateProjectMetadataJob` (durable, triggered on demand from `ProjectController.QueueUpdateProjectMetadataTask`, `ProjectController.cs:341-342`), `RetryEmailJob` (durable). Admin UI mounted at `/api/quartz` via CrystalQuartz, gated by `AdminRequiredAttribute` (`Program.cs:186`).

---

## 3. HTTP / GraphQL / SignalR surface, by project

### 3.1 LexBoxApi — GraphQL (HotChocolate, `/api/graphql`, embedded Nitro UI at `/api/graphql/ui`, SDL at `/api/graphql/schema.graphql` — `Program.cs:182-184`)

**Queries** (`[QueryType]` class `LexQueries`, VERIFIED full read of `backend/LexBoxApi/GraphQL/LexQueries.cs`): `myProjects`, `projects` (admin-only, `withDeleted` flag), `myDraftProjects`, `draftProjects` (admin), `projectsByLangCodeAndOrg`, `projectsInMyOrg`, `projectById`, `projectStatus`, `projectByCode`, `orgs`, `myOrgs`, `usersICanSee` (offset-paged), `orgById`, `mediaFiles` (admin, offset-paged), `users` (admin, offset-paged), `me`, `orgMemberById`, `meAuth`, `testingThrowsError`.

**Mutations** — `ProjectMutations.cs`: `createProject`, `addProjectMember`, `bulkAddProjectMembers`, `changeProjectMemberRole`, `askToJoinProject`, `changeProjectName`, `changeProjectDescription`, `setProjectConfidentiality`, `setRetentionPolicy`, `updateProjectRepoSizeInKb`, `updateProjectLexEntryCount`, `updateProjectLanguageList`, `updateLangProjectId`, `updateFLExModelVersion`, `leaveProject`, `removeProjectMember`, `deleteDraftProject`, `softDeleteProject` (19 mutations).
`UserMutations.cs`: `sendFWLiteBetaRequestEmail`, `changeUserAccountBySelf`, `changeUserAccountByAdmin`, `sendNewVerificationEmailByAdmin`, `createGuestUserByAdmin`, `deleteUserByAdminOrSelf`, `setUserLocked` (7).
`OrgMutations.cs`: `createOrganization`, `deleteOrg`, `addProjectToOrg`, `addProjectsToOrg`, `removeProjectFromOrg`, `setOrgMemberRole`, `changeOrgMemberRole`, `leaveOrg`, `bulkAddOrgMembers`, `changeOrgName` (10).
`TestQueries.cs`: `isAdmin()`.

Total: 18 queries + 36 mutations (VERIFIED counts from method-signature greps).

### 3.2 LexBoxApi — REST controllers (VERIFIED, every `[Http*]` attribute in `backend/LexBoxApi/Controllers/*.cs` grepped)

| Controller | Route prefix | Notable endpoints |
|---|---|---|
| `AdminController` | `/api/admin` | `POST resetPassword` (admin) |
| `CrdtController` | `/api/crdt` | `{id}/get`, `{id}/add`, `{id}/changes`, `{id}/countChanges`, `listProjects`, `listProjectsV2`, `lookupProjectId`, `checkConnection` — **the server-side CRDT commit push/pull API**, gated by `[RequireScope(SendAndReceive)]` class-wide (`CrdtController.cs:22`); `Add` also fans out a SignalR notification via `CrdtProjectChangeHub` (`:48`) |
| `UserController` | `/api/[controller]` | `registerAccount` (anon), `acceptInvitation` (GET+POST), `sendVerificationEmail`, `currentUser` |
| `LoginController` | `/api/login` | `loginRedirect`, `google` (anon), `verifyEmail`, `POST /` (login), `refresh`, `logout`, `forgotPassword` (anon), `resetPassword` |
| `AuthTestingController` | `/api/[controller]` | `requires-auth`, `requires-admin`, `requires-forgot-password`, `requiresSendReceiveScope`, `403`, `requiresFwBetaFeatureFlag`, `requires-admin-and-sr-scope`, `token-project-count` |
| `ProjectController` | `/api/project` | `refreshProjectLastChanged`, `lastCommitForRepo`, `updateAllRepoCommitDates` (admin), `updateProjectType/{id}`, `setProjectType` (admin), `projectCodeAvailable/{code}`, `determineProjectType/{id}`, `updateProjectTypesForUnknownProjects`, `countProjectMatches`, `backupProject/{code}`, `resetProject/{code}` (admin), `finishResetProject/{code}` (admin), `DELETE {id}`, `hgVerify/{code}` (admin), `hgRecover/{code}` (admin), `sldr-export` (admin), `updateMissingLanguageList`, `updateMissingLangProjectId`, `queueUpdateProjectMetadataTask` |
| `FwLiteReleaseController` | `/api/fwlite-release` | `download-latest`, `latest`, `should-update`, `new-release` (all `[AllowAnonymous]`) — proxies GitHub Releases for the FwLite auto-updater |
| `LegacyProjectApiController` | `/api/user/{userName}/projects` | Legacy LanguageForge-classic project listing API (form-encoded) |
| `OauthController` | `/api/oauth` | `open-id-auth` (GET+POST), `token` (anon) — OpenIddict authorization/token endpoints |
| `TestingController` | `/api/[controller]` | `makeJwt`, `claims` (anon), `cleanupSeedData`, `testTurnstile`, `debugConfiguration` (anon), `copyToNewProject` (admin), `seedDatabase` (admin), `throwsException` (anon), `test500NoException` (anon), `test-cleanup-reset-backups` (admin), `pre-approve-oauth-app` — **test/dev-only surface, present in the shipped binary** |
| `FeedbackController` | `/api/feedback` | `fw-lite` (redirect to feedback form) |
| `SyncController` | `/api/fw-lite/sync` | `status/{projectId}`, `trigger/{projectId}`, `await-sync-finished/{projectId}`, `sync-harmony/{projectId}`, `regenerate-snapshot/{projectId}`, `block`, `unblock`, `block-status` — **this is LexBoxApi's proxy layer in front of FwHeadless's `/api/merge/*` routes** (confirmed by `FwHeadlessClient` HTTP client registered against `http://fwHeadless` in `LexBoxKernel.cs:63-64`) |
| `IntegrationController` | `/api/integration` | `openWithFlex`, `getProjectToken` — desktop-FieldWorks integration (launch via URI handler + token issuance) |

Plus non-controller minimal-API routes mapped directly in `Program.cs`: `MapSecurityTxt()`, `/api/tus-test` and `/api/project/upload-zip/{code}` (tus resumable-upload endpoints, admin-gated), `MapHub<CrdtProjectChangeHub>("/api/hub/crdt/project-changes")`, `/login` (401 stub to break redirect loops), `MapFileUploadProxy()`, `MapSyncProxy(...)` (mounts the hg-wire-protocol proxy **inside** LexBoxApi as well as in the standalone `SyncReverseProxy` service — INFERRED two call sites of the same `AddSyncProxy`/`MapSyncProxy` extension, not verified why both exist; plausibly SyncReverseProxy is the externally-facing hg endpoint and this one is a fallback/shared code path), `MapLfClassicApi()` (legacy public API surface).

### 3.3 SignalR hubs

Only one, VERIFIED: `CrdtProjectChangeHub` (`backend/LexBoxApi/Hub/CrdtProjectChangeHub.cs:7-16`), mounted at `/api/hub/crdt/project-changes`, one method `ListenForProjectChanges(Guid projectId)` (asserts download permission, joins a `project-{id}` SignalR group). Server pushes `OnProjectUpdated(projectId, clientId)` to that group whenever `CrdtController.Add` receives new commits (`CrdtController.cs:48`) — this is the live multi-client "someone else changed this project" signal that FwLite's `PushListenerRecoveryService` consumes.

### 3.4 FwHeadless — HTTP surface (VERIFIED, full read of `Program.cs`, `Routes/MergeRoutes.cs`, `Routes/MediaFileRoutes.cs`, `Controllers/MediaFileController.cs`)

- `/api/merge/execute` (POST) — enqueue a sync job (`SyncHostedService.QueueJob`), fails fast if project blocked or Lexbox auth fails.
- `/api/merge/sync-harmony` (POST) — Harmony-only sync (used for snapshot recovery, `MergeRoutes.cs:80-101`).
- `/api/merge/regenerate-snapshot` (POST) — rebuild the project snapshot, optionally at a specific historical commit (`preserveAllFieldWorksCommits` flag).
- `/api/merge/status` (GET) — computed `ProjectSyncStatus` (Syncing / QueuedToSync / NeverSynced / ReadyToSync with pending-commit counts both directions).
- `/api/merge/await-finished` (GET) — long-poll on job completion with typed error results (`SyncJobStatusEnum`: `SyncJobNotFound`, `TimedOutAwaitingSyncStatus`, `SyncJobTimedOut`, `CrdtSyncFailed`, …).
- `/api/merge/block`, `/api/merge/unblock`, `/api/merge/block-status` (POST/POST/GET) — manual circuit-breaker per project (used after a detected Mercurial rollback, see §8).
- `/api/media/list/{projectId}`, `/api/media/metadata/{fileId}`, `GET/PUT/POST/DELETE /api/media/{fileId}` — LinkedFiles media CRUD, backed by Postgres `MediaFile`/`FileMetadata` rows + files on the `fw-headless` PVC.
- `DELETE /api/manage/repo/{projectId}`, `DELETE /api/manage/project/{projectId}` — admin cleanup (`Program.cs:88-101`).
- `/healthz` (via `MapDefaultEndpoints()` from `WebServiceDefaults`), OpenAPI+Scalar UI in dev only.

### 3.5 FwLiteWeb — local HTTP surface (VERIFIED, `grep` over `backend/FwLite/FwLiteWeb/Routes/*.cs`)

`/api/activity/{project}` (`/`, `/authors`, `/change-types`), `/api/test/{project}` (`/entries`, `/set-entry-note`, `/add-new-entry` — dev/test scaffolding shipped in the binary), `/api/history/{project}` (`/snapshot/{snapshotId}`, `/snapshot/commit/{commitId}`, `/{entityId}`), `/api/remoteProjects`, `/api/localProjects`, `/api/project/create`, `/api/project/create-demo`, `/api/upload/crdt/{serverAuthority}/{project}`, `/api/download/crdt/{serverAuthority}/{code}`, `DELETE /api/crdt/{code}`, `/api/fw/{fwDataProject}/link/entry/{id}` (FieldWorks-desktop deep link), `/api/feedback/fw-lite`, `/api/{projectType}/{projectCode}/...` (a whole `MiniLcmRoutes` group: `writingSystems`, `entries`, `entries/{search}`, `entry/{id}`, `sense/{id}`, `entry/{id}/index`, `parts-of-speech`, `semantic-domains`, `publications`, `POST entry`, `DELETE entry/{id}` — the generic MiniLcm read/write API, dispatched to whichever backend (`fwdata`/`crdt`) the `projectType` segment names), `/api/auth/{servers,login/{authority},login-web-view/{authority},oauth-callback,me/{authority},logout/{authority}}`, `/api/import/fwdata/{fwDataProjectName}`.

This is the **same** local HTTP API surface the `platform.bible-extension` consumes (`src/utils/fw-lite-api.ts`, VERIFIED file exists) and that the embedded viewer SPA talks to over `localhost`.

---

## 4. Storage inventory

| Store | Technology | Physical location | Owner/DbContext | Notes |
|---|---|---|---|---|
| Main Lexbox DB | PostgreSQL | k8s: single `db` Deployment/Service (`deployment/base/db-deployment.yaml:1-85`), PVC `db-data` 10Gi (`deployment/base/pvc.yaml:19-35`); local: `postgres:15-alpine` (dev/CI) | `LexBoxDbContext` (`backend/LexData/LexBoxDbContext.cs:10,30-37`: `Files`,`Users`,`Projects`,`ProjectUsers`,`DraftProjects`,`Orgs`,`OrgMembers`,`OrgProjects`) + `ServerCommit`/`Commit` sets (Harmony server-side commit log) + Quartz's own `quartz.QRTZ_*` tables (same connection string) + OpenIddict's EF store (also same DbContext, package ref `OpenIddict.EntityFrameworkCore`) | **VERIFIED single shared Postgres instance** — FwHeadless and LexBoxApi both connect to it (`DbConfig__LexBoxConnectionString` env var identical pattern in both deployments, `deployment/base/fw-headless-deployment.yaml:95-96`). 96 non-snapshot migration files (VERIFIED count). |
| CRDT project store | SQLite (one file per project) | FwHeadless: `{ProjectStorageRoot}/{code}-{id}/crdt.sqlite` (`FwHeadless/FwHeadlessConfig.cs:26-33,73-76`); FwLite desktop: `{LcmCrdtConfig.ProjectPath}/{code}.sqlite` (`LcmCrdtConfig.cs:8,14`, default `Path.GetFullPath(".")`) | `LcmCrdtDbContext` (`backend/FwLite/LcmCrdt/LcmCrdtDbContext.cs:19-35`: `ProjectData`,`WritingSystems`,`Entries`,`ComplexFormComponents`,`ComplexFormTypes`,`MorphTypes`,`Senses`,`ExampleSentences`,`SemanticDomains`,`PartsOfSpeech`,`Publications`,`CustomViews`,`CommentThreads`,`UserComments`,`UnreadComments`) plus `SIL.Harmony`'s own `Commit`/`Snapshot` tables via `modelBuilder.UseCrdt()` (`:39`) | 48 non-snapshot migrations (VERIFIED count). This is a **local, file-based, per-project** store — every FwLite install and every FwHeadless project folder has its own independent SQLite file; there is no shared CRDT database. |
| FieldWorks project data | LibLCM `.fwdata` (binary/XML hybrid) + Mercurial repo | FwHeadless: `{ProjectStorageRoot}/{code}-{id}/fw/{code}.fwdata` on the `fw-headless` PVC (10Gi, `deployment/base/pvc.yaml:39-53`); classic Mercurial repos: `hg-repos` PVC 10Gi mounted at `/var/hg/repos` in the `hg` deployment (`deployment/base/hg-deployment.yaml:137-146`) | n/a (file, not a DbContext) | Two separate PVCs for what is conceptually one Mercurial repo per project: hgweb's canonical copy (`hg-repos`) and FwHeadless's working clone + CRDT/snapshot sidecars (`fw-headless`). |
| Legacy Language Forge data | MongoDB | External Mongo cluster (connection string not found in this repo's deployment manifests — INFERRED external/managed, not provisioned by `deployment/`) | `SystemDbContext` (db `scriptureforge`, collections `users`,`projects`) and `ProjectDbContext` (per-project db `sf_{projectCode}`, collections `lexicon`,`optionlists`) — both VERIFIED, `backend/LfClassicData/{System,Project}DbContext.cs` | Read/write path for old (`.lift`-less) Language Forge projects that never migrated to FieldWorks/CRDT. |
| Media files | Postgres rows (`MediaFile`/`FileMetadata`) + files on disk | Same `fw-headless` PVC, under `{project}/fw/LinkedFiles/...` | `LexBoxDbContext.Files` | `MediaFileService.SyncMediaFiles` reconciles the on-disk `LinkedFiles` tree with the DB row set both ways (added/removed) on every sync (`backend/FwHeadless/Media/MediaFileService.cs:21-59`). |
| Local resource/media cache (FwLite client) | Filesystem | `{LcmCrdtConfig.ProjectPath}/localResourcesCache` (`LcmCrdtKernel.cs:69-72`) | n/a | Client-side cache for CRDT-synced media blobs. |
| In-memory cache | `IMemoryCache`/`HybridCache` | Process memory | n/a | LexBoxApi uses `AddHybridCache()` (`Program.cs:86`); FwHeadless caches recent sync results 30s (`SyncHostedService.cs:90-98`). |
| Quartz job store | Postgres (same DB, `quartz.` prefix) | — | — | See §2. |

---

## 5. Deployment — exact artifacts, exact targets

All 16 files under `.github/workflows/` were opened in full; the summary below is exhaustive, not a sample.

### 5.1 Reusable build workflows (each is `workflow_call` + often also directly triggered)

| Workflow | Builds | Requires Docker? | Runner(s) |
|---|---|---|---|
| `lexbox-api.yaml` | `LexBoxOnly.slnf` build+test (with a Postgres service container), then `docker/build-push-action` → `ghcr.io/sillsdev/lexbox-api` | Yes (image build; Postgres via GH Actions `services:`) | `ubuntu-latest` |
| `lexbox-ui.yaml` | pnpm install, vitest, then docker build → `ghcr.io/sillsdev/lexbox-ui` (context `frontend`) | Yes | `ubuntu-latest` |
| `lexbox-hgweb.yaml` | docker build only (no app build step) → `ghcr.io/sillsdev/lexbox-hgweb` (context `hgweb`); also self-triggers on push/PR to `hgweb/**` | Yes | `ubuntu-latest` |
| `lexbox-fw-headless.yaml` | `dotnet build`+test `FwHeadless.csproj`/`Testing.csproj` (Postgres service), then docker build (`file: backend/FwHeadless/Dockerfile`) → `ghcr.io/sillsdev/lexbox-fw-headless` | Yes | `ubuntu-latest` |
| `deploy.yaml` | No build — checks out this repo *and* a separate **fleet repo** (`vars.FLEET_REPO`, SSH-keyed), runs `kubectl kustomize` on `deployment/{env}`, writes `resources.yaml` into the fleet repo, bumps image tag via `yq`, commits+pushes to the fleet repo (GitOps), then polls `https://{domain}/api/healthz` and `/healthz` until the `lexbox-version` response header matches the deployed version | No (kubectl/kustomize only; the actual apply happens via the fleet repo's own GitOps controller — **not run from this workflow**) | `ubuntu-latest`, except `verify-published` uses **`self-hosted`** when `k8s-environment == develop` |

### 5.2 FwLite (`fw-lite.yaml`) — the only workflow that builds non-container artifacts

Jobs (VERIFIED, full file read): `build-and-test` (Linux, `FwLiteCore.slnf`, also runs `task fw-lite:has-pending-model-changes`/`has-stale-generated-types` drift checks) → `benchmark` (develop-push or manual only) → `frontend` (builds `frontend/viewer`, uploads `fw-lite-viewer-js` artifact consumed by every publish job) → `frontend-component-unit-tests` (vitest+Playwright component tests) → four parallel **publish** jobs → `create-release` (main branch only) → `e2e-test` (spins up a full k8s cluster via `.github/actions/setup-k8s`, publishes a real `linux-x64` `FwLiteWeb` single-file binary, and Playwright-drives it against that cluster).

| Publish job | Runner | Artifact | Command |
|---|---|---|---|
| `publish-mac` | `macos-latest` | osx-x64 + osx-arm64 self-contained build (not single-file — `PublishSingleFile=false` in Release, `FwLiteWeb.csproj:743-747`) | `dotnet publish -r osx-{x64,arm64}` |
| `publish-linux` | `ubuntu-latest` | `linux-x64`/`linux-arm64` **single-file** executable, smoke-tested (`timeout 10s ./FwLiteWeb`) | `dotnet publish -r linux-{x64,arm64} -p:PublishSingleFile=true` |
| `publish-android` | `ubuntu-latest` | signed `.apk`/`.aab` | `dotnet workload install maui-android`; `dotnet publish -f net10.0-android` with a keystore decoded from `secrets.FW_LITE_KEYSTORE_BASE64` |
| `publish-win` | `windows-latest` | MAUI portable folder + `.msixbundle`, code-signed via `sillsdev/codesign/trusted-signing-action` (develop/main only) | `dotnet workload install maui-windows`; builds+tests `FwLiteMaui.Tests`; `dotnet publish -f net10.0-windows10.0.19041.0` twice (portable, then MSIX) + `MakeAppx.exe bundle` |

**F2 — no iOS/MacCatalyst MAUI publish job exists**, even though `FwLiteMaui.csproj:443` conditionally adds those TFMs on macOS build hosts. Only `publish-mac` exists and it publishes **`FwLiteWeb`** (the ASP.NET host), not `FwLiteMaui` for those TFMs. iOS/MacCatalyst MAUI is buildable locally on a Mac dev machine but is **not part of the release pipeline**.

`create-release` (`fw-lite.yaml:516-570`, `main` only) bundles all four artifacts into one GitHub Release tagged `vYYYY-MM-DD-{sha}`, then pings `POST https://lexbox.org/api/fwlite-release/new-release` to bust `FwLiteReleaseController`'s cache.

### 5.3 Orchestration workflows

- `release-pipeline.yaml` (push to `main`): builds all four server images in parallel → deploys to **staging** → runs `integration-test.yaml` as a **2×2 matrix** (`ubuntu-latest`/`windows-latest` × Mercurial `3`/`6`) → deploys to **production** even if integration tests fail (explicit `if:` allows `failure()`, comment: "currently flaky") → creates a GitHub Release.
- `deploy-branch.yaml` (`workflow_dispatch` only): manual one-off deploy of the current branch to the `develop` k8s environment.
- `develop-api.yaml` / `develop-ui.yaml` / `develop-fw-headless.yaml`: per-component push/PR-to-`develop` CI+CD, each building only its own image and deploying only that image's tag into `develop` (path-filtered so an FwLite-only change does not rebuild/redeploy the API image, and vice versa — `develop-api.yaml:6-7` explicitly excludes `backend/FwLite/**`).
- `integration-test.yaml` / `integration-test-gha.yaml`: `.NET` integration tests (`Category=Integration`/`FlakyIntegration`) against either a real staging/prod deployment (via secrets/vars for hostnames) or a throwaway local kind cluster (`setup-k8s` action) with Playwright tests included in the GHA variant.
- `codeql.yml`: CodeQL for `csharp`, `javascript-typescript`, `actions` — no build (build-mode `none`), weekly cron + push/PR on code-file globs.
- `platform.bible-extension.yaml`: lint-only (format+eslint+stylelint) against a checkout of a **sibling** `paranext/paranext-core` repo for shared sub-packages.
- `package-cleanup.yaml`: manual-dispatch GHCR package-version pruning (keeps 10, untagged only) for `lexbox-ui`/`lexbox-api`/`lexbox-hgweb` — **`lexbox-fw-headless` is not in the cleanup matrix** (minor drift, F3).
- `labeler.yaml`: not read in depth (PR auto-labeling, no build/deploy implications).

### 5.4 What requires Docker vs. what does not

- **Requires Docker (server images, Linux containers only):** `lexbox-api`, `lexbox-ui`, `lexbox-hgweb`, `lexbox-fw-headless`. All four `Dockerfile`s use `mcr.microsoft.com/dotnet/{aspnet,sdk}:10.0` or, for hgweb, an Apache-based image (not opened in depth — out of the FwLite/FwHeadless focus of this brief).
- **Does not require Docker (dotnet-publish artifacts):** FwLiteWeb (linux/osx self-contained single-file or folder), FwLiteMaui (Android APK/AAB, Windows MSIX/portable). **`backend/FwLite/FwLiteWeb/Dockerfile` exists in the repo but is orphaned** — no workflow, no `deployment/` manifest, and no `docker build --file` reference to it anywhere (VERIFIED, `grep -rl "FwLiteWeb/Dockerfile"` across `.github` and `deployment` returns nothing). It looks like a leftover from an earlier "FwLiteWeb as a hosted container" design that was superseded by the desktop-single-file-binary distribution model — **F1**.
- **Kubernetes/Kustomize** (`deployment/base` + overlays `develop`/`staging`/`production`/`local-dev`): five Deployments (`db`, `hg`, `fw-headless`, `lexbox`—not read in depth here—, `ui`—not read in depth here) + PVCs (`hg-repos`, `db-data`, `fw-headless`) + a shared `app-config`/`secrets` ConfigMap/Secret set, all under namespace `languagedepot`.

---

## 6. Auth / permissions

### 6.1 Mechanism (VERIFIED, `backend/LexBoxApi/Auth/AuthKernel.cs`)

- Cookie **and** JWT bearer, unified under a `"JwtOrCookie"` default scheme (`AuthKernel.cs:30`).
- OpenIddict (`OpenIddict.AspNetCore`/`.EntityFrameworkCore`/`.Quartz`) issues/validates tokens against the same Postgres DB; `/api/oauth/{open-id-auth,token}` are the OpenIddict endpoints (`OauthController.cs`).
- Fallback authorization policy requires auth on **every** endpoint unless explicitly `[AllowAnonymous]` (`AuthKernel.cs:51-56`, comment confirms this is deliberate default-deny).
- Custom policies/attributes (all VERIFIED in `backend/LexBoxApi/Auth/Attributes/*.cs` + `AuthKernel.cs`): `[AdminRequired]`, `[RequireScope(...)]` (checks `LexboxAuthScope` claims), `[FeatureFlagRequired(...)]`, `[VerifiedEmailRequired]`, `[RequireAudience]`, `[RequireCurrentUserInfo]`, plus named policies for media-file upload/download and the sync-proxy's `UserHasAccessToProjectRequirement`.
- Google OAuth login supported (`LoginController.cs:63`, `AddAuthenticationGoogle`).
- Turnstile (Cloudflare) bot-check on registration (`TurnstileService`, `CloudFlareConfig`).

### 6.2 Roles/scopes (VERIFIED enums)

- `UserRole` (`LexCore/Auth/LexAuthUser.cs:362-367`): `admin`, `user` — **site-wide** role, only two values.
- `ProjectRole` (`LexCore/Entities/ProjectRole.cs`): `Unknown`, `Manager`, `Editor`, `Observer` (note: `Admin=1` is commented out — projects have no per-project admin distinct from Manager).
- `OrgRole` (`LexCore/Entities/Organization.cs:35-40`): `Unknown`, `Admin`, `User`.
- `FeatureFlag` (`LexCore/FeatureFlag.cs`): currently only `FwLiteBeta`.
- `LexboxAuthScope` (`LexCore/Auth/LexboxAuthScope.cs`): `openid`,`profile`,`email` (standard OIDC) + app scopes `LexboxApi`, `RegisterAccount`, `ForgotPassword`, `SendAndReceive`, `SendAndReceiveRefresh`.

### 6.3 Enforcement point (VERIFIED, full read of `backend/LexBoxApi/Services/PermissionService.cs`, 307 lines)

`PermissionService : IPermissionService` is the **single** authorization decision point for project/org actions, injected wherever needed (GraphQL resolvers, controllers, the SignalR hub). Every rule is symmetric `CanX`/`AssertCanX` pair. Key rules, all VERIFIED by line:
- Site `admin` bypasses every check (`:42,58,67,91,108,121,134,167,179,193,223,228,235,255,276,294`).
- Org **admins** can view/sync/manage every project owned by their org, including confidential ones (`ManagesOrgThatOwnsProject`, `:15-26`, used by `CanSyncProject`,`CanDownloadProject`,`CanViewProject`,`CanViewProjectMembers`,`CanManageProject`,`CanCreateGuestUserInProject`).
- Project membership role gates: `CanSyncProject` requires `Editor`/`Manager` (`:59`); `CanManageProject` requires `Manager` (`:135`); plain `CanDownloadProject`/`CanViewProject` require any membership.
- Confidentiality: non-members can view a project only if `IsConfidential == false`; **null confidentiality defaults to private** (`:96`, comment "Private by default") except for the *member-visibility* check specifically, which defaults to **public** unless explicitly private (`:127-128`, comment flags this as "In this specific case (only)").
- Self-action guards: can't lock/unlock your own account (`:217-218`), can't change your own project role (`:160-161`).
- `HasProjectRequestPermission` denies users created by an admin or with an unverified/missing email (`:245`).

No API-key/service-account concept was found for machine-to-machine calls other than FwHeadless's own Lexbox user credentials (`FwHeadlessConfig.LexboxUsername`/`LexboxPassword`, k8s secret `CRDT_MERGE_SEND_RECEIVE_{USERNAME,PASSWORD}` — FwHeadless authenticates to LexBoxApi as a regular, presumably admin-or-project-member, user).

---

## 7. The FwLite sub-cluster in detail

### 7.1 Layering (VERIFIED via `ProjectReference`s in every `.csproj`)

```
MiniLcm  (abstraction: IMiniLcmApi / models / JSON-Patch update contracts / query options)
  ├── LcmCrdt              (CRDT backend: SQLite + SIL.Harmony; Comments, CustomViews, History/Activity, media)
  ├── FwDataMiniLcmBridge  (LibLCM backend: SIL.LCModel reads/writes .fwdata)
  └── LfClassicData        (Mongo backend, lives in backend/, not backend/FwLite/)
FwLiteProjectSync  (depends on LcmCrdt + FwDataMiniLcmBridge — the CRDT↔FwData sync engine + its CLI)
FwLiteShared       (depends on LcmCrdt + MiniLcm; app-level services: auth, SignalR client, sync trigger, updater)
FwLiteWeb          (depends on FwLiteProjectSync + FwDataMiniLcmBridge + FwLiteShared + LcmCrdt + MiniLcm — always, unconditionally)
FwLiteMaui         (depends on FwLiteShared + LcmCrdt + MiniLcm always; FwDataMiniLcmBridge + FwLiteProjectSync ONLY on Windows TFM)
```

### 7.2 Conditional compilation / conditional references (VERIFIED)

- `FwLiteMaui.csproj:463-465`: `<IncludeFwDataBridge>false</IncludeFwDataBridge>`, flipped to `true` **only** `Condition="...GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'"`. This drives `#if INCLUDE_FWDATA_BRIDGE` in `FwLiteMauiKernel.cs:58-63` and `Services/FwLinker.cs`, which conditionally call `FwDataBridgeKernel.AddFwDataBridge`/`FwLiteProjectSyncKernel.AddFwLiteProjectSync` and register `IFwLinker`. **Net effect: FwLite on Android/iOS/MacCatalyst can never open a local `.fwdata` file or talk to desktop FieldWorks — only CRDT/server projects. Only the Windows MAUI build and the desktop `FwLiteWeb` host (any OS) can.**
- `FwLiteMaui.csproj:442-444`: `TargetFrameworks` are themselves conditional — Android only if `BuildAndroid != false`; iOS/MacCatalyst only `IsOSPlatform('osx') And BuildApple != false`; Windows only `IsOSPlatform('windows')`. This is *why* CI builds/tests FwLite MAUI only on `windows-latest` (`fw-lite.yaml:422-465`) — Linux CI hosts physically cannot produce iOS/Android/Windows MAUI heads in the same job (Android is cross-compiled fine on Linux in principle, but this repo's CI gates it to the Windows job alongside the Windows-only tests).
- `FwLiteMaui.csproj:548-603`: Android-only MSBuild targets patch a `linq2db.EntityFrameworkCore` static constructor via the separate `Linq2DbCctorPatcher` tool, gated by a version-pin check against `Directory.Packages.props` (fails the build loudly if the pin drifts outside `10.3.x`/`10.4.x` — tracked as issue #2291).
- `FwLiteWeb.csproj:776-795`: macOS-only `<ItemGroup Condition="IsOsPlatform('macOS')">` bundles `libicu*.dylib` from MacPorts or Homebrew paths — INFERRED this is why FwLiteWeb needs real macOS build hosts (not cross-compiled) for the mac publish job.
- Harmony references are switchable repo-wide via `backend/Harmony.{App,Core,Linq2db}.References.props`: if `UseHarmonySource=true` (with `HarmonySourcePath` set), the *source* `SIL.Harmony*` projects from a sibling `harmony` checkout are referenced instead of the NuGet packages — this is how a developer working across both repos (as in this session, `C:\Users\johnm\Documents\repos\harmony`) can co-develop Harmony changes. Default is the NuGet package.

### 7.3 What runs where — summary table

| Component | Windows desktop | Linux desktop | macOS desktop | Android | iOS/MacCatalyst | Server (FwHeadless) |
|---|---|---|---|---|---|---|
| MiniLcm abstraction | yes | yes | yes | yes | yes | yes |
| LcmCrdt (SQLite+Harmony) | yes | yes | yes | yes | yes | yes |
| FwDataMiniLcmBridge (LibLCM) | yes (FwLiteWeb + MAUI-Windows) | yes (FwLiteWeb only) | yes (FwLiteWeb only) | **no** | **no** | yes |
| FwLiteProjectSync (sync engine + CLI) | yes | yes | yes | **no** | **no** | yes |
| Host app | FwLiteWeb or FwLiteMaui-Windows | FwLiteWeb only | FwLiteWeb only | FwLiteMaui | FwLiteMaui (buildable, not released — F2) | FwHeadless |
| Auto-update / OAuth / SignalR client | FwLiteShared | FwLiteShared | FwLiteShared | FwLiteShared | FwLiteShared | n/a |

### 7.4 Tests

`FwLiteCore.slnf` (Linux CI) covers MiniLcm.Tests, LcmCrdt.Tests, FwDataMiniLcmBridge.Tests, FwLiteProjectSync.Tests, FwLiteShared.Tests — 773 `[Fact]`/`[Theory]` combined (VERIFIED count, may include `[Theory]` methods with multiple `[InlineData]` rows counted once each as a method, not as expanded cases). `FwLiteMaui.Tests` (6 methods) only builds/runs on the Windows `publish-win` job. `FwLiteProjectSync.Tests` also contains `Category=Benchmark` tests excluded from the normal test run and executed separately in Release mode only on `develop`/manual dispatch.

---

## 8. FwHeadless in detail

### 8.1 What it does, end to end

FwHeadless is the server-side agent that keeps three representations of one FieldWorks project in sync: (1) the canonical Mercurial repo (via hgweb), (2) a local `.fwdata` LibLCM working copy, and (3) a CRDT/Harmony SQLite database whose commits are also mirrored to Lexbox's Postgres (`ServerCommit` table). Its core loop (`SyncWorker.ExecuteSync`, `backend/FwHeadless/Services/SyncHostedService.cs:119-277`, fully read):

1. Fail fast if it cannot authenticate to Lexbox (`CrdtHttpSyncService.TestAuth`).
2. Check the project isn't `Blocked` (manual circuit breaker, see 8.3).
3. `SetupFwData`: if no local `.fwdata` clone exists, **clone** it via Chorus (`SendReceiveHelpers.CloneProject`); if pending commits exist, **Send/Receive** via Chorus first, and clean up the partial clone on failure (`SyncHostedService.cs:279-345`).
4. Sync media files from the FieldWorks `LinkedFiles` folder into the media DB (`MediaFileService.SyncMediaFiles(LcmCache)`), always, even with no pending S&R.
5. Open/create the CRDT project, sync it against Harmony (server commits) — **but only if the previous merge succeeded**, to avoid pushing a partial sync (`HasSyncedSuccessfully` check, `:200`).
6. Sync CRDT-tracked media resources the other direction (`MediaFileService.SyncMediaFiles(Guid, LcmMediaService)`).
7. Import-or-Sync the CRDT state against the FwData snapshot (`CrdtFwdataProjectSyncService.Import`/`Sync`) — first-time-seen project vs. incremental.
8. If that produced FwData-side changes, Send/Receive again (once, with one retry on HTTP 500) to push them back to Mercurial.
9. **Only after confirming the push succeeded**, regenerate the project snapshot (so a rolled-back push can never corrupt the snapshot) and push new CRDT commits to Harmony.

A Mercurial **rollback** detected in Chorus output (`"Rolling back..."` string match, `SendReceiveHelpers.cs:39-41`) causes the project to be **blocked from further syncing** (`ProjectMetadataService.BlockFromSyncAsync`) rather than silently retried — an explicit safety valve.

### 8.2 Job queue mechanics

In-process only: `SyncHostedService` is an unbounded `Channel<Guid>` consumed by a single `await foreach` loop (effectively one job at a time, sequential; not parallelized across projects) plus a `ConcurrentDictionary<Guid, TaskCompletionSource<SyncJobResult>>` used both for de-duplication (`QueueJob` no-ops if already queued) and for callers to `await` completion (`AwaitSyncFinished`). Results are cached 30s post-completion so a client polling slightly late still gets the answer. **No persistence** — a FwHeadless restart loses any in-flight/queued job state (acceptable because the client-facing `/api/merge/execute` is idempotent-safe to re-trigger).

### 8.3 Chorus / Mercurial / `chorusmerge`

- Uses `Chorus.VcsDrivers.Mercurial.HgRunner` directly for `hg incoming`/`hg outgoing` pending-commit counts and for manual `hg add`/`hg commit` when committing an uploaded media file (`SendReceiveHelpers.cs:115-190`).
- Uses `LfMergeBridge.LfMergeBridge.Execute("Language_Forge_Send_Receive"/"Language_Forge_Clone", ...)` for the actual Send/Receive/Clone operations (`SendReceiveHelpers.cs:73-83,192-237`) — this is the same bridge FLExBridge/LfMerge used historically.
- The Docker image (`backend/FwHeadless/Dockerfile`, fully read) bundles: Mercurial binaries + extensions (`Mercurial/`, `MercurialExtensions/`, copied via MSBuild `Content Include`), the `chorusmerge` executable (copied via a `<None Include="chorusmerge">` link plus its `.NET 8` runtimeconfig — **`chorusmerge` runs on .NET 8 while FwHeadless itself runs on .NET 10**, so the image layers in a `dotnet/runtime:8.0` stage just for `chorusmerge`'s `fxr`/shared-framework files, `Dockerfile:20,29-30`), and runs as non-root `www-data:33`.
- `FixFwData` is co-published into the same container (see §1) and smoke-tested at image-build time by fixing a real `.fwdata` extracted from `Testing/test-template-repo.zip` (`Dockerfile:36-45`) — build fails if `FixFwData` crashes with exit code >1.

### 8.4 Media storage

Covered in §4/§3.4 — Postgres `MediaFile`/`FileMetadata` rows plus files under each project's `fw/LinkedFiles/` tree on the `fw-headless` PVC; bidirectional reconciliation both against the raw filesystem (`SyncMediaFiles(LcmCache)`) and against Harmony-tracked CRDT media resources (`SyncMediaFiles(Guid, LcmMediaService)`).

### 8.5 Harmony/LcmCrdt integration

FwHeadless references `LcmCrdt` and `FwLiteProjectSync` directly (`FwHeadless.csproj:35-37`) and imports `Harmony.App.References.props` (`:33`) — i.e. **FwHeadless links the full `SIL.Harmony` CRDT substrate in-process**, not through a network call to some other Harmony-hosting service. `CrdtSyncService.SyncHarmonyProject()` (used from both `MergeRoutes.SyncHarmonyProject` and `SyncWorker.ExecuteSync`) pushes/pulls Harmony commits against Lexbox's `CrdtController` HTTP API — the same API real FwLite desktop clients use. This is the concrete evidence for "FwHeadless hosts Harmony" that the brief said prior analysis missed.

---

## 9. Analysis (the 15%)

### 9.1 Capabilities reusable as-is for a "propose → review effects → approve" product

- **`MiniLcm.IMiniLcmWriteApi` + `UpdateObjectInput<T>`** (`MiniLcm/IMiniLcmWriteApi.cs`) already models every lexicon mutation as a typed, JSON-Patch-backed operation with a `Set`/`Add`/`Remove` fluent builder. A "propose a change" surface can be built by capturing an `UpdateObjectInput<T>` (or the equivalent Create/Delete call) **without executing it**, and rendering a diff from `Patch` before commit.
- **`SIL.Harmony` change/commit model already used by `LcmCrdt`** gives every mutation a `Commit`/`ChangeEntity<IChange>` with author, timestamp, and a reversible/replayable change log (`LcmCrdt/HistoryService.cs`). A "review effects" UI has a real substrate to query: `HistoryService` already resolves a change to a human `Subject`/`Target`/owning-entry, and groups changes per commit (`ActivityChangeInfo`, `ProjectActivity`, fully read).
- **CommentThread/UserComment model** (`MiniLcm/Models/Comments.cs`, `IMiniLcmReadApi`/`WriteApi` comment methods) is **already a review/discussion substrate**: threads keyed to an entry/sense/example, `ThreadStatus.Open/Closed`, per-comment author/timestamps, unread tracking (`GetUnreadComments`, `CountUnreadComments`, `MarkCommentRead`/`MarkAllCommentsRead`). This is close to an "approve/reject with discussion" primitive already wired into the CRDT backend and the viewer UI (`frontend/viewer/src/lib/activity`, `.../history`, `.../components` — directories exist, not fully audited here).
- **The server-side CRDT sync API (`CrdtController`) + `CrdtProjectChangeHub` SignalR push** already gives multi-client "your view is stale, refresh" notification for free — a review workflow spanning multiple reviewers would not need new push infrastructure.
- **`FwLiteProjectSync`'s CLI** (`before-sync`/`after-sync`/`sync`) is a real, if minimal, non-interactive automation entry point already wired to the same sync engine FwHeadless uses — a batch "apply approved change-set" job could plausibly be scripted through it or its underlying `CrdtFwdataProjectSyncService`/`ProjectSnapshotService` calls rather than reinventing sync.
- **`ProjectSnapshotService`** (used throughout FwHeadless and referenced by the CLI) already captures before/after project state and is explicitly designed to be regenerated only after a push is confirmed — i.e., there is already a notion of "the last known-good state" to diff proposed changes against.

### 9.2 What exists but would need modification

- `IMiniLcmWriteApi`'s "Submit*" fire-and-forget variants (`#region Submit`) are explicitly built for sync's own conflict semantics ("delete wins"), not for a human review gate — a propose/approve flow would need its own variant that neither applies immediately (FwData) nor blind-submits (CRDT sync) but stages the patch for later application.
- `ThreadStatus` is binary (`Open`/`Closed`) with no `Approved`/`Rejected`/`ChangesRequested` semantics — the comment substrate is a discussion thread, not a change-approval workflow; extending it (or building a parallel "proposal" entity that references a `CommentThread`) would be needed.
- `PermissionService` has no notion of a reviewer/approver role distinct from `Manager`/`Editor` — `ProjectRole` would need a new value (or a claims-based reviewer flag) if approval authority should differ from edit authority.
- FwHeadless's sync loop assumes it is always safe to apply CRDT changes straight through to FwData once a sync fires (`SyncWorker.ExecuteSync`); a "hold pending approval" step would need to sit *before* `CrdtFwdataProjectSyncService.Sync` is called, likely by filtering which commits are eligible to sync rather than changing the sync engine itself.
- No dry-run/preview endpoint currently returns a diff without applying it anywhere in the HTTP surface enumerated in §3 (FwLiteProjectSync's CLI `--dry-run` flag is the closest existing analogue, but it is a local/offline tool, not a server API).

### 9.3 Likely attachment seams for a new capability

1. **`MiniLcm.IMiniLcmWriteApi`** itself, as a decorator/wrapper implementation that intercepts calls, stages them, and only forwards to the real backend on approval — this is the natural seam because every existing UI (viewer SPA) and every existing backend (CRDT, FwData, LfClassic) already goes through this one interface.
2. **`LcmCrdt`'s Harmony commit stream** as the audit/diff source — `HistoryService`/`ActivityQuery` already answer "what changed, by whom, when"; a review UI could be built almost entirely by querying this rather than a new event log.
3. **`CommentThread`/`UserComment`** as the discussion/approval-conversation attachment point, referenced from a new "proposal" concept keyed the same way (`SubjectType`/`SubjectId`).
4. **`FwLiteWeb`'s route layer** (`Routes/*.cs`, all thin static classes mapping to service calls) is a low-friction place to add new endpoints — the pattern (`MapGroup` → `MapGet/Post` → typed handler with DI params) is uniform across the whole codebase and easy to extend without touching sync/storage internals.
5. **`SyncHostedService`'s queue pattern** (Channel + `ConcurrentDictionary` for dedupe/await) is a reusable template if a "process approved proposals" background worker is needed server-side, distinct from FwHeadless's own project-sync queue.

---

## What I could not determine (would need to be run, not read)

- **Actual runtime behavior of the two `MapSyncProxy(...)` call sites** (`LexBoxApi/Program.cs:205` and the standalone `SyncReverseProxy`) — I read the registration code but did not trace YARP's route config far enough to say definitively why both exist or which one real hg clients hit in production; would need to inspect `deployment/base/ingress-config.yaml`'s actual path routing rules together with the YARP `appsettings` route table, or hit a live cluster, to confirm.
- **Whether `chorusmerge`'s separate .NET 8 runtime actually gets exercised in normal operation** vs. only in edge-case three-way merges — confirmed it's *shipped* and referenced, not confirmed it's *invoked* on a representative sync (would need a merge-conflict scenario run against a live FwHeadless instance, or a targeted grep of `LfMergeBridge`'s own source for when it shells out to `chorusmerge` specifically, which lives outside this repo).
- **hgweb / `hg-deployment.yaml`'s Apache config and the `hgresumable` sidecar's exact protocol** were located and the Deployment YAML was read, but the container images (`ghcr.io/sillsdev/lexbox-hgweb`, `ghcr.io/sillsdev/hgresume`) and `hgweb/Dockerfile` internals were not opened — out of scope per the brief's FwLite/FwHeadless focus, but flagged since "every database/every file store" was asked for exhaustively.
- **Exact production values** behind `deployment/*/secrets.yaml` and `app-config.yaml` (image tags currently deployed, actual Postgres DB name, actual domain-to-service ingress rules) are Sealed-Secrets/ConfigMap manifests whose *keys* I read but whose *values* are either encrypted or environment-specific — would need cluster access (`kubectl -n languagedepot get ...`) to confirm current live state rather than repo intent.
- **Whether the 431-vs-773 test count discrepancy** noted in `Motif/docs/minilcm-evaluation.md:141` reflects a different counting methodology (e.g., excluding `MiniLcm.Tests` or counting only FwData-focused suites) or a stale count — I verified 773 total `[Fact]`/`[Theory]` methods across the six FwLite test projects on this checkout of `develop`; I did not re-derive the other document's exact scope/filter to reconcile the two numbers, so I am not asserting it is wrong, only flagging the gap.
- **Live health of the `verify-published` self-hosted runner** and whether `deploy.yaml`'s fleet-repo GitOps push actually results in a cluster rollout (vs. just updating a manifest) was not observed — would require access to the fleet repo (`vars.FLEET_REPO`, not part of this checkout) and its own controller.
