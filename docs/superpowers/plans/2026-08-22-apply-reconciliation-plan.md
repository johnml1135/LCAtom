# Apply Authorization and Reconciliation Implementation Plan

> **AMENDED (2026-08-26) by [ADR 0040](../../adr/0040-one-api-the-cli.md).** Apply, its authorization, and reconciliation remain
> as designed; only the transport changes — the authorization no longer crosses a pipe, and the live host is
> always a Motif process. Read any step that names the worker client as naming an in-process call.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Make Apply an immediate, exact-evidence operation whose outcome can be recovered from project-owned
history after any worker/client disconnect.

**Architecture:** The worker validates durable workflow and issues a one-use authorization. The live host
passes it with its own `LcmCache` to the `netstandard2.0` Runner, which performs final Preflight and one UOW.
Positive applied-log facts reconcile automatically; unresolved disagreement becomes a local Conflict.

**Tech Stack:** Contract/Model/Runner `netstandard2.0`, Host/Worker `net10.0`, LibLCM, SQLite, xUnit.

---

### Task 1: Complete evidence and Receipt contracts

Every Apply must name exactly what was approved and leave enough permanent evidence to explain the result.

**Files:**
- Create: `src/SIL.Motif.Model/Apply/ApplyAuthorization.cs`
- Create: `src/SIL.Motif.Model/Apply/AssessmentDisposition.cs`
- Modify: `src/SIL.Motif.Model/Receipts/Receipt.cs`
- Modify: `src/SIL.Motif.Model/AppliedLog/AppliedLogEntry.cs`
- Test: `tests/SIL.Motif.Tests/Apply/ApplyEvidenceContractTests.cs`

- [ ] **Step 1: Write canonical binding tests**

Pin project identity, Proposal id/digest, Baseline Token, Dry Run anchor, Decision id/digest, Assessment
disposition, accepted warning, attempt id, issue/expiry, and nonce. Assert mutation of any field invalidates
the authorization and Receipt carries rationale, actor, before/result identities, and accepted warning.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: authorization and completed Receipt fields are absent.

- [ ] **Step 3: Implement opaque signed authorization data**

```csharp
public sealed record ApplyAuthorizationClaims(
    string ProjectIdentity,
    CanonicalId ProposalId,
    string IntentDigest,
    BaselineToken Baseline,
    BoundDryRunAnchor DryRun,
    string DecisionId,
    AssessmentDisposition Assessment,
    string AttemptId,
    string IssuedUtc,
    string ExpiresUtc,
    string Nonce);

public sealed record ApplyAuthorization(string OpaqueValue);
```

Keep signing, verification, and one-use consumption in the worker. The live host and Runner receive immutable
verified claims and recheck their live bindings, not the opaque value. Extend applied-log format version
rather than overloading the old parser, and preserve backward reading for reconciliation.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Model tests/SIL.Motif.Tests/Apply
git commit -m "feat: complete apply evidence contracts"
```

### Task 2: Enforce exact worker-side Apply policy

The worker decides whether the required workflow evidence exists, while never grading the grammar itself.

**Files:**
- Create: `src/SIL.Motif.Worker/Apply/ApplyPolicy.cs`
- Create: `src/SIL.Motif.Worker/Apply/ApplyAuthorizationIssuer.cs`
- Create: `src/SIL.Motif.Worker/Apply/ApplyAttemptRepository.cs`
- Create: `src/SIL.Motif.Worker/Apply/ApplyCommandHandler.cs`
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs`
- Test: `tests/SIL.Motif.Tests/Worker/ApplyPolicyTests.cs`

- [ ] **Step 1: Write the complete policy matrix**

Assert missing/stale Decision refuses, amended intent refuses, missing/queued/running/cancelled/tool-failed
Assessment refuses without force, the same states authorize with the narrow warning under force, a completed
poor Assessment authorizes without force, failed Dry Run and Conflict always refuse, authorization is one-use
and expires, and `authorization-issued` commits before issuance. Pin legal attempt transitions through
`mutation-started`, `runner-completed-in-cache`, `save-started`, `saved`, and `receipt-recorded`, plus terminal
`refused` and `needs-reconciliation`; every skipped or backward transition refuses.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: policy and issuer are absent.

