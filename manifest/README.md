# Coverage manifest

## Companion files

Four files sit alongside `liblcm-inventory.tsv`, in the same dialect (tab-separated, double-quoted, CRLF).

**`fieldworks-labels.tsv` — harvested, not authored.** What FieldWorks already shows a linguist for a given
`(class, field)`, scraped from `strings-en.xml`, the `.fwlayout` slice system and the Lists tool config by
`spikes/SIL.Motif.Spikes.LabelHarvest`. One row per *distinct* label, because the same field carries different
labels in different layouts — `MoInflAffixSlot.Name` is "Name" in one and "Slot Name" in another — and picking
one silently would bake a view-specific label into a contract. 768 rows covering 193 of 494 in-scope fields
(39.1%); 212 rows flagged `ambiguous`. **These are labels, not descriptions: only 20 rows carry any prose.**

**`kind-descriptions.tsv` — copied from a source, not written.** One sentence per authorable field saying
*when an agent should reach for this operation*, which is the job a two-word label cannot do. Required by
[ADR 0023](../docs/adr/0023-derived-kind-names-required-descriptions.md) decision 5 as amended: the build
fails if a description is missing **or if it merely restates the label**. Written per family as it ships;
93 rows today.

The `Reviewed` column records provenance, and [issue D8](../docs/issues.md) is why it is not optional — the
first large batch of freely-paraphrased descriptions was 8% wrong, four of them saying the exact opposite of
what the model means, and a backwards sentence reads just as fluently as a correct one. Five values:

| `Reviewed` | Meaning | `Source`/`SourceDetail` |
| --- | --- | --- |
| `sourced` | copied from a cited upstream sentence, not yet reviewed by a linguist | required |
| `hand-corrected` | corrected by a human against a cited source after an automated error; regeneration preserves the text verbatim | required |
| `adapted` | derived from a **sibling field's** cited source by a checked-in substitution rule, for a field the model documents only once per family | required; cites the sibling, the substitution, and the licence for adapting |
| `unsourced` | no upstream source found yet; the text is an unverified claim | must be empty |
| `no-source-exists` | searched for exhaustively and found nowhere | `none (searched)`, with the search itself in `SourceDetail` |

`SourceHash` is a `sha256:` digest of **the cited fragment** — the upstream sentence itself, not the whole
file and not our copy of it. For a `sourced` row the two texts are the same, so the digest is merely
convenient. It earns its place on the rows whose text deliberately differs from their source: comparing our
prose against upstream cannot detect that upstream moved when our prose is *supposed* to differ, and
`hand-corrected` and `adapted` rows are exactly that case. An `adapted` row stores its **sibling's** digest,
so the two rows cannot drift apart unnoticed.

Hashing the fragment rather than the file is deliberate. `MasterLCModel.xml` backs 66 of these rows, so a
file-level digest there would flag all 66 for a change to any one of them, and a check that cries wolf 66
times is a check nobody reads.

`refresh-descriptions` (below) is what writes this file; it never invents prose, so a row it cannot source
keeps whatever text it already had.

**`fieldworks-help-descriptions.tsv` — harvested, Windows-only, checked in for exactly that reason.** The
`Description:` row of the FieldWorks help page for each of nine fields, pulled out of
`DistFiles/Helps/FieldWorks_Language_Explorer_Help.chm` — the only place several of these sentences exist.
Opening a `.chm` needs `hh.exe`, which is Windows-only, so the extraction is a dev-time step whose output is
committed: **nothing in the build, the tests, or the runtime ever touches the help file.** Re-harvest with
`dotnet run --project src/SIL.Motif.Generator -- harvest-help` (add a path to an already-extracted help tree
to skip the Windows-only step), then `refresh-descriptions`, then commit both files.

**`source-pins.tsv` — the three files the descriptions are copied out of, pinned by content.** One row per
file: `MasterLCModel.xml`, `ContextHelp.xml`, and the compiled help file, each with a `sha256:` digest of
its bytes, plus the release it came from (liblcm by pinned package version, FieldWorks by
`git describe --tags --long` — both repos sit some commits past a tag, so the long form is deliberate).

`refresh-descriptions` **fails** rather than re-harvesting when one of those files has changed, naming the
file and both digests; re-run with `--accept-source-move` to upgrade deliberately, which re-pins and prints
every description whose upstream fragment drifted. That report is the point: a reworded upstream sentence
still reads fluently, so nothing downstream would otherwise notice that the sentence a reviewer signed off
on has been replaced.

**The check is on content, not on commits** — and that is a correction, not a preference. The first version
pinned each repository at a commit and refused to run within the hour, because the FieldWorks checkout had
advanced by one commit adding three unrelated test files. A pin that fires on every commit to a large
repository trains its reader to click through it. A project that moves without changing any of these three
files is now reported and re-pinned, not treated as a reason to stop.

