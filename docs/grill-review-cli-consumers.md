# Review/approval, CLI, and non-.NET consumers, on top of Harmony

Status: research report, prepared for a design discussion. Not a decision record.

This report takes [ADR 0013](adr/0013-harmony-is-the-change-mechanism.md) as settled: **Harmony's
`Commit`/`IChange` is the change mechanism; LCAtom does not ship a competing contract, format, or
runner.** That is not re-argued here. What follows is primary-source research into the three
questions ADR 0013 left open: what a review/approval layer needs, whether a CLI is still wanted and
where it belongs, and what non-.NET producers (PanGloss, Flexicon, linguistic-assistant) would need to
participate in a Harmony-shaped world.

Every factual claim below is marked **VERIFIED** (I read the cited code/doc and confirmed it) or
**INFERRED** (reasoned conclusion, not directly confirmed by a single piece of text — usually "absent
because a targeted search found nothing"). Paths are relative to the repo named in each subsection
unless given as a full Windows path.

---

## 1. Can propose/review/approve be built on Harmony as it stands?

**There is no "recorded but not yet applied" commit state in Harmony.** Every path that adds a commit
— `DataModel.AddChange`/`AddChanges` (`harmony/src/SIL.Harmony/DataModel.cs:53-91`), `AddManyChanges`
(`DataModel.cs:61-80`), and sync ingestion via `ISyncable.AddRangeFromSync`
(`DataModel.cs:138-168`) — calls `UpdateSnapshots` inside the same transaction that adds the commit
(`DataModel.cs:76-79`, `113-119`, `151-155`). There is no draft, staging, or pending-approval commit
table; once `AddChange` returns, the change is live in the project's current snapshot state and will
sync to every other peer via `SyncWith`/`SyncMany`. **VERIFIED.**

I looked specifically for a staging concept and found none: a case-insensitive search of
`harmony/src/SIL.Harmony` for `Draft|Proposal|Approv|Review` turns up nothing except incidental
"pending upload" language in the resource subsystem (`ResourceService.cs:89-105`, about a local file
not yet uploaded to remote storage — an upload-queue state, not a review-gate state). **VERIFIED
(absence)** — searched, no matches for a review/proposal primitive.

The one thing that looks adjacent, `CrdtRepository.GetCurrentSnapshotsAndPendingCommits`
(`harmony/src/SIL.Harmony/Db/CrdtRepository.cs:213-225`), is not a proposal queue either: "pending"
here means commits that were already added to the store but occurred after the newest commit already
reflected in the materialized snapshot cache — pure lazy-materialization bookkeeping, consumed by
`DataModel.GetSnapshotsAtCommit` (`DataModel.cs:300-315`) to catch the snapshot cache up to a target
commit. **VERIFIED.** It has nothing to do with review gating.

**What `GetAtCommit`/`GetBeforeCommit`/`GetAtTime` give you for free** is reconstructing state *at or
before an already-committed commit* — i.e., replaying history (`DataModel.cs:317-391`). `GetAtCommit`
returns the entity as of a given commit; `GetBeforeCommit` returns it as of the commit immediately
prior (`DataModel.cs:358-367`, explicitly used this way by `HistoryService.LoadChangeContext`,
`languageforge-lexbox/backend/FwLite/LcmCrdt/HistoryService.cs:328-329`, to show a before/after diff
for one change in the activity feed). This is a real, already-built "what did this change do" tool —
but it only works for changes that are **already committed**. It is not a preview mechanism for a
change that has not yet been committed. **VERIFIED.**

For previewing a *hypothetical, uncommitted* change (which is what LCAtom's own `assess` verb does
today against LibLCM, `src/SIL.LCAtom.Cli/Commands.cs:454-505`, via `ChangeSetAssessor.Assess` which
never saves), Harmony gives no equivalent primitive. `IChange.ApplyChange`/`NewEntity` need an
`IChangeContext` (`harmony/src/SIL.Harmony/Changes/Change.cs:12-33`), and Harmony's own
`ChangeContext` implementation is backed by a live `SnapshotWorker` reading the database
(`harmony/src/SIL.Harmony/Changes/ChangeContext.cs:24-35` — `GetSnapshot`, `GetObjectsReferencing`,
`GetObjectsOfType` all delegate to `_worker`, which queries `CrdtRepository`). There is no
in-memory-only, no-database "simulate this change against current state" entry point. **VERIFIED.**
The one place in this ecosystem that does something like a full hypothetical state computation,
`SnapshotAtCommitService.GetProjectSnapshotAtCommit`
(`languageforge-lexbox/backend/FwLite/LcmCrdt/SnapshotAtCommitService.cs:23-81`), does it by forking
the entire SQLite database file, deleting commits after a target point, and regenerating snapshots
(`SnapshotAtCommitService.cs:36-60`) — a heavyweight, whole-database operation built for browsing
history, not a lightweight per-change preview.

**What this means for "can a proposal be modeled as a set of `IChange`s held outside the main commit
log until approved":** yes, straightforwardly, but that holding has to happen *outside Harmony*, in
the application layer — because Harmony gives you no "hold this commit, don't apply it yet" state.
Two concrete shapes fall out of the primitives above:

- **Hold-then-commit.** Keep the drafted `IChange` list in application storage (Postgres row, local
  file, whatever) exactly the way LCAtom's own CLI already holds a draft today
  (`src/SIL.LCAtom.Cli/Store/DraftDocument.cs`, `Commands.cs:92-131`) — the object never touches
  Harmony's `DataModel` until an approval flips it to "commit for real" via `AddChange`. Preview
  ("what would this do") requires the application to call `IChange.NewEntity`/`ApplyChange` itself
  against a freshly-fetched current entity, without going through `DataModel.AddChange` — a thin,
  buildable wrapper, not a Harmony gap. This mirrors exactly what `ChangeSetAssessor.Assess` already
  does for LibLCM (assess without saving) and is a direct, in-kind port of that pattern onto Harmony
  entities.
- **Commit-then-gate.** Commit the change into Harmony immediately (so it CRDT-syncs to every device
  the instant it's authored, satisfying multi-device/multi-user visibility for free) and record
  approval as a *separate*, CRDT-native status object that references it — exactly the shape the
  comment-thread precedent already uses (§2 below). "Not yet approved" becomes an application-level
  read filter over Harmony's already-synced data, not a Harmony-level staging concept.

**Argument for where the gap belongs:** the "hold before commit" primitive that ADR 0013's own
`Consequences` section flags as open (the note that a CLI-in-Harmony is "deliberately left open") is
not, on this evidence, a primitive Harmony is missing structurally — Harmony's design (CRDT,
last-writer-wins per field, always-live snapshots) is *architecturally opposed* to holding a commit off
to one side, because the entire point of a CRDT commit log is that every commit that exists is already
mergeable and already applied. Bolting a "recorded but inert" commit state onto Harmony would fight
that design rather than extend it. The natural seam is the application layer (Lexbox/FwLite), using
Harmony's existing `IChange`/`DataModel` exactly as the comment-thread code already does. **This is my
own reasoned conclusion — INFERRED, not read directly from any Harmony design doc** (I found no
Harmony document that discusses staged/pending commits as a rejected or considered feature).

---

## 2. Where should approval state live?

**Verifying the "Harmony has no users/permissions/roles" premise:** confirmed. `CommitMetadata`
(`harmony/src/SIL.Harmony.Core/CommitMetadata.cs:3-19`) carries exactly `AuthorName`, `AuthorId`,
`ClientVersion`, and a free-form `ExtraMetadata` dictionary — plain strings, no `User` type, no role
enum, no permission check anywhere in the commit-write path. `DataModel.AddChange` takes a `clientId`
(a `Guid` identifying *a device*, per its own doc comment, `DataModel.cs:46-49`), not a user identity,
and performs no authorization check of any kind — anyone who can call `AddChange` can write any change.
**VERIFIED.** Harmony is, by design, an unauthenticated, unauthorized local data-structure library; all
gatekeeping happens above it.

**Option (a): approval as Harmony/CRDT data.** There is a direct, working precedent for this already
shipping in `LcmCrdt`: comment threads. `CommentThread`
(`languageforge-lexbox/backend/FwLite/MiniLcm/Models/Comments.cs:21-50`) carries `SubjectId` +
`SubjectType` (an `Entry`/`Sense`/`ExampleSentence` reference — i.e., "what this thread is about"),
`Status` (`ThreadStatus.Open`/`Closed`), `AuthorId`/`AuthorName`, and timestamps. It is created and
mutated entirely through Harmony `IChange`s —
`CreateCommentThreadChange`/`SetCommentThreadStatusChange`/`CreateUserCommentChange`/`EditUserCommentChange`
(`languageforge-lexbox/backend/FwLite/LcmCrdt/Changes/Comments/*.cs`) — with soft-delete cascade
implemented via `IObjectWithId.RemoveReference` (`UserComment.RemoveReference`,
`Comments.cs:69-73`, deletes a comment when its parent thread is deleted). This is architecturally
*exactly* what an "approval thread" would look like: a `Status` enum (`Proposed`/`Approved`/`Rejected`
instead of `Open`/`Closed`), a `SubjectId`/`SubjectType` pointing at the entity or entities a proposal
touches, author provenance already present, and a comment/discussion thread already attached to it for
free — the product's "conversation thread" requirement is *the same object*. **VERIFIED as precedent;
the extension to approval status is my own design inference, not something already built — INFERRED.**
Approval-as-CRDT-data syncs to every device automatically via the same sync path everything else in
`LcmCrdt` uses (`CrdtHttpSyncService`/`CrdtProjectSync`, §5 below), works fully offline, and needs no
new server infrastructure.

Its weakness is exactly the premise verified above: Harmony has no permission model, so "who is allowed
to approve" cannot be enforced by Harmony itself. A `SetApprovalStatusChange` is just data — any device
with sync access can write "approved" into it. Enforcing "only a project Manager may approve" would
require either (i) trusting the client UI to gate the button (weak — any other Harmony-aware client or
script could write the change directly), or (ii) the server checking the change's content and author
against roles at ingestion time inside `CrdtController.Add` (`languageforge-lexbox/backend/LexBoxApi/Controllers/CrdtController.cs:41-50`)
— which is possible in principle (the endpoint already runs `permissionService.AssertCanSyncProject`,
line 45) but would mean writing bespoke per-change-type authorization logic into the generic commit
ingestion path, which today authorizes at the *project* level only, not the *change* level.

**Option (b): approval server-side in Lexbox.** Lexbox already has a real, working role/permission
system to hang this on. `PermissionService`
(`languageforge-lexbox/backend/LexBoxApi/Services/PermissionService.cs`) checks `UserRole.admin`,
per-project `ProjectRole` (`Editor`/`Manager`, used at `CanSyncProject`, lines 55-62), and
`OrgRole.Admin` for org-managed projects (lines 15-26); GraphQL mutations declare and enforce these via
explicit `permissionService.AssertCan...` calls inside HotChocolate `[MutationType]` methods (e.g.
`ProjectMutations.AddProjectMember`, `languageforge-lexbox/backend/LexBoxApi/GraphQL/ProjectMutations.cs:71-79`).
**VERIFIED.** A Postgres-backed `Proposal`/`Approval` table plus a GraphQL mutation
(`approveProposal(id, ...)`) gated by `permissionService.AssertCanManageProject` (or a new, more
specific assertion) would reuse this machinery exactly as every other privileged write in Lexbox does.
The cost is that approval state then lives in a different store than the change it's approving
(Postgres vs. the CRDT commit log), which does not sync to offline FwLite clients the way CRDT data
does, and needs its own replication/consistency story if a reviewer is offline.

**Recommendation.** Split the two concerns rather than pick one store for both: **model the proposal
and its content as Harmony data (comment-thread pattern), but gate the *authorization to write an
approval change* at the Lexbox HTTP ingestion boundary**, the same boundary that already authorizes
every other commit (`CrdtController.Add`, line 41-50). Concretely: any device can locally compute and
show "this looks approved to me," but a `SetProposalStatusChange` claiming `Approved` is only
meaningful/trusted once it reaches the server and the server's `AddCommits` path checks the committing
user's role for that project before accepting it (or, more simply in a first version, accepts it
unconditionally into the CRDT log — since the log is an audit trail of what was proposed — but the
*read side* (GraphQL/viewer) only displays a proposal as "approved" once it can also verify the
committing author had the requisite role at commit time, using data already visible in
`CommitMetadata.AuthorId` cross-referenced against Lexbox's project-membership table). This keeps
approval visible, synced, and discussable offline (the product's actual ask: conversation thread +
decision, on the same object, following the comment-thread precedent) while keeping "who is allowed to
flip the switch" answerable by Lexbox's existing role system rather than inventing a second one inside
Harmony. **This recommendation is my own synthesis — INFERRED — built from the two verified precedents
above; I found no existing document that proposes this exact split.**

---

## 3. Attachments and typed metrics

**Harmony already has a full, shipping attachment/blob mechanism**, and it is not theoretical — it is
in production use in this exact codebase. The pieces:

- `LocalResource` (`harmony/src/SIL.Harmony/Resource/LocalResource.cs:6-16`) — a non-CRDT pointer to a
  file on local disk (an `Id` and a `LocalPath`).
- `RemoteResource<TMetadata>` (`harmony/src/SIL.Harmony/Resource/RemoteResource.cs:14-52`) — the CRDT
  object: an `Id`, an optional `RemoteId` (null until uploaded), and a generic, app-declared
  `TMetadata`. This is an `IObjectBase`, so it is itself a Harmony entity with its own change types:
  `CreateRemoteResourceChange<TMetadata>` (`Resource/CreateRemoteResourceChange.cs:6-24`),
  `SetRemoteResourceMetadataChange`, `RemoteResourceUploadedChange`,
  `CreateRemoteResourcePendingUploadChange`, `DeleteRemoteResourceChange`.
- `HarmonyResource<TMetadata>` (`Resource/HarmonyResource.cs:5-34`) — the merge view of local + remote
  state (`Local`/`Remote` flags).
- `ResourceService<TMetadata>` (`harmony/src/SIL.Harmony/ResourceService.cs`) — the API surface: add,
  upload (via a pluggable `IRemoteResourceService<TMetadata>`), download, list pending
  upload/download, delete. **VERIFIED**, all read directly.

This is **already wired end-to-end in `LcmCrdt`**, not just present as a library feature. `LcmCrdtKernel`
registers it — `config.AddRemoteResourceEntity<LcmFileMetadata>()`
(`languageforge-lexbox/backend/FwLite/LcmCrdt/LcmCrdtKernel.cs:402`) and
`services.AddCrdtRemoteResources<LcmFileMetadata>()` (line 68) — and `LcmMediaService`
(`languageforge-lexbox/backend/FwLite/LcmCrdt/MediaServer/LcmMediaService.cs`) implements
`IRemoteResourceService<LcmFileMetadata>`, uploading/downloading through a proxied media server
(`IMediaServerClient`, lines 150-212) with local caching, coalesced concurrent downloads, and
retry-on-transient-failure logic. This is currently used for FLEx media files (pictures, audio) — the
type parameter is `LcmFileMetadata`
(`languageforge-lexbox/backend/FwLite/MiniLcm/Media/FileMetadata.cs`), a filename/MIME-type/author/size
record, not something PanGloss/word-list specific. **VERIFIED.**

**Fit for the product's need** (bind a PanGloss report and word-list metrics to a proposal): this
mechanism already has everything ADR 0011's S5/attachment requirements ask for — a blob store
(local-first, with remote upload/download), app-declared typed metadata attached to the blob, and it is
already a Harmony `IObjectBase` so it CRDT-syncs, gets its own commit history, and can reference (or be
referenced by) the proposal entity from §2 the same way `RemoteResource` already gets referenced. What
it does *not* give you out of the box is ADR 0011's specific staleness-flagging semantics ("binds to
the `intentDigest`... amending marks prior reports stale," ADR 0011 §4) — that would need to be
declared as part of an app-specific `TMetadata` (e.g. `LcmFileMetadata`-style, but for
`PanGlossReportMetadata` carrying the commit/snapshot id it was computed against) and checked by
application code, not by Harmony itself. That is a thin, ordinary metadata-shape decision, not a new
storage layer. **This fit assessment is my own synthesis, INFERRED from the verified mechanism above —
I did not find any document proposing PanGloss/word-list attachments use `ResourceService` specifically.**

**Compared to building attachment storage in Lexbox from scratch** (object storage + Postgres row +
GraphQL mutation, the LCAtom-native path S5/ADR 0011 originally sketched): that would duplicate
`RemoteResource`/`LocalResource`/`ResourceService` almost feature-for-feature, and would live in a
different store than the proposal and comment thread it's attached to, losing the free
CRDT-sync-to-offline-clients property the Harmony-native path gets automatically. Given the existing,
working, in-tree precedent, **building new Lexbox-side attachment storage is very hard to justify** —
the honest case for it would be "PanGloss reports are large enough or numerous enough that they
shouldn't sync to every offline FwLite device the way pictures do," which is a real operational
question (report/blob size and sync-cost budget) but not one this research answered — see "What I
could not verify."

---

## 4. Is a CLI still wanted, and where does it belong?

**Inventory of what already exists:**

- **LCAtom's own CLI** (`src/SIL.LCAtom.Cli/Program.cs`, `Commands.cs`) — a real, working, in-process
  argument dispatcher (`open`, `new`, `add-set-gloss`, `label`, `comment`, `finalize`, `reopen`,
  `list`, `show`, `assess`, `apply`, `log`) driving LCAtom's own draft/store/Contract/Runner/Host stack
  against `.fwdata` directly (`Program.cs:1-152`, `Commands.cs`). Per ADR 0013 this entire
  Contract/Runner/store stack is the thing being retired as *the change mechanism* — but the CLI
  *shape* (a thin verb dispatcher over testable command handlers, `Commands.cs:26-32`) is a reusable
  pattern independent of what it's a client of. **VERIFIED.**
- **`FwLiteProjectSync`** — confirmed uses `System.CommandLine`
  (`languageforge-lexbox/backend/FwLite/FwLiteProjectSync/Program.cs:1,15-19`), with verbs
  `before-sync`/`after-sync`/`sync` (`Program.cs:20-85`) that reconcile a CRDT sqlite file against a
  `.fwdata` file via `CrdtFwdataProjectSyncService`. This is a batch/CI-shaped tool already, not an
  interactive one — it is invoked as a build/sync step, takes `--crdt`/`--fwdata`/`--dry-run` flags,
  and exits. **VERIFIED**, matches the background claim exactly.
- **`FwLiteWeb` as a single-file binary** — the claim is **true but narrower than stated**. The
  csproj's own Release default is `PublishSingleFile=false` with an explicit comment explaining why
  ("single file disabled as it's less efficient for updates",
  `languageforge-lexbox/backend/FwLite/FwLiteWeb/FwLiteWeb.csproj:11-13`). Two specific packaging paths
  *override* that default: CI's Linux release build passes `-p:PublishSingleFile=true` for
  `linux-x64`/`linux-arm64` (`.github/workflows/fw-lite.yaml:341-342`), and the `build-mini-lcm-sdk`
  Taskfile target does the same for `win-x64`, zipping the result into a distributable "FwLiteWeb
  server + project + config, run locally" SDK bundle
  (`languageforge-lexbox/backend/FwLite/Taskfile.yml:143-152`). The general
  `publish-web-win`/`publish-web-linux`/`publish-web-osx`/`publish-web-osx-arm` tasks
  (`Taskfile.yml:161-176`) do **not** pass `PublishSingleFile=true` — they use the csproj default
  (`false`). **VERIFIED**: single-file is a deliberate, narrow opt-in for two release artifacts (Linux
  CI release, Windows SDK zip), not a blanket cross-platform default. I found no osx single-file publish
  step; the background's "linux/osx/win" framing overstates osx specifically — **INFERRED absent**
  (searched `PublishSingleFile` repo-wide, only the four hits cited above exist).

**Would a CLI belong inside Harmony itself?** No evidence supports this, and the shape of Harmony
argues against it: Harmony is a generic CRDT library with no concept of a "project" in the FLEx sense,
no lexical-entry/sense/proposal vocabulary, and (per §2) no users/permissions. A CLI needs a domain
vocabulary and a place to authenticate/authorize — neither of which Harmony has or should have, since
multiple unrelated apps (`SIL.Harmony.Sample`, `LcmCrdt`) already build different things on top of the
same library. **INFERRED**, from Harmony's own layering (a generic library with `SIL.Harmony.Sample`
as a separate consumer, `harmony/src/SIL.Harmony.Sample/`) rather than a specific document ruling this
out.

