# Evidence ledger for the 107-question grill

*Adjudicated 2026-08-01 from four independent xhigh Luna reviews, repository inspection, and
primary-source research. This ledger classifies the existing identifiers; it does not silently make
owner decisions.*

## Outcome

| Disposition | Count | Treatment |
| --- | ---: | --- |
| `RESOLVED_FACT` | 47 | Do not ask again. Keep the stated conformance proof as plan work. |
| `EVIDENCE_TASK` | 27 | Do not ask as a preference. Run the named test, audit, or measurement. |
| `OWNER_DECISION` | 33 | Ask in the architecture-first order in the grill queue. |

`Q103` is retained as a stable historical identifier but is an exact duplicate of `Q44`; its one
useful implementation question is folded into Q44.

## Complete disposition

### Resolved principles (47)

`Q3, Q5, Q6, Q7, Q11, Q12, Q14, Q15, Q19, Q24, Q25, Q26, Q29, Q30, Q31,
Q32, Q34, Q35, Q36, Q37, Q39, Q43, Q45, Q46, Q51, Q55, Q56, Q58, Q59,
Q60, Q66, Q67, Q70, Q74, Q75, Q77, Q78, Q80, Q81, Q82, Q84, Q97, Q101,
Q102, Q104, Q105, Q106`.

These include: the loaded-cache host owns lifecycle and final writes; whole-project normalized
semantic tokens are the v1 default; Dry Runs and Assessments are immutable and independently stale;
approval binds intent and expected effects; no stale force-apply exists; recovery is an explicit
state machine; unknown/refused history is retained and never silently reactivated; PanGloss supplies
evidence but never merge authority; alpha variables use stable keys; the crosswalk is Motif-owned and
machine-readable; same names never prove equivalent semantics; unsafe grammar merges coordinate or
refuse; Motif owns the PR-like workflow; grammar is first; text authoring is a separate bounded
context; selective bidirectional compatibility—not full `.fwdata` export—is the promotion gate.

### Evidence tasks (27)

`Q4, Q8, Q9, Q10, Q16, Q17, Q18, Q20, Q22, Q27, Q28, Q40, Q48, Q49,
Q50, Q62, Q63, Q65, Q68, Q69, Q72, Q91, Q92, Q93, Q94, Q95, Q103`.

The work is concrete:

- enumerate supported writers and inject a collision through final compare/UOW/save;
- generate fail-closed Baseline Token coverage, cross-project custom-field vectors, and a transitive
  read/effect-closure mutation test before considering scoped authorization;
- compare physical project copies with `LcmCache.CreateCacheCopy`, stress cache isolation, and test
  workspace provenance, quarantine, rollback, and cache poisoning;
- fault-inject every boundary from authorization consumption through UOW, save, Harmony recording,
  Receipt completion, restart, and reconciliation;
- exhaust strict-anchor, concurrent-move, same-gap, positional-field, real feeding/bleeding, and
  diagnostic-identity cases across arrival orders;
- prove mixed-version opaque preservation and the Harmony atomic-group/payload model;
- benchmark cold/warm whole-project hashing on real small/medium/large projects and record p50/p95/
  p99 latency, CPU, memory, allocations, and I/O.

### Owner decisions (33)

`Q1, Q2, Q13, Q21, Q23, Q33, Q38, Q41, Q42, Q44, Q47, Q52, Q53, Q54,
Q57, Q61, Q64, Q71, Q73, Q76, Q79, Q83, Q85, Q86, Q87, Q88, Q89, Q90, Q96,
Q98, Q99, Q100, Q107`.

The primary irreducible choices are the v1 authority boundary and migration gate; Proposal-to-Harmony
atomic-group representation; strict move and concurrent move policy; policy registry/version rules;
trust boundary and payload tamper evidence; workspace retention and remote disclosure; recovery and
Receipt storage ownership; diagnostic public contract; MiniLcm vocabulary and morph-type capability;
threat/privacy model; staffing/sign-off; performance risk tolerance; telemetry; architectural
falsifiers; and typed AI authority.

## Stronger evidence and corrections

1. **“Always resolve” has a precise safe meaning.** History always converges and is retained; every
   materialization receives `Applied`, `Refused`, or `Deferred`. It cannot mean every semantic
   conflict auto-applies. Invariant-confluence theory requires coordination or refusal where
   invariants are not confluent. [Bailis et al.](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf)
2. **Strict ordering needs an explicit move operation.** List/tree CRDT research shows that naive
   reordering produces duplicates, cycles, or surprising outcomes. Stable identity and authored
   placement anchors are justified; the linguistic conflict policy remains ours to choose.
   [Moving elements in list CRDTs](https://doi.org/10.1145/3380787.3393677),
   [tree moves](https://doi.org/10.1109/TPDS.2021.3118603)
3. **A file lock is not the authority contract.** Delayed stale holders require fencing or a final
   version comparison. [The Chubby lock service](https://www.usenix.org/legacy/event/osdi06/tech/full_papers/burrows/burrows_html/)
4. **Cross-store apply is durable workflow, not exception handling.** The LibLCM UOW, `.fwdata`
   save, Harmony recording, and Receipt are separate boundaries. Recovery must classify uncertainty
   and never blindly replay. [Sagas](https://doi.org/10.1145/38713.38742),
   [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html)
5. **Morph-type creation is a proven capability mismatch.** LcmCrdt represents creation while the
   FieldWorks bridge rejects it because FieldWorks morph types are predefined. Q83 is now a narrow
   compatibility policy decision, not a terminology question.
6. **AI authority is risk- and role-specific.** Current NIST guidance supports declared human/AI
   roles, oversight, provenance, testing, and risk-tiered policy. GitHub's current Copilot review is
   comment-only and does not satisfy required approval. [NIST AI RMF Core](https://airc.nist.gov/airmf-resources/airmf/5-sec-core/),
   [NIST GenAI Profile](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.600-1.pdf),
   [GitHub Copilot review](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review)
7. **Text evidence now, authoring later is supported.** Durable segment identity, declared Unicode
   normalization/segmentation, standoff annotations, provenance, and explicit reanchoring/refusal
   are prerequisites. [W3C Web Annotation](https://www.w3.org/TR/annotation-model/),
   [Unicode UAX #15](https://unicode.org/reports/tr15/),
   [Unicode UAX #29](https://unicode.org/reports/tr29/)

## Evidence provenance

The detailed per-question reports are retained under
`.tmp/luna-grill-evidence-2026-08-01/runs/`. Their recommendations were checked against
[CONTEXT.md](../../CONTEXT.md), the live plans, ADRs, source/tests, and the earlier
[collaboration synthesis](2026-08-01-pr-like-collaboration-synthesis.md). Repository conclusions are
distinguished from literature constraints: research can bound a safe design, but it cannot choose
the project's staffing, privacy tolerance, public vocabulary, or product authority policy.