- [ ] **Step 3: Implement policy without linguistic verdicts**

```csharp
public sealed class ApplyPolicy
{
    public ApplyPolicyResult Evaluate(
        ProposalWorkflowRecord proposal,
        bool forceUnavailableAssessment,
        DateTimeOffset now);
}

public sealed class ApplyAuthorizationIssuer
{
    public ApplyAuthorization Issue(ApplyPolicyResult accepted, TimeSpan lifetime);
    public ApplyAuthorizationClaims Consume(ApplyAuthorization authorization);
}
```

Sign with a per-worker-start secret held in protected memory; persist claims and nonce/consumed state, not the
secret. `ApplyCommandHandler` owns the connected request and records every transition through
`ApplyAttemptRepository`; the schema migration creates `ApplyAttempts` and consumed-nonce storage. On restart,
outstanding authorizations become reconciliation-required rather than reusable.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Worker tests/SIL.Motif.Tests/Worker
git commit -m "feat: issue exact one-use apply authorization"
```

### Task 3: Move final Preflight into Runner Apply

The last safety comparison belongs beside the actual mutation, against the live model the host already owns.

**Files:**
- Create: `src/SIL.Motif.Runner/Apply/ApplyRequest.cs`
- Create: `src/SIL.Motif.Runner/Apply/ApplyPreflight.cs`
- Modify: `src/SIL.Motif.Runner/Apply/ProposalApplier.cs`
- Test: `tests/SIL.Motif.Tests/Apply/ProposalApplierAuthorizationTests.cs`

- [ ] **Step 1: Write live-cache refusal and atomicity tests**

Assert exact project/intent/footprint/effect/projection/Runner/LibLCM binding, prerequisite history, malformed
or mismatched verified claims, model failure rollback, applied-log entry in the same UOW, and cancellation
ignored after mutation begins. Reuse refusal belongs to the worker issuer tests; Runner is storage-agnostic
and receives claims only after one-use consumption. Preserve already-applied idempotence from project history.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: new Apply request and final Preflight are absent.

- [ ] **Step 3: Implement storage-agnostic final Preflight**

```csharp
public sealed record ApplyRequest(
    Proposal Proposal,
    ApplyAuthorizationClaims Authorization,
    string ApplierIdentity,
    string Description);

public static class ProposalApplier
{
    public static Receipt Apply(LcmCache cache, ApplyRequest request);
}
```

Runner accepts claims already verified and consumed by the worker, rechecks their live semantic bindings, and opens exactly
one UOW only after non-mutating Preflight succeeds. Runner never opens SQLite, saves, checks a job, or infers
approval policy. Retain the old overload only during call-site migration, then delete it in plan 6.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Runner tests/SIL.Motif.Tests/Apply
git commit -m "feat: preflight authorized apply in runner"
```

### Task 4: Bound Apply gate acquisition and disconnect handling

Apply should happen now or fail quickly, and a lost connection must never cause the change to run twice.

**Files:**
- Create: `src/SIL.Motif.LiveHost/Apply/LiveApplyCoordinator.cs`
- Create: `src/SIL.Motif.LiveHost/Apply/IApplyPersistenceRecovery.cs`
- Modify: `src/SIL.Motif.Worker/Scheduling/ProjectLane.cs`
- Test: `tests/SIL.Motif.Tests/Host/LiveApplyCoordinatorTests.cs`

- [ ] **Step 1: Write timing and fault-injection tests**

Use a fake monotonic clock to prove five-second maximum, no durable Apply job, priority over unstarted refresh,
active capture completion, cancellation only before mutation, and every fault boundary. A Runner refusal
leaves the cache unchanged; Runner failure during its UOW requires discard/reload; failure after Runner
returns but before confirmed save becomes ambiguous; confirmed save plus lost Receipt is recovered from the
project applied log. Assert save failure fences editing and live Motif work until host recovery either
confirms the same save or unloads/reloads the cache; continuing on the mutated unsaved cache is forbidden. No
ambiguous boundary invokes Apply again.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: coordinator is absent.

