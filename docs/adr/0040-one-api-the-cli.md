# ADR 0040 — There is one API, and it is the CLI

**Status:** accepted, 2026-08-26. Supersedes [ADR 0039](0039-one-worker-baseline-and-live-host-authority.md)
decisions 1 and 8, and its decision 2's in-process FieldWorks boundary. ADR 0039's Baseline, live-host
authority, per-project queueing, PanGloss bounding, and one-sibling-database decisions remain binding.
Completes [ADR 0021](0021-cli-is-the-full-surface-layer-1-churns.md) decision 2 by making the process
boundary the same as the product boundary. ADR 0030's FieldWorks file lock and one-live-writer findings
remain binding and are now the mechanism, not a constraint worked around.

**In plain terms:** Motif had grown two ways in — a command line for people and scripts, and a private
pipe protocol for a future FieldWorks add-in to speak. Two doors into one product means every feature
gets built, versioned, and refused twice, and the second door was going to open inside FieldWorks,
carrying Motif's database engine and JSON library into a process Motif does not control. **So there is
now one door.** Everything — an AI agent, a script, and FieldWorks itself — runs the `motif` command and
reads its JSON. FieldWorks becomes a skin over that output rather than a host for Motif's insides.

## Context

Three facts arrived together and, taken together, they invert the reasoning behind ADR 0039.

**The wire was never load-bearing for coordination.** ADR 0039 specified a named-pipe JSON protocol with
capability negotiation so a `net10.0` worker could serve a `net10.0` CLI and a `netstandard2.0` FieldWorks
client. Seven handlers were ever wired into the real server — job status, store cutover, two Baseline
transfer handlers, and three live-host observation handlers — against a CLI surface of thirty verbs, and no
test has sent a command through the real worker process: `motif.exe` and `worker.exe` have never exchanged
a frame. The durable layer underneath it, by contrast, was built for multi-process coordination from the
start: the
`Jobs` table already carries an optimistic-concurrency `Version` column, `JobRepository.Transition` already
takes an `expectedVersion`, and `IX_Jobs_Status_Updated` is a work-queue claim index.

**The remaining migration was dominated by re-encoding refusals.** The CLI's `Commands.cs` is 1,845 lines
containing 59 distinct `Fail(...)` sites. Moving the decision to refuse across a process boundary means a
typed refusal payload rich enough to reproduce each one, which the
[CLI-to-worker API surface note](../cli-worker-api-surface.md) called the single largest piece of work in
the migration and larger than its other five steps combined. That work exists only because a boundary was
placed between the validator and the caller. Removed boundary, removed work.

**The in-process FieldWorks plan was the most expensive part, not the free part.** FieldWorks ships
entirely on `net48` and has no third-party plugin mechanism, so any add-in is a change to the FieldWorks
source tree. It uses Newtonsoft.Json throughout and no `System.Text.Json`; it ships no SQLite provider at
all. Motif references `System.Text.Json` from 162 files and `Microsoft.Data.Sqlite`, which drags
SQLitePCLRaw and a native `e_sqlite3.dll` whose resolution under `net48` is a documented recurring
failure. Loading Motif in-process therefore injects Motif's heaviest and most fragile dependencies into
the one process whose release cadence Motif does not set — in exchange for a target framework that has a
scheduled end date, because FieldWorks' Avalonia transition is also a move to `net10.0`.

Measured, so the trade is on the record rather than asserted. Retargeting `Host`, `Projection`, and
`Worker` to `netstandard2.0` produces roughly 156 compile errors across eight distinct missing APIs. Seven
are covered by a polyfill package with no source edits. The eighth is not: `ProcessStartInfo.ArgumentList`
has 14 call sites and does not exist on `netstandard2.0`, so every one becomes hand-quoted command-line
string building — on the path that launches PanGloss. The compile cost is roughly a day; the standing cost
is that every future line of storage and orchestration code is written against a 2018 BCL, without
`IAsyncDisposable`, `Task.WaitAsync`, `Stream.ReadExactlyAsync`, or a nullable-annotated framework.

## Decision

### 1. The FieldWorks surface never opens Motif's database, and never loads Motif in-process

This is the load-bearing rule and everything else follows from it. Under the pipe, the boundary between
FieldWorks and Motif's storage was enforced by the compiler: the FieldWorks side referenced only the
contract and client assemblies and could not have opened a database if it tried. That enforcement is now
gone, so the rule must be written down and held deliberately.

A FieldWorks-side Motif surface obtains every fact by running the `motif` executable and reading its JSON,
and causes every change the same way. It does not reference `SIL.Motif.Host`, `SIL.Motif.Worker`,
`SIL.Motif.Projection`, `Microsoft.Data.Sqlite`, or the Motif schema. It does not open `Project.motif.db`
for reading, not even for a list or a status poll, because a read path is how a SQLite provider and its
native assets arrive in the FieldWorks process by the back door.

