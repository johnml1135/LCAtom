# ADR 0027 — A passing test means the parser agrees about the morphology, not about the meaning

**Status:** accepted, 2026-08-05. Defines the comparison [ADR 0025](0025-parser-first-build-order.md)'s
acceptance test depends on. Resolves `I35a` and `I35b`.
Evidence: [what is a proper word analysis](../research/2026-08-05-what-is-a-proper-word-analysis.md).

## Context

ADR 0025 committed to a test suite where a **human-approved word analysis** is the assertion and a parser run
is the thing under test: if the parser cannot produce an analysis a human approved, that is a failing test.
FieldWorks already computes it as `NumUserApprovedAnalysesMissing`.

That rests entirely on when two analyses count as *the same*, and **an analysis is not a string of morphemes.**
For each piece of the word FieldWorks records four separate claims — the form (which allomorph), which
dictionary entry, **which sense of that entry**, and which grammatical-category record (the MSA) — plus, for
the whole word, a part of speech, optional grammatical features, and word-level glosses.

The comparison that answers the question ignores two of those. `ParseAnalysis.MatchesIWfiAnalysis`
(`FieldWorks/Src/LexText/ParserCore/ParseResult.cs:102-133`) compares morph-bundle count and, per bundle,
`MorphRA`, `MsaRA`, `InflTypeRA`, plus a guessed-string special case. It never compares `SenseRA` or
`CategoryRA`.

That looked wrong, because **the sense is what FieldWorks itself draws the morpheme-gloss line from**
(`InterlinVc.cs:1984`, `:2001`) — the line a linguist reads most.

## What the investigation established

**It is deliberate and structural, not an oversight.** Verified: `ParseFiler.cs` — the code that files parser
output — contains **no assignment to `SenseRA` or `CategoryRA` at all**. Only the human approval path sets
them (`SandboxBase.GetRealyAnalysisMethod.cs:383` sets `CategoryRA`, `:445` sets `mb.SenseRA`). The field
selection traces back to a 2002 SQL Server stored procedure (`UpdWfiAnalysisAndEval$`) whose data contract
never carried either field.

So comparing sense would compare a human judgement against a field the parser **cannot populate by
construction**. Every test would fail, for a reason that is nothing to do with the grammar.

**And a morphological parser has no basis to choose a sense.** It can determine which entry a piece came from
and what category it carries, because that is morphology. "Which of the five meanings of *run* is this" is a
semantic judgement with no morphological evidence behind it.

**PanGloss cannot express it either.** Its `AnalysisIdentity` (`pg-assess/src/identity.rs:36-44`) has **no
per-morpheme sense field**. So a sense-sensitive gate is not merely undesirable — it is unimplementable
against the engine we intend to use.

**There are two comparisons in the codebase, and this repo already said so.** `I35b` in
[grill-plan-a.md](../grill-plan-a.md) had both of them written down, with the field lists, before this
investigation started. A pass of mine grepped for them, did not find the second, and reported the
two-comparison claim as unsupported — twice — contradicting a finding already recorded here:

| | `MatchesIWfiAnalysis` | `WfiWordformServices.DuplicateAnalyses` |
| --- | --- | --- |
| Location | `ParseResult.cs:102-133` | `liblcm/.../WfiWordformServices.cs:318-345` |
| Job | parser output vs. human analysis | merge duplicate analyses *within* the project |
| `MorphRA`, `MsaRA` | ✅ | ✅ |
| `InflTypeRA` | ✅ | ❌ |
| `SenseRA` | ❌ | ✅ |
| `CategoryRA` | ❌ | ✅ |

They disagree on exactly the fields in question — but they answer different questions. One compares *across*
representations where one side structurally lacks two fields; the other compares two objects of the same kind,
both human-authored, where those fields exist on both sides. **Not a contradiction: two tools for two jobs.**

## Decision

### 1. The pass/fail gate is `MatchesIWfiAnalysis`'s shape

Morph-bundle count, and per bundle `MorphRA`, `MsaRA`, `InflTypeRA`, with the guessed-string handling. This is
what FieldWorks already reports, so a linguist sees the same verdict from Motif that they see from the tool
they already trust.

### 2. Sense and word-level category are reported, and do not gate

They are real human judgements and worth surfacing — "the parser found this breakdown; the human also
attached it to sense 2 of *run*, which the parser does not opine on." That is a diagnostic, not a failure.
Reporting them costs nothing and hiding them would throw away the linguist's most visible work.

### 3. State what a green result actually claims

**"The parser agrees about the morphology."** Not "the parser agrees with the linguist." The difference is
material: a human's sense and category choices are extra information they supplied, not something the parser
was asked about, so a passing test says nothing about them.

Any report, CLI output or UI that shows this verdict must say so. A number labelled "analyses the parser
cannot produce" invites being read as "analyses the parser got wrong", and the gap between those two readings
is exactly what this ADR exists to pin down.

## Consequences

- **`I35a` and `I35b` are resolved.** The "false agreement" worry — that PanGloss's coarser identity would call
  two different analyses the same — is real but correctly scoped: it can only cause false agreement about
  *sense*, which is not under test and is reported separately. It cannot cause false agreement about
  morphology, which is what the gate checks.
- **A limitation is declared rather than discovered.** Sense-sensitive testing is unavailable until PanGloss's
  analysis identity carries a sense. That is a possible future ask on PanGloss, not a defect in this design,
  and it belongs in `plan-cross-repo.md` if it is ever wanted.
- **Corroboration for the manifest classification.** `DuplicateAnalyses` refuses to merge when
  `CompoundRuleApps`, `InflTemplateApps`, `Stems`, `Derivation` or `MsFeatures` hold data, commenting that
  those fields are *"currently unused… play safe"* (`WfiWordformServices.cs:327-334`). We classified the first
  four as `derived-read-only` when bringing analysis into scope ([ADR 0025](0025-parser-first-build-order.md));
  liblcm's own comment agrees they are not live human-authored data.
- **A process note, recorded because it cost two wrong statements.** The second comparison was already
  documented in `I35b` with its exact field list. I grepped for it with patterns like `SenseRA ==`, missed
  `bundle1.SenseRA != bundle2.SenseRA`, and reported the two-comparison claim as unsupported — without
  checking the grill queue, which already held the answer. Two lessons, and the second is the bigger one:
  a failed grep is weak evidence of absence and should be reported as *“I did not find it”*; and **the
  repo's own open-questions file is a source to read before contradicting it**, not after.
