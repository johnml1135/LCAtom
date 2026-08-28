# The Motif API

*2026-08-26. The contract every consumer uses, established by
[ADR 0040](adr/0040-one-api-the-cli.md).*

Motif has one API and it is the `motif` executable. An AI agent, a shell script, a test, and a
FieldWorks-side view model are the same kind of consumer: they run a verb and read its output. There is
no second vocabulary, no library a host embeds, and no wire protocol beside this one.

This document states what the contract is, what part of it is built, and what part is specified and not
yet built. It does not schedule the work.

## The shape of a call

One process is one call. There is no session, no connection, and no handshake; state that outlives a call
lives in `Project.motif.db` beside the project, and a resident job runner picks up work from there.

```
motif <verb> [--project <path.fwdata>] [--store <dir>] [flags] [--json]
```

Thirty verbs exist today, in five groups:

| Group | Verbs |
| --- | --- |
| Project and evidence | `open`, `analyses`, `log` |
| Proposal authoring | `new`, `add-set-gloss`, `add-delete-lexeme-form`, `compose-author-lexeme-form`, `compose-author-feature-structure`, `promote-gloss`, `remove-operations`, `split`, `duplicate` |
| Proposal lifecycle | `label`, `comment`, `finalize`, `reopen`, `defer`, `approve`, `reject`, `supersede` |
| Inspection | `list`, `show`, `dry-run`, `apply` |
| Corpus | `add-corpus`, `add-document`, `add-corpus-bundle`, `corpora`, `show-corpus` |

The verb set is expected to churn. [ADR 0021](adr/0021-cli-is-the-full-surface-layer-1-churns.md) settles
that churn is welcome in this surface and forbidden in the hashed operation vocabulary and canonical JSON
form beneath it — those are what digests are computed over, and changing them changes the meaning of
stored evidence.

## What a consumer may rely on

**Text is for humans; `--json` is for everything else.** A consumer that parses the text rendering is
using an unsupported interface. Every projection is reachable as structured data and the text form is a
formatting pass over it, which is the rule ADR 0021 decision 2 sets so that a FieldWorks skin and the CLI
render the same material.

**Success goes to stdout, failure goes to stderr, and the exit code is authoritative.** A consumer decides
success from the exit code, never by inspecting output.

**No verb requires a prior verb in the same process.** Anything a call needs is either passed as a flag or
already durable in the store.

## The failure contract

Every refusal in `Commands.cs`, plus unknown verbs and usage errors, used to render the same way — plain
`error: <message>` on stderr with exit code `1`, **regardless of `--json`**. A machine consumer that asked
for JSON got prose when something went wrong, and could not tell a malformed invocation from a locked
project from a refused Apply.

That was the whole of the work ADR 0040 left behind, and it was small for a reason worth naming: under
the superseded worker protocol each refusal had to be re-expressed as a typed payload a remote client
could reconstitute a message from, which the [API surface note](cli-worker-api-surface.md) called the
largest step in that migration. Here the decision to refuse and its wording stay in one process, so only
the envelope was missing.

### The failure envelope

Under `--json`, a failure emits a single JSON object on stderr and nothing on stdout:

```json
{
  "ok": false,
  "reason": "ProjectLocked",
  "message": "FieldWorks cannot open the project because another program is using it.",
  "detail": { "project": "…\\Sena 3.fwdata" }
}
```

`message` is the existing human wording, unchanged — the 59 sites keep their text. `reason` is a closed
set of machine-stable codes; `detail` is optional and reason-specific. Without `--json` the current
`error: <message>` rendering stays exactly as it is, because that is the human interface and it works.

Successful `--json` output stays as it is today: the projection itself, unwrapped. A consumer distinguishes
the two by exit code and stream, not by probing for an envelope.

### Exit codes

One code for every failure makes the caller guess. The set is deliberately small, because a code is a
promise and a large set is a large promise:

