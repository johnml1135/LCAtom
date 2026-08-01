# PR-like collaboration architecture — review synthesis

*Adjudicated synthesis of four independent xhigh Luna reviews plus local source verification and
primary-source literature review on 2026-08-01. Recommendations are incorporated in the live plans;
this file preserves the evidence trail.*

## Reviews

The four read-only reviews covered:

1. CRDT/history/materialization theory and current Harmony/LcmCrdt code;
2. PR-style checks, review, governance, provenance, and AI controls;
3. linguistic domain coverage, text anchoring, normalization, and operation-family completeness;
4. an independent red-team of ownership, authority, and milestone structure.

The raw reports remain in `.tmp/luna-plan-review-2026-08-01/results/` for this worktree.

## Findings accepted

- CRDT convergence is not semantic validity. Unsafe invariant combinations need coordination,
  deterministic refusal, or a stronger operation.
- Replicated history, materialized state, and Proposal workflow are different state machines.
- Checks and approvals bind exact Proposal, baseline, artifact, tool, and policy revisions; changed
  inputs make them stale.
- AI review is typed and advisory by default. It cannot imply human or native-speaker judgement.
- Whole-project normalized Baseline Tokens are the safe v1 authorization default; scoped tokens are
  an optimization gated by a proven transitive read/effect closure.
- Cross-store apply is a recovery state machine, not one transaction.
- Strict grammar order needs stable identities, semantic placement intent, deterministic refusal,
  and real feeding/bleeding fixtures; scalar LWW order is not a fallback.
- MiniLcm/LibLCM correspondence is a versioned name, shape, capability, and lossiness crosswalk.
- Text mutation is not covered by the current manifest/generator plan and needs its own bounded
  context; text evidence may proceed earlier.
- Full CRDT-only creation of a new `.fwdata` may remain conditional, but selective bidirectional
  compatibility for every promoted domain is mandatory.
- Generated outputs need reproducible-build provenance linking the model file, manifest, crosswalk,
  generator, dependencies, and build.

## Adjudicated disagreements

Some reviewers inferred permanent FieldWorks authority from older glossary/README text. The current
owner direction and D1 instead establish LcmCrdt as the target collaborative authority. The plans now
distinguish that destination from the FieldWorks-hosted transition: LibLCM remains the invariant,
lifecycle, and compatibility authority when materializing a FieldWorks project, but is not a second
independent merge authority.

One review recommended a separate companion repository. The current owner direction resolves that
question: Motif is the product and application domain. Deployable components may live beside their
natural hosts, but Proposal/Check/Review/Decision semantics remain Motif-owned.

## Primary sources

- Bailis et al., [Invariant Confluence](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf)
- Kleppmann et al., [Local-first software](https://doi.org/10.1145/3359591.3359737)
- GitHub, [protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
- Google Docs, [working with suggestions](https://developers.google.com/workspace/docs/api/how-tos/suggestions)
- W3C, [Web Annotation Data Model](https://www.w3.org/TR/annotation-model/)
- Unicode, [Normalization](https://www.unicode.org/reports/tr15/) and
  [Text Segmentation](https://www.unicode.org/reports/tr29/)
- W3C, [PROV-DM](https://www.w3.org/TR/prov-dm/)
- SLSA, [provenance](https://slsa.dev/spec/v1.2/provenance)
- in-toto, [attestation statement](https://github.com/in-toto/attestation/blob/main/spec/v1/statement.md)