**One assembly is permitted to cross, and only as shapes.** `SIL.Motif.Contract` describes what Motif's
fields and responses *are*, so that FieldWorks can deserialise `motif --json` into typed values and render
a diff rather than re-deriving the vocabulary. It holds no behaviour that reaches storage, no LibLCM
reference, and no project loader. That is a narrower footprint than the superseded design gave FieldWorks,
which was Contract plus a live protocol client.

This creates an obligation the current code does not meet: **the response shapes a consumer deserialises
must live in `SIL.Motif.Contract`.** Today the projections rendered by `--json` are declared in
`SIL.Motif.Projection`, which references LibLCM, ICU, and libpalaso — types no FieldWorks surface should
have to bind to in order to read a list. The serialisable response records move to Contract; the builders
that need an `LcmCache` to populate them stay where they are. Until that move happens, a consumer can only
read the JSON loosely, and this decision is not fully delivered.

### 2. There is one API surface: `motif <verb>` and its JSON

Every consumer is the same consumer. An AI agent, a shell script, a test, and a FieldWorks view model all
invoke the CLI and parse `--json` output. There is no second vocabulary, no wire command set parallel to
the verb set, and no capability negotiation, because there is no second client kind to negotiate with.

Refusals stay where they are computed. The 59 `Fail(...)` sites keep deciding and keep wording their own
refusals in-process; what the CLI gains is a defined envelope and exit-code contract for reporting them,
specified in [the CLI API](../cli-api.md). This is a documentation obligation, not a migration.

### 3. `netstandard2.0` survives only where something outside Motif consumes the assembly

The rule is consumption, not convention: **a Motif assembly targets `netstandard2.0` if and only if a
`net48` FieldWorks or a non-.NET runner references it.** Today that is `SIL.Motif.Contract` and nothing
else — it is the shape library of decision 1, and its own project file already records a second consumer,
the Python and Rust runners that read it as the normative description of Change Set shape and RFC 8785
canonicalisation. It keeps the target.

`SIL.Motif.Runner` loses it. Its stated reason was that FieldWorks must load it in-process to run Apply on
FieldWorks' own `LcmCache`, and that is exactly what this ADR withdraws. The Runner still runs in-process
with whoever owns the live cache, as [ADR 0006](0006-engine-reality-apply-readback-preflight.md) requires —
that owner is now always a `net10.0` Motif process, so read-back after apply is unaffected and only the
identity of the surrounding process changes.

`SIL.Motif.Model` loses it unless the response-shape move in decision 1 shows that Contract's records
need it, in which case it is kept for the same reason Contract is. Everything else — `Host`, `Worker`,
`Projection`, `Cli` — is `net10.0` and was already.

**What this removes, stated precisely, because a partial claim is worse than none.** The severe `net48`
hazards go: `Microsoft.Data.Sqlite` and SQLitePCLRaw's native `e_sqlite3.dll` never enter the FieldWorks
process, so the documented native-asset resolution failure cannot occur, and no second ICU or SLDR
initialisation is needed. The managed one does **not** go: Contract references `System.Text.Json 8.0.5`,
and loading it into a `net48` host that uses Newtonsoft throughout is a binding-redirect question that
still has to be answered when FieldWorks first references Contract. It is a much smaller question than the
native one — it is assembly-version configuration, not asset probing — but it is not zero and must not be
recorded as solved.

### 4. The named-pipe protocol is deleted; the database is the boundary between Motif's own processes

The pipe framing, envelopes, handshake, capability negotiation, refusal envelope, dispatcher, event sink,
and binary transfer are removed — 3,356 lines carrying no domain behaviour. The CLI and a resident job
runner coordinate through `Project.motif.db` and nothing else. Neither asks the other anything; the runner
claims rows and the CLI writes them.

`SIL.Motif.Launcher` survives intact: the installed-executable catalog, newest-compatible selection, the
per-user mutex, and run-until-idle lifetime are how the job runner is started and kept singular, and none
of them depend on a wire.

### 5. The CLI and the job runner ship as one artifact at one version

Schema-as-contract is safe between processes that version together and unsafe between processes that do
not. `MotifDatabase` refuses to open a database whose schema generation exceeds the one the binary
supports, unconditionally and before any compatibility floor is consulted, so a mixed-version pair does
not degrade — it stops. That is correct behaviour and it is only tolerable because the two processes are
installed together and upgrade together. Any future proposal to ship them separately reopens this ADR.

The compatibility floor in `MotifMetadata.MinimumWorkerVersion` has returned the same value for every
schema generation so far, so its raise-on-migration path has never executed. It is retained as the
intended lever for deliberate lockout, and the refusal it produces is now user-facing text rather than an
internal invariant, because under this decision an ordinary user can meet it by upgrading one product.

### 6. Motif never attaches to a live FieldWorks project as a shared-XML peer

liblcm's `SharedXMLBackendProvider` genuinely permits several processes to hold one project at once: only
the first peer takes the `{project}.fwdata.lock`, later peers register in a memory-mapped commit log, and
a non-master peer may commit into that log. FieldWorks reaches it through `BackendProviderType.kSharedXML`
and the Paratext Lexicon Plugin already writes to projects this way. It is therefore a real option, and it
is rejected.

