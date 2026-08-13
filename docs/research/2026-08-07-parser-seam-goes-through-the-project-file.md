# Measured 2026-08-07 — the parser seam goes through the project file, not through FieldWorks

**In plain terms:** to find out what a grammar change did to parsing, we do **not** need any FieldWorks code.
PanGloss reads a real `.fwdata` project file directly — 253 ms to load and compile a grammar out of a 56 MB,
152,222-object project — and it answers in **FieldWorks GUIDs**, which is exactly what Motif needs to tie a parse
result back to the entry or rule a proposal touched. The other available route, handing PanGloss a HermitCrab
XML file, produces the same linguistic answers under invented names that cannot be tied to anything. So the
route is settled, and it is the cheaper one.

Two warnings came with it. One real project could not be parsed *at all*: its grammar overflowed the FST
engine's budget, and the fallback engine did not finish a single word in ten minutes. And the only path that
yields GUIDs today is the command-line tool, not the in-process library.

## What was checked, and how

`pangloss batch` / `assess` / `compare` from a release build, against real projects rather than fixtures:
the 56 MB, 152,222-object project from the FieldWorks checkout, and PanGloss's own `aweti.fwdata` and
checked-in HC XML samples.

## 1. There is no FieldWorks dependency, and that was not obvious

`HCLoader.Load(LcmCache, IHCLoadErrorLogger)` — the documented way to get a HermitCrab `Language` out of a
live cache — is `public static`, but it lives in **`Src/LexText/ParserCore/HCLoader.cs` in the FieldWorks
repository**, not in liblcm, and it pulls in `SIL.Machine.Morphology.HermitCrab`. Taking that route means
depending on FieldWorks application code from scope 1, or porting several thousand lines of grammar
extraction and maintaining the fork.

**It is unnecessary.** `pg-cli`'s `load_grammar` dispatches on file extension:

| input | path taken |
| --- | --- |
| `.fwdata` | `pg_fwdata::import_file` → snapshot → `pg_grammar::compile_project` |
| `.json` | `pg_snapshot::Snapshot::from_json` → `pg_grammar::compile_project` |
| anything else | `pg_grammar::load` — HermitCrab XML |

So a real project file goes straight to a compiled grammar in Rust. Measured on the 152,222-object project:
**`grammar_load_ms=252.9`**, then a 10.7 s FST build, then 40 words at a mean of 15 ms — against 12 ms for
the same language via the checked-in HC XML. Same performance, no FieldWorks assemblies.

## 2. The routes are not interchangeable, and only one of them is usable by Motif

`assess` on both grammar sources for the same 40 words, then `compare`:

```
outcomeDigestsAgree : false
summary             : { totalCases: 40, changedCases: 33, byCategory: { mixed: 33, unchanged: 7 } }
```

Every changed case is `added N / removed N` with **equal counts**, which is the signature of the same
analyses under different names rather than of different analyses. Inspecting them confirms it:

| word | HC XML route | project-file route |
| --- | --- | --- |
| `ya` | `pos84066` + `mrule128`, `entry1083` | `8d0461bd-…` + `603fc0f8-…`, `0832679c-…` |
| `pya` | `pos84066` + `mrule126`, `entry1083` | `8d0461bd-…` + `10aa030f-…`, `0832679c-…` |

Same `rootIndex`, same morpheme count, and a consistent mapping — `pos84066` ↔ `8d0461bd-…` and
`entry1083` ↔ `0832679c-…` in both words. **The linguistics agree; the identity namespace does not.**

**This is why the route matters rather than being a preference.** Motif's canonical ids are GUIDs, and
[ADR 0027](../adr/0027-what-counts-as-the-same-word-analysis.md) gates a comparison on the morph, the
category record and the inflection type — all of them objects identified by GUID. A parse result keyed by
`mrule128` cannot be tied to the entry a Proposal edited; one keyed by `603fc0f8-…` can.

`MOT-15` anticipated exactly this — *"HC XML uses session-scoped `Hvo` integers … unusable as a durable
interchange key, where the snapshot format uses FieldWorks GUIDs"* — and planned HC XML first as the cheap
step. **The measurement inverts that order: the cheap step produces answers Motif cannot use**, so for Motif
the snapshot/project-file route is not step 2, it is the only step.

## 3. The FFI cannot do this yet — only the CLI can

The C ABI exposes `hc_grammar_load(xml_utf8, len, …)` and nothing that takes a snapshot or a project file. So
**the GUID-keyed route is reachable today only by running the `pangloss` executable**, not by P/Invoke into
`pg-ffi`. That is `MOT-15`'s already-recorded ask — one new entry point, `hc_grammar_load_snapshot`, calling
`pg_grammar::compile_project` — now with a concrete reason: without it, scope 2 cannot host the parser
in-process on `net48`, and scope 1 must shell out.

Shelling out is acceptable for scope 1 and is what Motif should do first, behind an interface narrow enough
that an FFI implementation can replace it without touching callers.

## 4. Two real projects, two different failures — the fallback chain is not hypothetical

The owner's constraint is that Motif uses the FST engine pruned by HermitCrab, falling back to HermitCrab
alone for odd cases *"including the inability to compile the grammar into an FST."* That case appeared on the
first real project tried, and the fallback did not save it.

`aweti.fwdata` (11 MB, PanGloss's own sample):

- **FST engine: refused to compile.** *"grammar exceeds the foma-engine's eager-enumeration budget: composite
  lexc entries (fusion + interdigitation + structural) = 200500 (limit 200000)."* The error itself advises
  using the default engine, and offers `HC_ENUM_ENTRY_BUDGET` to raise the limit.
- **Default engine: zero of 15 words finished in ten minutes.** Grammar load was fine (`total_ms=123.7`);
  analysis never produced a line.

So on that project **neither mode is usable as configured**, and a coverage report would have nothing to say.
The 152,222-object project is fine on both. Whether Aweti is pathological by construction — it is a test sample — or
representative of a class of real projects is unknown and worth knowing, because it decides whether "fall
back to HermitCrab" is a sufficient answer or merely the second thing to try.

Also noted without conclusion: the project-file route reported **137 compiler warnings and one out-of-scope
reference** on the project where the HC XML route reported none, plus a skipped entry for *"circumfix cross-product
allomorphs … not implemented"*. That may mean the XML was pre-cleaned, or that the file route surfaces real
problems the XML route silently dropped. A coverage report must carry these through either way — a number
computed while 137 warnings were ignored is not a number about the grammar.

## What this settles, and what it opens

**Settled.** Motif's parser seam is: save the project (already synchronous as of yesterday), hand the
`.fwdata` path to PanGloss, receive GUID-keyed analyses. No FieldWorks assemblies, no HermitCrab dependency,
no vendoring, and no HC XML step.

**Open, and each is a measurement rather than a decision.** Whether Aweti's double failure is a class or a
curiosity. Whether the 137 warnings represent loss. And the FFI entry point, which scope 2 needs and scope 1
can live without.