**Does it belong in Lexbox or FwLite?** The precedent already answers this: `FwLiteProjectSync` is
exactly "a CLI that operates on Harmony/CRDT data," already living under `backend/FwLite/` next to
`LcmCrdt`, not inside Harmony. A proposal-submission or PanGloss/Flexicon-integration CLI is the same
shape of tool — it needs `LcmCrdt`'s domain model (`Entry`/`Sense`/`CommentThread`/`RemoteResource`) and
Harmony's `DataModel`, so it belongs alongside `FwLiteProjectSync`, not inside Harmony, and not inside
LCAtom (which per ADR 0013 no longer owns the change mechanism this CLI would be driving).

**What job is left undone that only a CLI could do**, given that a Lexbox server and a FwLite web UI
already exist? Concretely, three jobs neither the GraphQL API nor the browser UI serve well:

1. **Batch/scripted proposal submission** — PanGloss and linguistic-assistant are not humans clicking
   a UI; they are pipelines that need to submit N proposed changes (with attached reports) as part of
   an automated run, non-interactively, with a script-friendly exit code and output — exactly the shape
   `FwLiteProjectSync`'s `sync` verb already has (`Program.cs:38-85`, `--dry-run` flag included).
2. **CI integration** — running PanGloss/Flexicon against a project and having the result land as a
   proposal automatically (e.g., a nightly job that regenerates a grammar assessment and opens a
   proposal if metrics regressed) needs something invocable from a CI runner with no browser, no
   interactive auth flow ideally (service-account style), and machine-readable output — a CLI (or the
   HTTP endpoints a CLI wraps, §5) is the natural shape for that, a browser UI is not.