The reason is that its failure modes require a user to be present. On a reconciliation conflict the
provider does not throw; it records a pending reconciliation and defers to `ILcmUI.ConflictingSave()`,
where answering yes means reverting to the saved state and returning from `Save()` normally. A headless
process that answers that question has either silently discarded the work it was asked to apply or has no
correct answer to give. Motif's own `HeadlessLcmUi.ConflictingSave` returns true today, which would make
a conflicted Apply report success while reverting — recorded as a defect and safe only because the
exclusive lock currently makes foreign conflicts impossible.

The remaining hazards compound it: a master peer that dies with unflushed generations wedges the project
for the next peer with an inconsistent-state exception; commit-log overflow makes the master silently drop
a commit that other peers then cannot find; and a non-master peer may not migrate the model version at
all. None of these is a defect in liblcm — they are the reasonable cost of an interactive multi-peer
design — but they are all resolved by a human at a keyboard, and Motif's authoring path is explicitly for
unattended agents.

### 7. Reaching a live project follows the pattern FieldWorks already ships

When FieldWorks holds a project and Motif must touch it, FieldWorks saves and releases it, runs the
`motif` verb, and reloads afterwards. This is the FLExBridge pattern: a separate executable launched by
FieldWorks, operating on the released project, with FieldWorks reloading the result. Users already meet it
during Send/Receive, including its cost — an external mutation discards the undo stack on reload.

Little traffic takes that path. Proposal authoring, lists, diagnostics, reports, and summaries touch only
Motif's own database and run with FieldWorks fully open and untouched. Dry Runs use a saved Baseline and
never open the live project, which is what ADR 0039's Baseline exists for and why that decision survives
this one. Only Baseline capture and Apply need the project itself, and both are deliberate acts at a save
boundary.

## Consequences

- One surface is specified, tested, versioned, and refused once. A feature reachable by an agent is
  reachable by FieldWorks the same day, because it is the same invocation.
- Every FieldWorks-side feature is exercised by the CLI first, which is the cheapest place to iterate and
  the only place an AI agent can drive. ADR 0021's "CLI is the whole product" becomes structurally true
  rather than aspirational.
- End-to-end tests become ordinary in-process tests. The spine that was never stood up under the pipe —
  a real command reaching real storage — no longer requires two executables to meet.
- Motif loses the ability to mutate a project FieldWorks currently holds open. That capability was
  specified but never built, and decision 6 records why it should not be.
- A FieldWorks-initiated verb that opens a project pays the cold `LcmCache` load per invocation. The
  resident job runner keeps warmth for queued work; interactive calls do not get it.
- Motif's JSON output becomes a compatibility surface with real consumers, and must be versioned with the
  care previously reserved for the wire contract. The difference is that it is one surface instead of two,
  and it is the one the product already had to ship.
- The `netstandard2.0` retirement removes the severe `net48` runtime risks — native `e_sqlite3`
  resolution, duplicate ICU and SLDR initialisation, and the transitive `L10NSharp` package that has no
  `netstandard2.0` target at all — because none of the assemblies carrying them cross any more. It leaves
  one open: `SIL.Motif.Contract` still carries `System.Text.Json`, so binding redirects in a
  Newtonsoft-based `net48` host remain to be settled when FieldWorks first references it.
- `SIL.Motif.Contract` becomes a genuine published contract with two consumer families — a `net48`
  FieldWorks surface and the non-.NET runners — and must be versioned accordingly. Moving the `--json`
  response records into it is a prerequisite for the first, and is not yet done.

## Rejected alternatives

- **Keep the worker protocol and finish routing the verbs through it.** Rejected because it is the only
  option carrying two contracts where one suffices — the database schema exists under every design, and
  the wire is added on top of it — and because finishing it means re-encoding 59 refusals whose only
  reason to cross a boundary is that the boundary was placed there.
- **Load Motif in-process in FieldWorks over a shared database.** Rejected under decision 1. It moves
  Motif's SQLite provider, native assets, and JSON stack into a process Motif does not release, to reach a
  target framework that is scheduled to be retired.
- **Attach to the live project as a `kSharedXML` peer.** Rejected under decision 6, on the specific
  ground that its conflict and master-failure paths require an interactive user.
- **Shared database with no resident process.** Rejected because PanGloss work outlives a CLI invocation:
  it runs as a supervised killable child with machine-wide capacity limits and retry-on-interruption, and
  something must own that across invocations.
- **Install the job runner as a Windows service.** Rejected because it requires elevation, is machine-wide
  rather than per-user, and must be stopped and re-registered to update. The existing per-user mutex and
  run-until-idle lifetime already provide singularity without any of that.
- **Expose a thin FieldWorks-only IPC alongside the CLI, designed later.** Rejected because "later,
  against a real consumer" is how the first wire was justified, and the consumer it was designed against
  never arrived. If FieldWorks needs something the CLI cannot express, the answer is to fix the CLI.