The `.chm` is pinned even though `refresh-descriptions` never opens it: the checked-in help harvest is
derived from it, so a changed digest there is the only thing that can say "re-run `harvest-help`".

## `liblcm-inventory.tsv` — the raw inventory (generated, checked in)

Every property of every LibLCM class, generated mechanically from
`MasterLCModel.xml` as shipped inside the pinned `SIL.LCModel` NuGet package
(`contentFiles/MasterLCModel.xml`) — so the inventory is version-locked to
exactly the LibLCM assembly Motif references, with no dependency on a sibling
repository checkout.

898 rows, 19 columns. The first nine are the raw inventory, generated mechanically; the other ten
are added by [`classify.ps1`](classify.ps1) (see "The manifest is now the type system" below) and are
blank/`n/a` for every row where `Scope != in`.

| Column | Meaning |
| --- | --- |
| `Class` | LibLCM class name |
| `Base` | its base class |
| `Abstract` | `true` if the class cannot be instantiated |
| `Scope` | `in` (domain-reachable, authorable), `trace` (derivation-trace — analyzer output, never authored), `out` |
| `ScopeReason` | why that scope was assigned |
| `Field` | property name |
| `Kind` | `basic` (value) / `owning` / `rel` (reference) |
| `Sig` | value type, or destination class for relations |
| `Card` | `atomic` / `col` / `seq` |
| `HcReferenced` | raw name-only join against [`hcloader-surface.tsv`](hcloader-surface.tsv): `name-referenced` or `no`. Precursor to `HcReachable`, which corrects the false positives a bare-name join produces (see issue D1 in [issues.md](../docs/issues.md)). |
| `Construct` | the layer-1 construct this field belongs to (`lexEntry`, `msa`, `phoneme`, `rewriteRule`, …; the possibility family fans out to a multi-construct string) — see [ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md) and [API surface layer 1](../docs/api-surface-layer1.md). 54 distinct constructs across the in-scope rows. |
| `Group` | `grammar` / `lexical` / `system` / `lists` / `analysis` — **the editorial domain: who should review a change**, per [ADR 0024](../docs/adr/0024-group-is-derived-domain-is-editorial.md). It is *not* the first segment of an operation's name; that segment is derived from the declaring class and the two deliberately disagree on 53 rows. `analysis` was added by [ADR 0025](../docs/adr/0025-parser-first-build-order.md). |
| `Classification` | what kind of field this is for kind-generation purposes: `semantic-operation` (core authorable content), `supporting-detail` (secondary/administrative), `unsupported` (explicitly not offered as a control), `internal` (import residue, singleton roots), `derived-read-only` (engine-computed), `runner-bookkeeping` (the applied-log's reuse of `CmResource`), `observable-not-authorable` (engine-maintained reverse index). |
| `ComparisonClass` | how instances of this field compare for drift/effect purposes: `unordered`, `positional`, `index-as-identity` (alpha-variable pools — [issue B8](../docs/issues.md)), `feeding` (order encodes rule interaction — [issue B8](../docs/issues.md)). |
| `Verbs` | the CRUD verb subset this field's `(Kind, Card)` shape generates (`set|clear`, `create|delete`, `create|delete|move|reparent`, `addRef|removeRef`, `addRef|removeRef|move`), or `n/a` when `Classification` marks the field non-authorable. |
| `HcReachable` | does `HCLoader` actually read this field — `yes` / `no` / `unconfirmed`, corrected from `HcReferenced` against the curated surface map (see [the HC grammar map](../docs/hc-grammar-map.md)). |
| `EnumValues` | for the 28 in-scope `Integer` fields only: a confirmed `value=Name;...` mapping, `unknown` (named-enum-shaped but no confirming code found), or blank (a magnitude, not an enumeration) — [issue B7](../docs/issues.md). |
| `Rationale` | required prose for every in-scope row, citing the classification decision and folding in any `ComparisonClass`/`HcReachable`/`EnumValues` notes. Distinguishes a cited source from a generic field-name-heuristic default — see [issue B18](../docs/issues.md). |

### `Scope` is computed, not guessed

Scope is **owning-edge reachability** from the domain roots — `LexDb`, `MoMorphData`, `PhPhonData`,
`FsFeatureSystem`, `PartOfSpeech`, and `LexEntry` (the last because `LexDb` only *references* entries,
never owns them) — plus the declared `CmPossibility` subclasses that in-scope lists use, minus the
derivation-trace family. Polymorphic expansion is deliberately blocked at broad bases (`CmObject`,
`CmPossibility`, `CmPossibilityList`, …), since a list's legal item class is governed per-instance by
`ItemClsid` at runtime rather than by the schema.

A naming heuristic was wrong in **both** directions, which is why this is computed:

- **Admitted what it shouldn't:** the `MoDeriv*` / `*App` derivation-trace family (9 classes, 28 props)
  looks like grammar but is reachable only from `WfiAnalysis.Derivation`, an out-of-scope interlinear
  class. It is analyzer output. Tagged `Scope=trace`.
- **Excluded what it shouldn't:** `CmPossibility`, `CmPossibilityList`, `CmMedia`, `CmPicture`,
  `CmTranslation`, `CmResource` (the applied-log), `StText`/`StPara` — no domain prefix, but they back
  in-scope fields.

**In scope: 494 properties across 100 classes** (494 = 214 `basic` + 148 `owning` + 132 `rel`). Was 473
across 95 until [ADR 0025](../docs/adr/0025-parser-first-build-order.md) brought in 21 analysis rows —
the approval half of word analysis, which has durable identity. Occurrence assignment
(`Segment.Analyses`) remains out. Basic —
MultiUnicode 52, MultiString 51, Unicode 35, Integer 28, Boolean 25, String 10, Time 5, Guid 2,
TextPropBinary 1. Relations — owning/atomic 69, rel/atomic 57, owning/col 39, rel/col 38, owning/seq 33,
rel/seq 28. Excluded as `trace`: 28 props across 9 classes.

An earlier count of 478 properties across 96 classes included 5 `TextTag` rows later demoted as a
scope over-inclusion — see [issue D5](../docs/issues.md). The numbers above are post-D5 and were
recomputed directly from the TSV, not adjusted by hand from the old figures.

**This file is the drift-detection artifact.** Regenerating it after a LibLCM package bump must produce
no diff; any diff is a model change requiring review and (re)classification.

## The manifest is now the type system

Classification has shipped (commit `66fe792`). Every in-scope row carries all ten classification
columns — **zero in-scope rows are unclassified** — turning `liblcm-inventory.tsv` from a raw
inventory into the type system the (not-yet-written) kind generator is meant to read: per
[ADR 0009](../docs/adr/0009-layered-api-primitives-and-composers.md) and
[API surface layer 1](../docs/api-surface-layer1.md), each field's `Construct`, `Classification`,
`ComparisonClass`, `Verbs`, `HcReachable` (does `HCLoader` read it — see
[the HC grammar map](../docs/hc-grammar-map.md)) and `EnumValues` (for the 28
in-scope `Integer` fields), and `Rationale` are all populated. See the column table above for what
each one means and the value set it takes.

[`classify.ps1`](classify.ps1) is the mechanized, checked-in, re-runnable producer of these columns.
**But it has fallen behind the file, and rerunning it now loses work:** as of 2026-08-06 it rewrites 26
rows — the `CmAgent`/`TextTag`/`Wfi*` families — reverting the `Group`/`HcReachable`/`Rationale` values
that [ADR 0025](../docs/adr/0025-parser-first-build-order.md)'s analysis-approval scoping set by hand,
because the script does not know about that ADR. Treat it as a **first-pass tool**, and diff its output
against the committed file before accepting it. See [issue D7](../docs/issues.md).

### What genuinely still remains

- **No kind generator yet.** The manifest is the type system a generator would read, but nothing in
  `src/` reads `Construct`/`Classification`/`Verbs` to emit operation kinds. Today's runner still
  implements exactly one hand-written operation, `lexical/lexSense/setGloss`.
- **`manifest/generate-inventory.ps1` does not exist.** The raw first-nine-column inventory was
  produced by ad-hoc commands rather than a committed script, so the drift gate this file's opening
  section promises ("regenerating it after a LibLCM package bump must produce no diff") cannot
  actually be re-run yet. Tracked as [issue D6](../docs/issues.md).
- **Confidence caveats.** `HcReachable` is `unconfirmed` for 7 in-scope rows pending an HCLoader-read
  citation ([issue B17](../docs/issues.md) calls out `MoMorphSynAnalysis.Components`/`GlossBundle`
  specifically). Most rows carry no citation — **but this stopped being a risk on 2026-08-05.** The
  generator no longer trusts these columns: `Verbs` and `ComparisonClass` are **derived** from LibLCM's own
  structural declarations with five cited exceptions, and the build fails on any unexplained departure
  ([ADR 0022](../docs/adr/0022-structure-is-derived-policy-is-five-rows.md)); the operation name is derived
  from the declaring class ([ADR 0023](../docs/adr/0023-derived-kind-names-required-descriptions.md)). A
  missing citation on a computed value is not a defect. What still rests on human judgement, and where a
  citation therefore earns its place: `Scope`, `Construct` (what ships together), `Group` (who reviews), and
  the five order-carries-meaning rows.