3. **Admin/ops tooling** — `FwLiteProjectSync`'s existing `before-sync`/`after-sync`/`sync` verbs are
   already this category (project reconciliation, run by an operator or a script, not through the
   viewer UI). A proposal/approval CLI plausibly grows the same kind of "operator forgot to do X in the
   UI, fix it from the command line" surface.

**This three-item list is my own synthesis of unmet needs — INFERRED** — cross-referenced against the
`FwLiteProjectSync` precedent (item 1/3, directly analogous to something that already exists and works)
and against what PanGloss/linguistic-assistant's own repos describe about how they're invoked (§5,
batch/pipeline-shaped, never interactive).

---

## 5. Non-.NET consumers

**Harmony's `$type` discrimination, verified precisely.** `IChange` polymorphism is *not*
`System.Text.Json`'s built-in `[JsonPolymorphic]` — it is a custom converter,
`PeekThenConcreteChangeConverter` (`harmony/src/SIL.Harmony/Changes/PeekThenConcreteChangeConverter.cs`).
On read, it requires `$type` as the **first** JSON property of every change object (line 36-38: throws
if the first property isn't the discriminator), looks it up against a per-`HarmonyConfig` registered
type table, and on a match deserializes via the concrete type's cached `JsonTypeInfo` (lines 49-56); on
no match it falls back to `OpaqueChange`, preserving the raw JSON verbatim
(`Changes/OpaqueChange.cs:9-27`, `ReadOpaque`, `PeekThenConcreteChangeConverter.cs:70-82`). On write,
known types get a synthetic `$type` property injected via a `JsonTypeInfo` modifier
(`HarmonyConfig.cs:94-128`, `AddSyntheticTypeDiscriminator`), and `OpaqueChange` writes back its
preserved raw JSON unchanged (`PeekThenConcreteChangeConverter.cs:60-64`). **VERIFIED.** This is the
mechanism ADR 0013 cites as already solving "carrying changes the client cannot interpret" — confirmed
by direct reading, not just by the ADR's summary.

**The `$type` string itself** comes from `IPolyType.TypeName`
(`harmony/src/SIL.Harmony/Entities/IPolyType.cs:6-13`): either an explicit static `TypeName` (e.g.
`CreateRemoteResourceChange<TMetadata>.TypeName => "create:remote-resource"`,
`Resource/CreateRemoteResourceChange.cs:23`) or, via `ISelfNamedType<T>`, the bare CLR type name (e.g.
`CreateCommentThreadChange` in `LcmCrdt`, `languageforge-lexbox/backend/FwLite/LcmCrdt/Changes/Comments/CreateCommentThreadChange.cs:9`,
implements `ISelfNamedType<CreateCommentThreadChange>`, so its wire `$type` is the literal string
`"CreateCommentThreadChange"`). **VERIFIED.** There is no central enum or manifest of discriminator
strings — each app's `HarmonyConfig.ChangeTypeListBuilder.Add<T>()` registration
(e.g. `LcmCrdtKernel.cs:330` registers `JsonPatchChange<Entry>`) is the only source of truth for what
`$type` strings a given deployment understands, and that registration lives in C# only.

**Is there a published JSON schema?** No. I searched both `harmony` and
`languageforge-lexbox/backend/FwLite` for `JsonSchema`/`json-schema`/`GenerateSchema` generation tied to
`IChange` and found nothing (one unrelated hit, an OpenAPI route-parameter schema in
`FwLiteWeb/Routes/MiniLcmRoutes.cs:49`, not a change-type schema). **VERIFIED (absence)** — searched,
no schema-generation code exists for the `IChange` hierarchy. A non-.NET producer today has no
machine-readable artifact to generate against; the only source of truth is the C# change classes
themselves (their property names/types, in file order, per field, since STJ's default naming policy
applies with no camelCase transform configured in `HarmonyConfig.CreateJsonSerializerOptions`,
`HarmonyConfig.cs:39-50` — `JsonSerializerDefaults.General`, i.e. exact-case property names as declared
in C#).

**The commit hash algorithm**, found and read: `CommitBase.GenerateHash`
(`harmony/src/SIL.Harmony.Core/CommitBase.cs:32-40`) computes `XxHash64` over the concatenation of the
commit's own `Id` (a `Guid`, 16 bytes) and the *parent commit's hash* (hex-decoded) — **not** over the
change payload/content at all. `NullParentHash` is the literal string `"0000"`
(`CommitBase.cs:12`). This is a genuinely useful and slightly counterintuitive finding for a non-.NET
producer: **the hash chain authenticates commit *ordering and identity*, not content** — a Rust or
Python producer does not need to replicate any canonical serialization of the change payload to compute
a valid hash; it only needs a fresh random commit `Id` and the current chain's tip hash, both trivial to
obtain/generate outside .NET. **VERIFIED**, and worth flagging explicitly at the design discussion since
it's easy to assume (wrongly) that the hash is a content-integrity check.

**The HTTP endpoint that accepts changes on their behalf**, found and read on both sides:
- Client contract: `ISyncHttp` (`languageforge-lexbox/backend/FwLite/LcmCrdt/RemoteSync/CrdtHttpSyncService.cs:141-154`)
  — `GET /api/crdt/checkConnection`, `POST /api/crdt/{id}/add`, `GET /api/crdt/{id}/get`,
  `POST /api/crdt/{id}/changes`.
- Server implementation: `CrdtController`
  (`languageforge-lexbox/backend/LexBoxApi/Controllers/CrdtController.cs`) — `[Route("/api/crdt")]`,
  `[RequireScope(LexboxAuthScope.SendAndReceive)]` (class-level, line 22). `Add`
  (lines 41-50) takes `StreamJsonAsyncEnumerable<ServerCommit>` — i.e. a **streamed JSON array of
  commit objects**, POSTed as ordinary HTTP+JSON — checks `permissionService.AssertCanSyncProject`,
  then calls `crdtCommitService.AddCommits`. `GetSyncState`/`Changes`/`CountChanges` are the
  corresponding read/diff endpoints. **VERIFIED.** This is a plain REST/JSON surface, not a .NET-only
  RPC mechanism (no gRPC, no gRPC-Web, no SignalR on the write path) — a Rust or Python HTTP client with
  a JSON library can call it today with no .NET runtime involved, **provided** it can produce
  `ServerCommit`-shaped JSON (id, client id, hybrid-clock timestamp, hash, parent hash, and a
  `ChangeEntities` array of correctly-`$type`-tagged change objects) and authenticate with a bearer
  token satisfying `RequireScope(SendAndReceive)`.

**What a Rust/Python producer would concretely need**, synthesizing the above:
1. A **type registry** — the literal `$type` strings and field shapes it intends to emit, sourced by
   hand from the relevant C# change classes (no schema exists to generate from, per above) — a genuine,
   real integration cost, but a bounded and mechanical one for a small, fixed set of change types (e.g.
   just `CreateCommentThreadChange`-shaped "create a proposal" and `CreateRemoteResourceChange`-shaped
   "attach a report").
2. The **commit hash recipe** (§ above) — cheap, three lines of code in any language (a random UUID,
   XxHash64 of id-bytes+parent-hash-bytes, hex-encode).
3. A **hybrid logical clock timestamp** shape matching `HybridDateTime` (not independently verified in
   this pass — flagged under "could not verify" below) — needed on every commit.
4. The **HTTP endpoint and auth token** (§ above, verified) to actually submit.
5. Alternatively, and probably preferable for a first cut: **don't emit Harmony commits directly at
   all**. Emit `OpaqueChange`-compatible JSON — i.e., valid-shaped JSON with a `$type` the *server*
   doesn't recognize yet — and let a small .NET shim (or `OpaqueChange`'s own round-trip property) carry
   it until a real C# type exists, exactly as `OpaqueChange` was designed for
   (`Changes/OpaqueChange.cs:5-8`: "preserves the original JSON so it can round-trip and be applied once
   the type is known"). This defers the type-registry cost (item 1) at the price of the change being
   inert (unappliable) until someone adds the matching C# type — reasonable for early prototyping, not
   for a shipped integration.

**Cross-referencing against what PanGloss/Flexicon/linguistic-assistant actually produce today — this
is greenfield for all three, verified by reading each:**

- **PanGloss** (Rust) reads `.fwdata` directly via its own `pg-fwdata` crate and produces its own
  independent JSON format, `pg-snapshot` (`PanGloss/docs/snapshot-format.md:1-24`, envelope
  `{"format": "pangloss-project", "version": 1, ...}`, PanGloss-owned camelCase field names explicitly
  *not* mirroring LCM/M3Dump property names, per its own conventions section). It "emits structured
  reports and FieldWorks investigation handoffs" and explicitly "never launches FieldWorks"
  (`PanGloss/README.md:26-27`); its ingestion formats are `.fwdata`, its own `.json` snapshot, or legacy
  HC XML (`README.md:69-76`) — none of these is Harmony's `IChange` JSON shape, and I found no
  reference to Harmony, `$type` discrimination, or commit/CRDT concepts anywhere in the files read.
  **VERIFIED**: PanGloss today has zero contact with Harmony's format. Its "report" (build report,
  assessment report, `README.md:33-46`) is exactly the artifact the product description calls "a
  PanGloss report" — but it is currently a freestanding file PanGloss writes, with no attachment/binding
  mechanism of its own; §3's `ResourceService` is the natural place for it to land, not something
  PanGloss needs to build.
- **Flexicon** (Python, `flexicon/docs/ARCHITECTURE.md`) is a **pythonnet wrapper directly over
  `SIL.LCModel` in-process** ("LCM Objects (pythonnet) — from SIL.LCModel," lines 100-108) — it manipulates
  a live LCM object graph through direct property access and casting, not through any JSON change
  format, Harmony or otherwise. **VERIFIED**: no contact with Harmony/CRDT concepts in the architecture
  doc; this is the oldest and most direct of the three integration styles (equivalent to editing FLEx
  itself, not to submitting a proposal).
- **linguistic-assistant**'s `eval-proposal-loop` OpenSpec change (Python,
  `linguistic-assistant/openspec/changes/eval-proposal-loop/proposal.md`) is, notably, **already
  building its own independent "ChangeSet" proposal contract** — described as "a validated ChangeSet of
  LIFT + Hermit Crab edit operations" (proposal.md:16-17) and, per the design doc, "lists of `lexical/*`
  + `morphophonology/*` ops" (`design.md:38`). This is a **third, separate change-set vocabulary**
  (LIFT/HC ops, namespaced `lexical/*`/`morphophonology/*`) — coincidentally closer in naming style to
  LCAtom's own retired operation-kind convention (`lexical/sense/setGloss`,
  `src/SIL.LCAtom.Cli/Commands.cs:154`) than to Harmony's `IChange` — built for a golden-eval/scoring
  pipeline, not for submitting to Harmony/Lexbox. **VERIFIED, and worth surfacing explicitly at the
  design discussion**: if linguistic-assistant's proposal format is meant to eventually become "the
  thing a human reviews and approves" in the product this ADR is designing for, there are now three
  independent change-representation vocabularies in play (Harmony's `IChange`, PanGloss's `pg-snapshot`,
  linguistic-assistant's LIFT/HC `ChangeSet`) and no stated plan to reconcile them. That reconciliation
  — whether linguistic-assistant's harness should target Harmony's format directly, or keep its own and
  translate at a boundary — is a real open question this research did not resolve (see "Questions a
  human must decide").

---

## 6. What of `stage2-change-management.md`'s requirements survives as a requirement?

Extracting the genuine product requirements (not mechanisms) from
[`stage2-change-management.md`](stage2-change-management.md) and
[ADR 0011](adr/0011-experiment-loop-boundary-lcatom-is-the-record.md):

### Effect-scoped approval
*(S4: "approval is per-effect-digest and drift-invalidated," `stage2-change-management.md:56`)*

**(ii) Satisfiable with existing primitives.** A proposal modeled as a `CommentThread`-style object
(§2) with `SubjectId`/`SubjectType` pointing at the specific entity/entities it touches
(`Comments.cs:25-26`) already scopes approval to *that* entity by construction — approving proposal X
does not touch any other entity's data, because the approval record and the underlying change are two
separate objects related only by that reference, exactly the way a `UserComment` is scoped to its
`CommentThread` via `CommentThreadId` (`Comments.cs:56`) and nothing else. No new Harmony primitive is
needed; this falls out of reusing the comment-thread shape. **INFERRED** (the shape supports it; nobody
has built the approval-specific version yet).

### Drift invalidation
*(S4/ADR 0004 decision 3: a proposal computed against commit A must be re-reviewed if the branch moved
past A before approval — "bound apply," `docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md:44-50`)*

**(ii) Satisfiable with existing primitives, not automatic.** Harmony gives you the raw material —
every `ObjectSnapshot` carries a `CommitId`
(`harmony/src/SIL.Harmony/Db/ObjectSnapshot.cs:76`) and `DataModel.GetLatestSnapshotByObjectId`
(`DataModel.cs:245-250`) tells you the current snapshot for an entity — so "has this entity moved since
I evaluated my proposal" is a cheap comparison (store the snapshot/commit id the proposal was computed
against; at approval time, compare it to the current one). But Harmony does **not** do this check for
you the way LCAtom's own `apply` does (`Commands.cs:507-589`, which hard-fails without a bound
`Assessment` whose footprint digest matches, per ADR 0004 decision 3) — there is no equivalent
optimistic-concurrency gate anywhere in `DataModel.AddChange`. **VERIFIED absence** — `AddChange`
(`DataModel.cs:53-91`) takes no "expected prior state" parameter and performs no such comparison. This
is a real, if small, piece of application logic that needs writing: an explicit "recorded-against"
snapshot/commit id field on the proposal object, checked by the approval UI/mutation before honoring an
approval, not provided automatically by Harmony or by the comment-thread precedent as it stands today.

