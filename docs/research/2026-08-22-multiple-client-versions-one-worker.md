# Multiple Motif versions and one local worker

**In plain terms:** FieldWorks may ship Motif 3.2.0 while a separately installed CLI is already at
3.4.2. They should normally share one newer worker, provided both speak an overlapping wire protocol.
Product versions identify builds; an explicit protocol range and capabilities decide whether two builds
can work together. Exact-version workers are a poor default for Motif because its worker is also the sole
owner of project workflow databases and the machine-wide PanGloss scheduler.

This is a research note, not an architecture decision. It compares primary-source precedents and records
the consequences for the proposed per-user `net10.0` worker, `net48`/`netstandard2.0` FieldWorks adapter,
standalone CLI, named-pipe JSON protocol, and sibling SQLite databases.

## What established systems do

### Docker: one daemon, negotiated API overlap

Docker allows client and daemon product versions to differ. Its API has minimum and maximum versions;
negotiation selects the highest version both peers support, disables features introduced later, and adjusts
requests and responses to the negotiated version. Docker describes compatibility as best effort, while
making the versioned API backward-compatible and returning an explicit error for an unsupported API
version. Sources: [Docker Engine API](https://docs.docker.com/reference/api/engine/) and the
[versioned API reference](https://docs.docker.com/reference/api/engine/version/v1.40/).

This is Motif's closest operational analogue: one shared daemon can serve older and newer clients when the
wire contract is designed for it. The worker's product version should be diagnostic information; the
negotiated protocol and capabilities should control which commands and fields a connection may use.

### LSP: stable protocol plus capabilities

The Language Server Protocol exchanges client and server capabilities during `initialize`. Its
specification says features are kept compatible through those capability flags, and clients should ignore
server capabilities they do not understand. It also makes the client responsible for server lifecycle.
Source: [LSP 3.17 specification](https://github.com/Microsoft/language-server-protocol/blob/gh-pages/_specifications/lsp/3.17/specification.md).

Microsoft's newer Agent Host Protocol states the rule particularly clearly: implementation-version fields
are informational, and peers should not parse them to detect features; feature availability belongs in the
capability model. Source: [AHP connection lifecycle](https://microsoft.github.io/agent-host-protocol/specification/lifecycle.html).

For Motif, the initial pipe handshake should carry:

- client identity and product version, for status and support;
- minimum and maximum wire-protocol versions;
- explicit capabilities for optional commands and response shapes;
- the selected protocol version and effective capability set in the reply.

The worker should choose these per connection. FieldWorks 3.2.0 and CLI 3.4.2 can therefore use different
effective feature sets concurrently through one worker.

### VS Code Remote: install an exactly matching server

VS Code takes the opposite approach. Its official extension guidance says the remote server must match the
client version exactly, so the client automatically installs or updates that server. Source:
[Supporting Remote Development](https://code.visualstudio.com/api/advanced-topics/remote-extensions#_architecture-and-extension-kinds).

This works where the remote server is an implementation companion to one client. It would be awkward for
Motif: two clients could demand two workers while both workers want to own the same `.motif.db` and the same
global PanGloss limit. Reproducing VS Code's pattern would therefore require an additional stable broker or
strictly separate durable state.

### Gradle: exact-version daemons, with a stable mediation API

Gradle clients only connect to daemons of the same Gradle version. If no compatible daemon exists, a new
one starts; daemon registries and logs are version-scoped. Source:
[Gradle Daemon](https://docs.gradle.org/current/userguide/gradle_daemon.html#sec:daemon_compatibility).
Gradle separately provides a Tooling API designed to operate across Gradle versions and able to select or
download the build's appropriate Gradle distribution. Source:
[Gradle Tooling API](https://docs.gradle.org/current/userguide/tooling_api.html#sec:tooling_api).

The useful lesson is not that Motif should run parallel versioned daemons. It is that exact-version engines
need a version-independent mediation layer. Motif already wants a single global scheduler and database
authority, so that mediation layer would end up recreating most of the shared worker.

### Protobuf/gRPC: additive evolution works only by discipline

Protobuf's binary format permits adding fields because old readers ignore unknown fields and new readers
accept messages written by old code. It forbids reusing field numbers and requires careful handling of
removed fields. Source: [Proto3 updating rules](https://protobuf.dev/programming-guides/proto3/#updating).
That guarantee does not automatically transfer to JSON: the official ProtoJSON documentation says its JSON
format does not support unknown fields by default and has weaker schema-evolution guarantees. Source:
[ProtoJSON format](https://protobuf.dev/programming-guides/json/#non-goals-of-the-format).

Motif need not adopt Protobuf to borrow the discipline. Its named-pipe JSON readers must explicitly tolerate
unknown object properties, additions must be optional, existing meanings must not change inside a protocol
generation, and new enum values or required behavior must be capability-gated. Compatibility tests should
exercise old-client/new-worker and new-client/old-worker pairs from the supported window.

## Strategies for Motif

| Strategy | Advantage | Cost and risk for Motif |
| --- | --- | --- |
| One shared newest-compatible worker | One pipe, scheduler, database owner, and migration authority | Requires a real compatibility policy and cross-version tests |
| Side-by-side versioned workers | Each client gets its exact implementation | Competing database migrations/writes and duplicate global scheduling; separate DBs would split one workflow |
| Each client ships and starts its worker | Simple packaging ownership | Startup races decide which version owns shared state; uninstall or downgrade can strand a migrated database |
| Stable broker in front of versioned workers | Central arbitration while engines remain versioned | Adds another protocol, process, installer lifetime, failure mode, and security boundary |

The first strategy is the smallest one that preserves the agreed single-worker responsibilities. A stable
launcher can solve discovery without becoming a long-running broker: installations register immutable,
versioned worker locations; the launcher at a stable per-user entry point selects the highest installed
worker whose protocol support includes the maintained client window, then that worker owns the one pipe.
FieldWorks and the CLI should never choose a worker by comparing product-version strings themselves.

## Database and locking hazards

SQLite prevents simultaneous incompatible writes at the storage layer, but that is not semantic
compatibility. In rollback mode only one process may hold the reserved write lock, and an unsuccessful
writer receives `SQLITE_BUSY`; WAL also permits only one writer at a time. Sources:
[SQLite locking](https://www.sqlite.org/lockingv3.html) and
[write-ahead logging](https://www.sqlite.org/wal.html#concurrency).
Two worker versions can therefore alternate successful transactions while disagreeing about workflow
invariants or migration meaning. File locking alone does not make side-by-side workers safe.

SQLite provides an application-controlled `user_version` and an `application_id`, while its own
`schema_version` has a different internal purpose and must not be repurposed. Source:
[SQLite PRAGMA documentation](https://sqlite.org/pragma.html#pragma_user_version).
Motif should store a separate schema generation and minimum worker generation in each `.motif.db`, migrate
transactionally under sole-worker authority, and refuse a database newer than the worker understands.
There should be no automatic downgrade.

A newer worker may migrate a database beyond every older installed worker. Removing that newer executable
must not silently cause an older worker to open it. The launcher should select another installed worker that
meets the database minimum or fail loudly with an update/reinstall instruction. Installers should use
versioned, immutable worker directories so replacing or uninstalling one product cannot delete an executable
that is currently running.

## Recommendation

Use a **Docker/LSP hybrid**:

1. Run exactly one on-demand Motif worker per Windows user.
2. Separate Motif product versions from a monotonically versioned pipe protocol.
3. Have every connection negotiate a protocol interval and explicit capabilities.
4. Keep the JSON contract additive and unknown-property tolerant within a protocol generation.
5. Publish and test a compatibility window. Initially, the 3.4 worker should deliberately support the
   protocol spoken by the FieldWorks 3.2 adapter as well as 3.4 features.
6. Let a stable launcher choose the newest installed compatible worker; keep the launcher out of the data
   path after startup.
7. Give only that worker database access and migration authority. Never start a second version merely to
   satisfy an incompatible client.
8. When no protocol overlap exists, fail clearly and require an update. Introduce a stable broker or
   side-by-side state only if a future major version proves that one worker genuinely cannot implement both
   semantics.

This makes the ordinary 3.2.0/3.4.2 case routine without committing Motif to indefinite backward
compatibility or allowing multiple binaries to arbitrate one workflow database.