- [ ] **Step 3: Implement immediate host-owned Apply**

```csharp
public sealed class LiveApplyCoordinator
{
    public Task<Receipt> ApplyAsync(
        LcmCache liveCache,
        ApplyRequest request,
        Func<LcmCache, Task> saveAsync,
        IApplyPersistenceRecovery persistenceRecovery,
        CancellationToken preparationCancellation);
}
```

`SIL.Motif.LiveHost` is the shared `netstandard2.0` LibLCM-aware assembly created in the Baseline plan, so both
the CLI Host and FieldWorks use this coordinator. Acquire the gate for at most five seconds. Report
`mutation-started` before calling Runner, `runner-completed-in-cache` after return, `save-started` before the
host save, and `saved` only after confirmed persistence. Report Receipt separately. A Runner exception forces
the host to discard or reload its cache. Any uncertain post-Runner outcome is `needs-reconciliation`; never
retry. `IApplyPersistenceRecovery` is supplied by the host and keeps the session fenced until it confirms the
same save or reloads the project; reconciliation reads the durable applied log before the fence clears.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.LiveHost src/SIL.Motif.Worker tests/SIL.Motif.Tests/Host
git commit -m "feat: coordinate immediate live apply"
```

### Task 5: Reconcile applied history and surface Conflict

When the project proves a change already landed, Motif should repair its records; when facts truly disagree,
it should explain the disagreement instead of guessing.

**Files:**
- Create: `src/SIL.Motif.Contract/Reconciliation/ReconciliationContracts.cs`
- Create: `src/SIL.Motif.Worker/Reconciliation/AppliedLogReconciler.cs`
- Create: `src/SIL.Motif.Worker/Reconciliation/ConflictRepository.cs`
- Create: `src/SIL.Motif.Projection/ConflictProjection.cs`
- Modify: `src/SIL.Motif.Projection/ProposalListProjection.cs`
- Modify: `src/SIL.Motif.Host/Store/MotifSchema.cs`
- Test: `tests/SIL.Motif.Tests/Worker/AppliedLogReconcilerTests.cs`
- Test: `tests/SIL.Motif.Tests/Projection/ConflictProjectionTests.cs`

- [ ] **Step 1: Write repair and disagreement tests**

Assert positive project entries mark Proposal applied, complete attempts, reconstruct minimal Receipt, preserve
unknown tombstones, advance an idempotent watermark, and never un-apply from absence alone. Assert true
disagreement creates deterministic explanation, sorts first, blocks affected dependency closure only, and
never auto-archives. Pin each resolution: accepting a project account with no applied entry preserves audit
but requires a fresh Dry Run and Decision; project-history repair requires exact live read-back equivalence to
stored Receipt and intent and writes an explicit reconciliation UOW; failed equivalence refuses repair; rerun
is offered only after resolution as not applied.

- [ ] **Step 2: Run red**

Run `./test.ps1`. Expected: reconciliation and Conflict types are absent.

- [ ] **Step 3: Implement positive-fact reconciliation**

```csharp
public sealed record AppliedLogDelta(
    string ProjectIdentity,
    string PriorWatermark,
    IReadOnlyList<AppliedLogEntry> Entries,
    string NewWatermark);

public sealed class AppliedLogReconciler
{
    public ReconciliationResult Reconcile(AppliedLogDelta delta);
}
```

Generate explanations from compared stored facts and timestamps, labeling causes as possibilities. Resolutions
are explicit worker commands: accept project, request verified project-history repair from the live host,
retry after not-applied resolution, or leave unresolved. The worker cannot write the project log; the live
host performs the repair UOW and save and returns its new watermark. `ConflictRepository` persists compared
facts, explanation inputs, resolution, and audit timestamps in the `Conflicts` schema migration.

- [ ] **Step 4: Run green and commit**

Run `./test.ps1`, then:

```powershell
git add src/SIL.Motif.Contract src/SIL.Motif.Worker src/SIL.Motif.Projection tests/SIL.Motif.Tests
git commit -m "feat: reconcile project apply history"
```