### Provenance (who/what proposed this, human or AI)
*(S8: "provenance splits author... from applier," `stage2-change-management.md:126-128`)*

**(i) Already satisfied today.** `CommitMetadata.AuthorId`/`AuthorName`
(`harmony/src/SIL.Harmony.Core/CommitMetadata.cs:6-7`) records who authored every commit, and
`HistoryService.ListActivityAuthors`/`ProjectActivity`
(`languageforge-lexbox/backend/FwLite/LcmCrdt/HistoryService.cs:116-171`) already surfaces this in a
UI-ready form, including filtering by author. Distinguishing *human* from *AI* specifically is not a
separate field today — but `CommitMetadata.ExtraMetadata`
(`CommitMetadata.cs:11-12`, "used to store application specific metadata") is exactly the
already-provided extension point for it (e.g. `ExtraMetadata["actorKind"] = "ai"`), the same mechanism
`ActivitySort.SyncedNewestFirst` already uses for a different app-specific key
(`ExtraMetadata.SyncDate`, `HistoryService.cs:225-226`). **VERIFIED** that the extension point exists
and is already used this way for an unrelated purpose; adding an `actorKind`/similar key is a trivial,
already-supported addition, not a gap.

### Attachments/typed metrics staleness on amend
*(ADR 0011 §4: "both bind to the intentDigest... amending marks prior reports stale")*