| Code | Meaning |
| --- | --- |
| `0` | The verb did what it was asked. |
| `1` | The invocation was wrong — unknown verb, missing or malformed flag. Retrying unchanged cannot help. |
| `2` | The request was well-formed and Motif refused it. A Drift refusal, a failed precondition, a policy denial. The state is unchanged and the caller may act on `reason`. |
| `3` | The request was well-formed and could not be attempted now — the project is locked, the store is busy, a lease is held. Retrying later may succeed. |
| `4` | Motif failed unexpectedly. A bug, not a decision. |

The `2` and `3` split is the one that earns its keep: an agent must not retry a refusal, and must be
allowed to retry a lock.

### Versioning

The JSON surface is now a compatibility surface with real consumers, which is what the capability
negotiation in the deleted protocol existed to manage. It is managed here instead by additive discipline:

- New fields may be added to any projection at any time. Consumers ignore unknown fields.
- A field is never removed or retyped in place; a replacement is added beside it and the old one kept
  until every shipped consumer has moved.
- `reason` codes may be added. A consumer treats an unrecognised `reason` as a generic failure of the
  class its exit code names, which is why the exit code carries the retry decision and not the reason.

## What is deliberately not in this API

- **No in-process entry point for a host.** ADR 0040 decision 1: a FieldWorks-side surface runs the
  executable and reads JSON. It does not reference `SIL.Motif.Host`, `SIL.Motif.Worker`,
  `SIL.Motif.Projection`, `Microsoft.Data.Sqlite`, or the schema, and does not open `Project.motif.db` —
  not even to read.

  **One exception, and it is shapes only.** A consumer may reference `SIL.Motif.Contract` to deserialise
  this output into typed values — that is how FieldWorks will know what the fields are and render a diff
  rather than re-deriving the vocabulary. Contract keeps `netstandard2.0` for exactly this reason
  (ADR 0040 decision 3) and holds no behaviour that reaches storage.

  The response records now live in `SIL.Motif.Contract.Responses`, with the `LcmCache`-dependent builders
  left behind in `SIL.Motif.Projection`. `ResponseBindingTests` stands in for the consumer: it names no
  Motif namespace but Contract's, so a shape that ever needs a type from another assembly stops it
  compiling rather than failing quietly in a consumer nobody has built yet.
- **No access to the database as an interface.** `Project.motif.db` is an implementation detail shared by
  the CLI and the job runner, which ship together at one version. It is not a contract for anyone else.
- **No long-lived connection, no server, no port.** The job runner exists so that queued work outlives a
  CLI invocation. Nothing asks it anything; it claims rows.
- **No verb that mutates a project another process currently holds open.** ADR 0040 decisions 6 and 7:
  FieldWorks releases the project, the verb runs, FieldWorks reloads.

## Amendments

### 2026-08-28 — `store-cutover` is gone

[ADR 0041](adr/0041-the-database-is-the-only-store.md) decision 1 deletes the file store rather than
migrating it, so the verb that migrated it is deleted with it. It had never worked: it refused every
Proposal the CLI can author, because the operation kinds it validates against are registered by
`SIL.Motif.Runner`'s module initializers and only the `Commands` static constructor forces them to load.

The rest of ADR 0041 changes this contract further — every remaining verb gains a required `--project`,
`dry-run` becomes a job, and a set of cross-project job verbs joins the set. Those land with the tasks
that implement them; this note records only what has already been removed.

## Related

- [ADR 0041 — The database is the only store](adr/0041-the-database-is-the-only-store.md)
- [ADR 0040 — There is one API, and it is the CLI](adr/0040-one-api-the-cli.md)
- [ADR 0021 — The CLI is the full product surface](adr/0021-cli-is-the-full-surface-layer-1-churns.md)
- [ADR 0039 — Baseline and live-host authority](adr/0039-one-worker-baseline-and-live-host-authority.md),
  whose Baseline, queueing, and PanGloss decisions this contract still rests on
- [CLI-to-worker API surface](cli-worker-api-surface.md), superseded, kept for the refusal-fidelity
  analysis that motivated collapsing the boundary
