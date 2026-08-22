# Motif Worker and Baseline Implementation Plan Set

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Build the accepted worker, Baseline, asynchronous Dry Run and Assessment, synchronous Apply, and
FieldWorks integration boundaries without changing Runner ownership rules.

**Architecture:** Six independently reviewable plans move process ownership outward from the current one-shot
CLI while preserving the existing Runner. The worker owns SQLite and durable coordination; a live host owns
`LcmCache`, save, lock, and Apply; PanGloss receives a fresh candidate export and never a persisted engine.

**Tech Stack:** C# 14, `net10.0`, `netstandard2.0`, `net48` consumer compatibility, named pipes,
`System.Text.Json`, `Microsoft.Data.Sqlite`, LibLCM, Windows Job Objects, xUnit.

---

## Binding inputs

These documents settle what the implementation must mean. Read them before choosing APIs or persistence
details.

- [ADR 0039](../../adr/0039-one-worker-baseline-and-live-host-authority.md)
- [worker, Baseline, Dry Run, and Apply design](../specs/2026-08-20-baseline-dry-run-session-design.md)
- [multiple-client version research](../../research/2026-08-22-multiple-client-versions-one-worker.md)
- [canonical vocabulary](../../../CONTEXT.md)
- [repository rules](../../../AGENTS.md)

The detailed design is the semantic authority. If a plan step conflicts with it, fix the plan before changing
code. Do not introduce `net8.0`, pass an `LcmCache` across a pipe, let the worker call Runner Apply against a
FieldWorks-owned project, persist a PanGloss engine, or make `--force` bypass anything except unavailable
Assessment evidence.

## Execution order

The work lands in six stages so each new owner and boundary is proven before another component depends on it.

1. [Worker protocol, client, launcher, and version negotiation](2026-08-22-worker-protocol-launcher-plan.md)
2. [Paired database, durable jobs, archive, and recovery](2026-08-22-worker-database-jobs-plan.md)
3. [Minimal Baseline capture and per-project scheduler](2026-08-22-baseline-project-scheduler-plan.md)
4. [PanGloss orchestration and machine resource envelope](2026-08-22-pangloss-worker-orchestration-plan.md)
5. [Apply Authorization, final Preflight, reconciliation, and Conflict](2026-08-22-apply-reconciliation-plan.md)
6. [CLI migration and FieldWorks integration package](2026-08-22-cli-fieldworks-worker-integration-plan.md)

Plans are sequential because each consumes durable contracts from the previous one. Within a plan, preserve
the red/green/commit order exactly and run `./test.ps1` at every commit boundary. Never use bare `dotnet build`
or `dotnet test`.

## Acceptance coverage

Every architecture promise has one plan responsible for proving it. Shared rows name both plans when the
contract and its final integration land separately.

| Design requirement | Owning plan |
| --- | --- |
| One Baseline supports twenty stable media-free Dry Runs | 3 |
| FieldWorks remains open outside brief capture | 3 and 6 |
| Streaming `netstandard2.0` Baseline adapter | 3 and 6 |
| Duplex live-host events and bounded one-use binary transfer | 1, 3, and 6 |
| CLI refusal for live operations while FieldWorks hosts | 3 and 6 |
| Pure authoring remains immediate; resolution is Baseline-bound or waits | 6 |
| Waiting refresh transfers after FieldWorks closes | 3 |
| Refresh accept/defer/decline and live edit-generation warning | 3 and 6 |
| Refresh barrier orders old/new Baselines | 3 |
| Apply waits five seconds, never queues, cancellation boundary | 5 |
| Exact evidence and every authorization/UOW/save/Receipt crash boundary | 5 and 6 |
| Poor Assessment advisory; unavailable evidence needs narrow force | 5 |
| Reconciliation repairs or creates local Conflict | 5 |
| Two machine PanGloss slots at 25 percent; per-user FIFO across projects | 4 |
| Retry, startup cleanup, archive, workspace eviction | 2 and 4 |
| Compatible mixed-version clients; one database owner | 1 and 2 |
| Transactional Proposal/Corpus/Assessment migration; newer schema refusal; no downgrade | 2 |
| Model-owned media references delete; linked bytes remain; every family classified | 3 and 6 |
| Same-identity clones at different paths do not collide | 2 and 3 |
| Managed move carries the pair; managed or unmanaged duplicate starts fresh | 6 |

## Integration gate

The combined result is ready only when the repository's own full gate passes and the runtime matrix remains
unchanged.

- [ ] Run `./test.ps1` from the repository root.

Expected: comment hygiene reports zero violations, compilation has zero errors, every runnable test passes,
and only the existing PanGloss-dependent tests may skip when the executable is unavailable.

- [ ] Run `git diff --check`.

Expected: no whitespace errors.

- [ ] Verify target frameworks.

Run:

```powershell
rg -n "<TargetFrameworks?>" src tests
```

Expected: only `net10.0`, `netstandard2.0`, `net48`, and the existing Runner combination
`netstandard2.0;net10.0`; no `net8.0`.

- [ ] Commit the completed architecture as one reviewed integration commit after the six plan branches have
  landed cleanly.

```powershell
git add src tests Motif.sln docs
git commit -m "feat: add local motif worker architecture"
```