**(iii) Genuine gap, but narrow.** As discussed in §3, `ResourceService`/`RemoteResource<TMetadata>`
gives you the storage and sync but not the staleness bookkeeping — that requires the app-declared
`TMetadata` to carry a reference to what state it was computed against (e.g. a commit id) and something
to compare it against current state at display time. This is ordinary application code, not a
Harmony gap, and is a smaller version of the same "no automatic drift check" gap noted above — both
would likely share one small comparison utility if built together.

### "Never merge" / two-merges distinction
*(S2: LCAtom "never merges change-set stores/histories," `stage2-change-management.md:28-33`)*

**(i) Already satisfied, differently than S2 imagined.** S2 was written assuming LCAtom's own
change-set store existed to *not* merge. Under ADR 0013 there is no LCAtom change-set store; Harmony's
CRDT commit log *is* the merge mechanism now (commutative per-field changes reconciled by hybrid-clock
order, `CommitBase.CompareKey`, `harmony/src/SIL.Harmony.Core/CommitBase.cs:25`), and it is designed
to merge — that is the entire point of a CRDT. This requirement, read literally, does not survive
ADR 0013 at all; it was a property of the mechanism being retired, not an independent product need.
**No action needed; superseded, not a gap.**

---

## Questions a human must decide

1. **Where does "approved" get authorized, precisely?** §2 recommends CRDT-native proposal/comment data
   with authorization enforced at Lexbox's commit-ingestion boundary (`CrdtController.Add`) rather than
   trusting client UI — but today that endpoint authorizes at the *project* level only
   (`AssertCanSyncProject`), not per change-type. Does the team want to build change-type-aware
   authorization into `CrdtController`/`CrdtCommitService`, or accept that "approved" is advisory
   (client-enforced) until/unless that's built?
2. **Does linguistic-assistant's `eval-proposal-loop` change-set format (LIFT + Hermit Crab ops,
   `lexical/*`/`morphophonology/*`) get retargeted at Harmony's `IChange`/`$type` format, kept as its
   own thing and translated at a boundary, or left alone because it serves a different purpose (golden
   eval scoring) that never needs to reach Lexbox?** This is a live, already-in-flight piece of work in
   a sibling repo that nobody appears to have cross-checked against ADR 0013 yet.
3. **Is Harmony's `RemoteResource`/`ResourceService` attachment mechanism (already used for FLEx media
   files) acceptable for PanGloss reports and word-list metrics as-is, or do report/metric volumes and
   sync-cost make a separate, non-CRDT-synced store the right call for this specific attachment type?**
   §3 found the mechanism fits structurally; whether report *size/frequency* makes syncing it to every
   offline FwLite device a problem is an operational question this research did not (and could not)
   answer.

## Confidence level

**Medium-high.** Every claim about Harmony's own mechanics (commit model, hash algorithm, `$type`
discrimination, resource subsystem, absence of users/permissions) is read directly from source and
cross-checked against at least one live usage site in `LcmCrdt`, so I'm confident in those. The
weaker parts are (a) the recommendations in §2 and §4, which are my own synthesis rather than something
found written down anywhere, and (b) the cross-language sections (§5), where PanGloss and
linguistic-assistant are large, actively-changing repos (PanGloss alone has 900+ markdown files and
multiple parallel worktrees) and I read a representative slice (README, architecture docs, one relevant
OpenSpec change) rather than their entire history — it's possible a more recent or less discoverable
document in either repo already addresses Harmony integration and I missed it.

## What I could not verify

- **`HybridDateTime`'s exact wire shape** (fields, units, clock-skew handling) — referenced constantly
  (`CommitBase.HybridDateTime`, `HistoryService.ActivitySort`) but I did not open its source file, so I
  cannot state precisely what a non-.NET producer would need to construct one. This is a real gap in
  the "what does a Rust/Python producer need" answer in §5 and should be closed before anyone scopes
  that work for real.
- **The exact byte-level definition of `ServerCommit`** (the type actually POSTed to
  `/api/crdt/{id}/add`) beyond what `CrdtController.cs` and `ISyncHttp` show — I did not read
  `SIL.Harmony.Core`'s `ServerCommit` type itself, so field-by-field JSON shape for a non-.NET producer
  is not fully nailed down.
- **Storage backend behind `IMediaServerClient`/FwHeadless media** (§3) — I confirmed the client-side
  proxy and upload/download flow but did not trace `FwHeadless/Media/MediaFileService.cs` far enough to
  say whether the underlying store is local disk, object storage, or something else, or what its size
  limits are (a `MaxUploadFileSizeBytes` config was seen but not its value) — relevant to whether PanGloss
  reports (which could be large) are a good fit size-wise.
- **Whether `CrdtController.Add`'s ingestion path does or could feasibly do per-change-type
  authorization** — I read that it authorizes at the project level today; I did not investigate how
  invasive it would be to add change-type-aware checks (e.g., does `crdtCommitService.AddCommits`
  already have access to each change's `$type`/content before persisting, in a form cheap enough to
  branch on).
- **PanGloss and linguistic-assistant beyond the slice read.** Both are large, fast-moving repos with
  active parallel work (PanGloss has `.claude/worktrees/` with several in-flight branches). I read
  README/architecture/format docs, not the full source tree, so I cannot rule out an existing,
  less-discoverable integration point with Harmony or Lexbox that I didn't find.
- **Whether anyone on the FwLite/Lexbox side has already discussed or rejected a "hold a commit before
  applying" feature for Harmony itself.** I found no such discussion, but absence of a hit in this
  pass is not proof no such discussion exists elsewhere (e.g., in issue trackers, PR discussions, or
  Slack/chat history not present in either checked-out repo).
