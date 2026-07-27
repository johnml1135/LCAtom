# Path 2 — LCAtom becomes the one API surface

*Research report. Scope: whether LCAtom, as designed and as built, can serve as the single, reliable,
cross-platform API surface over FieldWorks language data, with Android and Linux support as a
non-negotiable requirement. Companion candidate paths (MiniLcm-centric, and any third option) are
covered by other agents and are out of scope here, except where LCAtom's own documents already compare
against MiniLcm — those comparisons are cited, not re-derived.*

**Marker convention:** `[VERIFIED]` — read directly from source in this session, cited `path:line`.
`[INFERRED]` — a reasonable conclusion from verified facts, not itself read from a running system or an
authoritative external record. Every load-bearing claim carries one of these tags.

## Bottom line, stated first

LCAtom, as built and as designed, **cannot reach Android on its own.** Its Runner and Host are, and are
designed to remain, thin layers over `SIL.LCModel`/`LcmCache` `[VERIFIED — AGENTS.md:39-40]`, and LibLCM
itself requires a native ICU4C build (SIL's custom "fw" variant, version 70) that exists today only as a
Windows NuGet binary and a Debian/Ubuntu apt package `[VERIFIED — see §2 below]`. No Android build of
that native dependency exists anywhere in the three repositories examined, and the one shipping product
that already tried to put FieldWorks data on a phone — FwLite/MiniLcm — solved this by **not** running
LibLCM on the device at all: `IncludeFwDataBridge` is compiled out for every non-Windows target
`[VERIFIED — FwLiteMaui.csproj:26-27]`. LCAtom's own internal design document reaches the identical
conclusion about itself: mobile can carry an LCAtom change set as an opaque, unopened payload, but
*interpreting* one — which is the entire point of "the runner gives that change set one meaning"
(`README.md:18`) — "happens where LibLCM is" `[VERIFIED — one-api-problem.md:95-97]`. So "LCAtom is the
one API surface" is achievable only in a specific, narrower sense than the phrase suggests: one *change
format*, one *C# execution engine*, running on Windows and Linux, with Android (and iOS) reached only
through a client/server split that turns the phone into a thin, non-interpreting carrier of change sets
authored or applied elsewhere. That is not a failure of imagination that better engineering fixes; it is
a direct consequence of native-library availability that no amount of C# portability work removes. The
rest of this report lays out the evidence and what that split would actually cost.

---

## 1. What "LCAtom is the one API surface" would actually mean

LCAtom is not a CRUD API and does not claim to be one. It is explicit about this in its own product
boundary: it does not own "opening, saving, closing, locking, backing up, or disposing FieldWorks
projects" and does not own "UI behavior" `[VERIFIED — README.md:87-93]`. Its three artifacts are a
**Canonical Change Set** (portable ordered semantic intent), a **Change Set Assessment** (read-only
evaluation of one intent against one baseline), and an **Application Receipt** (the realized atomic
transition) `[VERIFIED — architecture.md:100-131]`. There is no "get this entry," "list these senses," or
"set this gloss as I type" verb anywhere in the shipped or designed surface — the entire vocabulary is
`create / ensure / set / clear / addRef / removeRef / move / reparent / delete / merge / replace`, applied
to a Change Set that is authored, assessed, and only then atomically applied
`[VERIFIED — change-set-contract.md:89-141]`.

This is a genuinely different *kind* of API from a live-editing surface, and LCAtom's own comparison
document says so without hedging: *"MiniLcm is nouns and verbs — `CreateEntry`, `UpdateSense`,
`DeletePartOfSpeech` — executed immediately. LCAtom is one noun, a change set, carrying a generated kind
namespace, assessed before it is applied, hashed, reviewable, and separable into Change Set / Assessment /
Receipt."* `[VERIFIED — one-api-problem.md:62-67]` The same document frames the two as structurally
different jobs — "a query/interactive-edit API" versus "a change-authorship/reasoning/review API" — and
recommends against collapsing them, calling the alternative "one ring with a hole in it"
`[VERIFIED — one-api-problem.md:44-45, 79-98]`.

**Direct answer:** LCAtom does not become the CRUD API, and the repository's own most recent design work
(dated the same day as this brief, 2026-07-27) explicitly rules that out as the goal. What LCAtom proposes
instead is narrower and, if taken at face value, more defensible: **one canonical *change format* and one
canonical *execution engine* for describing and realizing a change to a FieldWorks project — not one
interface for touching the project live.** Whether that is honestly "one API surface" depends entirely on
what the reader means by the phrase. As a *change-authorship and review* surface — the thing a person or
an AI uses to propose, inspect, and commit an edit — it is one surface, singular, by design. As *the* API
surface over FieldWorks data in the everyday sense of "how do I read this entry and show it in a UI" — it
is not one surface at all, because that job stays with something else (today, MiniLcm; in a
FieldWorks-desktop-only world, LibLCM itself). A reader who hears "LCAtom is the one API surface" and
expects it to replace live queries and interactive editing has been misled; a reader who hears it as "one
canonical way to propose and land a reviewable change" has been told the truth. The report's remaining
sections take the narrower, honest reading, because that is what the repository itself commits to.

---

## 2. How LCAtom reaches Android and Linux

### 2a. Does LibLCM load on Linux today?

**Yes, on a conventional Linux server/desktop, with a real dependency to manage — not "no."** LibLCM's CI
matrix is `[windows-latest, ubuntu-22.04]` `[VERIFIED — liblcm/.github/workflows/ci-cd.yml:24]`, every
LibLCM library multi-targets `netstandard2.0;net462;net8.0`
`[VERIFIED — liblcm/src/SIL.LCModel/SIL.LCModel.csproj:4`, and equivalently for `.Core`, `.FixData`,
`.Utils`]`, and the pipeline actually builds and runs the test suite on Ubuntu — not merely compiles it:
`dotnet test --no-restore --no-build -p:ParallelizeAssembly=false --configuration Release ...`
`[VERIFIED — liblcm/.github/workflows/ci-cd.yml:70-73]`, gated by a downstream job
(`publish-test-results`) with `action_fail: true` `[VERIFIED — liblcm/.github/workflows/ci-cd.yml:113-120]`
— i.e. a failing test on Linux fails the build, not just a soft warning. I did not execute this pipeline
myself and cannot independently confirm today's run is green `[INFERRED — the workflow is the CI gate on
`develop`/`master`; a long-broken Linux leg would be a visible, load-bearing regression, but I have no
direct evidence of the latest run's status]`.

The `[LibLCM has no `Reflection.Emit` and only one `DllImport`]` claim from the ground truth is correct as
far as it goes but incomplete: a second `DllImport` exists, and it is the more consequential one for
cross-platform reach. `SIL.LCModel.Core.Text.CustomIcu` declares
`private const string IcuucDllName = "icuuc" + Version + ".dll";` with `Version = "70"`
`[VERIFIED — liblcm/src/SIL.LCModel.Core/Text/CustomIcu.cs:28-33]`, and the P/Invoke signature is
`[DllImport(IcuucDllName, EntryPoint = "SilIcuInit", ...)]` at `CustomIcu.cs:260`
`[VERIFIED — grep confirmed this second DllImport site]`. This is not the generic system ICU that ships
with most Linux distributions — it is SIL's own custom ICU4C build, carrying FieldWorks-specific
normalization data (`nfc_fw.nrm`/`nfkc_fw.nrm`, referenced by name in
`change-set-contract.md:418` — `"nfc_fw"`), version-pinned to major version 70
`[VERIFIED — CustomIcu.cs:29,32]`. On Windows this native binary ships as NuGet packages —
`icu4c.win.fw.bin` and `icu4c.win.fw.lib`, present in the local NuGet cache at version `70.1.152`, with
`build/win-x64` and `build/win-x86` folders only
`[VERIFIED — /c/Users/johnm/.nuget/packages/icu4c.win.fw.bin/70.1.152/build/*]`. On Linux, the CI
workflow gets the equivalent binary a completely different way: `sudo apt-get install icu-fw` from a
SIL-operated apt repository, gated to `ubuntu-22.04` only
`[VERIFIED — liblcm/.github/workflows/ci-cd.yml:40-44]`. No NuGet package providing a Linux build of this
custom ICU exists in the local package cache alongside the Windows ones
`[VERIFIED — searched `~/.nuget/packages` for `*icu4c*fw*`; only `icu4c.win.fw.bin`/`.lib` found]`.
(Separately, `microsoft.icu.icu4c.runtime.linux-x64`/`linux-arm64` packages exist in the cache, but these
are Microsoft's stock ICU builds, unrelated to the `icuuc70`-named, `nfc_fw`-carrying SIL build LibLCM's
`DllImport` actually names — `[INFERRED]` from the naming and from LibLCM's own apt-based Linux CI step,
which would be unnecessary if the Microsoft package already satisfied the dependency.)

**Net finding:** LibLCM genuinely runs LCAtom's own dependency chain (`net8.0`, the pinned TFM) on Linux
in CI today, which is real, verified, positive evidence for this path — the ground truth's "no
`Reflection.Emit`, only one `DllImport`" undersold the actual native surface by one dependency, but that
dependency is *solved* for conventional Linux (apt package exists and is exercised by a real, gating test
suite). It is not solved in a way that travels to Android.

### 2b. What would Android require?

Android is not "Linux with a different package manager" for this purpose. Three separate facts converge:

1. **The native ICU dependency has no Android build anywhere in evidence.** The `icu-fw` apt package is
   Ubuntu-specific; the Windows NuGet packages ship `win-x64`/`win-x86` binaries only. Getting LibLCM
   running on Android would require cross-compiling SIL's custom ICU4C fork (with its `nfc_fw`
   normalization tables) for `android-arm64-v8a`, `armeabi-v7a`, and `x86_64`, packaging the result as
   `.so` files under the APK's native library folders, and getting .NET's Android native-library resolver
   to find something P/Invoke-declared as `"icuuc70.dll"` — none of which is attempted anywhere in the
   three repositories read for this report `[VERIFIED absence — grep across `liblcm`, `LCAtom`, and
   `languageforge-lexbox` for any Android-targeted ICU build step or `.so` artifact found nothing]`.
2. **The one team that already tried this concluded it wasn't viable and built around it, not through
   it.** `FwLiteMaui.csproj` targets `net10.0-android` conditionally alongside iOS/macCatalyst/Windows
   `[VERIFIED — FwLiteMaui.csproj:5-9]`, but gates LibLCM out explicitly:
   ```
   <IncludeFwDataBridge>false</IncludeFwDataBridge>
   <IncludeFwDataBridge Condition="…GetTargetPlatformIdentifier('$(TargetFramework)') == 'windows'">true</IncludeFwDataBridge>
   ```
   `[VERIFIED — FwLiteMaui.csproj:26-27, cited identically in one-api-problem.md:20-24]`. On Android there
   is no `FwDataMiniLcmBridge`, no `SIL.LCModel` reference, no LibLCM at all — the mobile build runs
   entirely on `LcmCrdt` (EF Core + SQLite + `SIL.Harmony`), a from-scratch reimplementation with zero
   LibLCM reference `[VERIFIED — one-api-problem.md:9-16]`. This is not a hypothetical obstacle for
   LCAtom's path; it is the identical native-dependency wall, already hit and already routed around, by
   engineers who needed the exact same thing LCAtom needs (an ICU-and-LibLCM-backed model on a phone) and
   chose to leave LibLCM off the device rather than solve the porting problem.
3. **LCAtom's `Runner`/`Host` are architecturally the same shape as the bridge that was excluded.** Both
   are thin layers whose entire job is to hold `LcmCache` and drive LibLCM's factories and UOW machinery
   `[VERIFIED — AGENTS.md:39-40 for LCAtom's TFM/dependency split; `Runner`/`Host` `.csproj` files confirm
   `net8.0` + `PackageReference Include="SIL.LCModel" Version="11.0.0-beta0150"` at
   `src/SIL.LCAtom.Runner/SIL.LCAtom.Runner.csproj` and `src/SIL.LCAtom.Host/SIL.LCAtom.Host.csproj`]`.
   Nothing about LCAtom's own code changes the native-dependency calculus that already excluded the
   structurally identical `FwDataMiniLcmBridge` from Android.

`[INFERRED, moderate confidence]` Building an Android-capable LibLCM is not obviously *impossible* — ICU4C
itself cross-compiles for Android routinely in other projects, and .NET for Android does support bundling
native `.so` libraries. But it is a nontrivial, currently-unattempted engineering project (cross-compiling
a customized ICU fork with FieldWorks-specific normalization data, for three Android ABIs, then verifying
LibLCM's generated model code and `structuremap`-based DI layer behave correctly under Android's AOT/trim
constraints) that nobody in either repository examined has started, and it is a project LCAtom's own
scope explicitly disclaims — `AGENTS.md` calls the wider target matrix, including any non-Windows,
non-`net8.0`-desktop build, "deferred to Phase 9 ... not present reality"
`[VERIFIED — AGENTS.md:43-48]`, and Phase 9 itself is "status: not started" with "No process/JSON host, no
`net48` adapter, and no published package exist yet"
`[VERIFIED — implementation-plan.md:268-270]`. Nothing in Phase 9's own item list (§9.1-9.6,
`implementation-plan.md:271-281`) mentions Android, iOS, or a mobile target at all.

### 2c. The client/server split — what it costs

Given (a) and (b), the only route to Android that does not require rebuilding LibLCM's native dependency
chain from scratch is a split where the LibLCM-bearing Runner/Host runs somewhere with a supported native
ICU build — Windows or Linux, desktop or server — and Android participates as a remote client. LCAtom's
own most recent design document proposes exactly this, independently of this brief, as "Option B: One
ring at the *contract* level, not the method level": make LCAtom's change set the lingua franca, give
MiniLcm's CRDT backend the single new capability of applying an LCAtom change set, and let it "carry
grammar change sets as opaque synced payloads it stores but does not interpret"
`[VERIFIED — one-api-problem.md:84-98]`. The document is candid about what this costs: *"A phone does not
need to understand a phonological rule change to sync it, show that it is pending, carry its PanGloss
report, and let someone approve it. Interpretation happens where LibLCM is."*
`[VERIFIED — one-api-problem.md:95-97]`

Concretely, this means:

- **Latency and offline capability.** *Authoring* a change set is pure C#/JSON and needs no LibLCM at all
  — `Contract` is LibLCM-free by design `[VERIFIED — architecture.md:61-68]` — so a phone can draft,
  label, and queue a Change Set fully offline. But *assessing* it (seeing the real effect delta before
  approving) and *applying* it both require a live `LcmCache` against the actual project
  `[VERIFIED — architecture.md:152-166]`, which by construction cannot run on the phone. Either those
  steps require a round trip to wherever the Runner does run (killing offline capability for the review
  step, which is the step LCAtom is built to make trustworthy — "author intent → assess the exact state
  delta before touching anything" is listed first in the product description,
  `architecture.md:5-8`), or the phone accepts assessments/receipts it cannot independently verify,
  which is a meaningfully weaker guarantee than what the design promises for a same-machine caller.
- **Complexity.** Issue B13 — "Cross-process protocol unspecified" — is the honest name for this gap, and
  it is not a footnote: it is the single artifact that would have to exist before any non-.NET, any
  non-desktop, or any remote consumer could reach LCAtom's semantics at all. As of 2026-07-27 it remains
  **open**, and its own description makes the shape of the remaining work explicit: framing, error/exit
  code contract, and one-shot-vs-daemon are all undecided, and the entry records a same-day reshape
  concluding that "915 generated kinds make typed CLI verbs impossible, so a generic change-set-JSON path
  is forced, not optional" `[VERIFIED — issues.md:37]`. **No cross-process transport of any kind exists in
  the codebase today** — confirmed by direct search: no `gRPC`, `JsonRpc`, `HttpListener`,
  `WebApplication`/Kestrel, or named-pipe code anywhere under `src/`
  `[VERIFIED — grep across `src/` for those tokens found only build-artifact `.pdb`/DLL paths, not source]`.
  The only consumer surface that exists is a single-process CLI (`SIL.LCAtom.Cli`) dispatching twelve
  verbs directly into in-process `Commands` calls over a local files store
  `[VERIFIED — src/SIL.LCAtom.Cli/Program.cs:1-9, 27-60; build-stages.md:49]`. There is no evidence — not
  a partial implementation, not a spike, not even a chosen framing — of the protocol a client/server split
  would need. This is the largest single gap between "LCAtom reaches Android" as a sentence and "LCAtom
  reaches Android" as a working system.
- **What does travel today, and how far.** The measured cost figures in ADR 0011 are relevant context for
  how expensive the LibLCM-side half of this split actually is: on `TestLangProj` (43 MB, 61 entries), a
  scratch-copy operation costs 0.05s, but a `LcmCache` load costs 10.1s cold / 3.6s warm
  `[VERIFIED — issues.md:37; adr/0011...md:71-74]`. A server-side Runner process is therefore not free to
  spin up per request; whatever daemon model B13 eventually settles on has to keep a cache warm across
  requests to be usable interactively, which pushes toward the "per-project daemon" shape the issue
  register already floats — `[VERIFIED — issues.md:37, "a per-project daemon that *is* the exclusive-write
  guarantee"]` — and a daemon is exactly the kind of hosting/multi-tenant infrastructure the project's own
  product boundary disclaims owning `[VERIFIED — README.md:89, "hosting ... database storage"]`.

**Direct answer to the "does this undermine the premise" question:** yes, partially, and LCAtom's own
newest document says so in different words. Reaching Android is possible only by accepting that the part
of LCAtom that does the actual work — turning intent into a verified, atomic model change — never runs on
the device, and by building a cross-process protocol that today does not exist even as a chosen design
(B13 is open, not merely unimplemented). What ships to Android under this plan is not LCAtom; it is a
client that trusts LCAtom, running elsewhere, to have done its job. Whether that is an acceptable reading
of "Android support is non-negotiable" is a product decision this report cannot make, but it should not be
mistaken for LCAtom itself running on Android, because it does not and — given the ICU native-dependency
wall — realistically will not without a substantial, currently unscoped porting project.

---

## 3. Is the generated-kinds bet sound?

The claim under test: **332 kinds generated from ~12 hand-written type handlers**, covering the
HermitCrab-reachable surface `[VERIFIED, as a design target — README.md:40-41, ADR 0012 lines 33-39]`.
The generator that would produce this **does not exist** — confirmed by direct inspection of
`src/SIL.LCAtom.Runner/Operations/`, which contains exactly four hand-written files
(`LexicalSenseOperationKinds.cs`, `SetGlossLowering.cs`, `SetGlossOperationHandler.cs`,
`SetGlossPayload.cs`) implementing the single shipped kind `lexical/sense/setGloss`
`[VERIFIED — directory listing and `Operations/LexicalSenseOperationKinds.cs:13`]`, and by the
implementation plan's own words: *"nothing yet generates operation kinds from it"*
`[VERIFIED — README.md:38-39, echoed at operation-catalog-plan.md:40-41]`.

**The arithmetic itself is internally consistent but scope-narrower than the headline number suggests.**
Two different handler counts appear in the design documents, for two different scopes, and they are
consistent with each other once the scope difference is made explicit — but a reader who only sees "12
handlers" should know which 12:

- **12 handlers → 332 kinds** is scoped to the 150 in-scope fields `HCLoader` actually reads
  (`HcReachable=yes`), of which only **12 distinct `(Kind, Card, Sig)` shapes** occur
  `[VERIFIED — adr/0012:34-39]`. Of those 12, 10 are already covered by the 37 non-grammar fields, and
  grammar adds only two more shapes (`basic/Integer`, `basic/String`)
  `[VERIFIED — adr/0012:36, 103]`.
- **25 handlers → ~1,100 kinds** is scoped to the *full* in-scope manifest (473 rows, all constructs,
  not just HC-reachable ones): "the basic-type half is exactly 8 handlers ... the relation/owning half
  adds ~17" `[VERIFIED — api-surface-layer1.md:69-91]`, and the whole-surface generated-kind estimate
  elsewhere is stated as 915 for "all 412 authorable in-scope rows"
  `[VERIFIED — adr/0012:39]` (a third number, close to but not identical to the "~1,100" figure in
  `api-surface-layer1.md:70` — `[INFERRED]` the discrepancy is likely counting basis: raw manifest rows
  vs. authorable rows vs. rows after multi-construct fan-out, none of which is reconciled in the text I
  read).

So the true statement is: **12 handlers get you the grammar-experimentation slice (332 kinds); the full
LibLCM-authoring surface needs roughly double that handler count (25) to reach roughly 915-1,100 kinds.**
This is still meaningful leverage — 25 handlers for ~1,000 kinds is a real win over one-handler-per-kind —
but it is a different, larger number than "12 handlers, period," and the report's brief was right to flag
it as unproven: **it is a projected ratio from a static shape count over the manifest, not a measured
ratio from a working generator.** No generator has been run to confirm that a `(Kind, Card, Sig)` triple
is actually sufficient dispatch information once real per-construct semantics are added.

**Does the evidence suggest the shape-count collapse actually holds, or would per-construct semantics
defeat it?** The manifest's own "Gaps recorded, not papered over" section is the most honest evidence
available, and it cuts both ways:

- **In favor of the bet:** the `(Kind, Card, Sig)` triple genuinely is enough to dispatch a *majority* of
  fields correctly — basic scalar/textual types (Integer, Boolean, Time, Guid, Unicode, MultiUnicode,
  String, MultiString) really do share one `set`/`clear` shape each regardless of which construct they
  belong to `[VERIFIED — api-surface-layer1.md:74-77]`, and the totality argument for `owning/atomic`
  replacement (all 69 in-scope fields resolved by one `create`-into-occupied rule) is a real, general
  result, not a per-field special case `[VERIFIED — api-surface-layer1.md:94-104]`.
- **Against a clean collapse:** the same document lists concrete cases where the triple is
  *insufficient* and a hand-reviewed map is required on top of it, not instead of it:
  - **Construct naming is explicitly non-mechanical and still open (issue B19).** `LexSense.Gloss` is
    tagged `Construct=lexSense` in the manifest but ships as `lexical/sense/setGloss` — segment `sense`,
    not `lexSense` — and the generator "cannot run unattended until the manifest's construct names are
    normalized or a mapping is committed" `[VERIFIED — issues.md:43]`. This is not a cosmetic gap: it
    means the one kind that *has* shipped was hand-named, and there is no evidence the naming rule that
    produced it has been written down as an algorithm rather than as a judgment call.
  - **17 rows carry a multi-construct string with no resolution rule (issue B20)** — e.g.
    `possibility|partOfSpeech|lexRefType|…` — needing "most likely fan-out to one kind per construct"
    (a proposal, not a decision) `[VERIFIED — issues.md:44]`.
  - **The `class → construct` map is explicitly "reviewed" and "not mechanical"**, called out in ADR 0009
    itself: "A reviewed `class → construct` map is required and is not mechanical: `PhNCSegments` and
    `PhNCFeatures` are both `naturalClass`; the three MSA classes are all `msa`; inherited members such as
    `CmPossibility.Name` generate at construct level or the namespace explodes."
    `[VERIFIED — adr/0009:74-77]`. A map requiring human judgment per row is not what "generated from 12
    handlers" evokes; it is closer to "12 handlers plus a hand-curated lookup table that must be gotten
    right for every one of ~95 classes."
  - **Feeding order, index-as-identity, and positional Output/Input resolution are semantics no
    `(Kind, Card, Sig)` triple can express, and the manifest already required a fourth column
    (`ComparisonClass`) plus per-row overrides to carry them:** `PhPhonData.PhonRules` and
    `LexEntry.AlternateForms` are `seq` fields overridden to `Feeding`; `PhRegularRule.StrucDesc` and its
    RHS context slots are a *third* ordering mode where "index is identity" and a **hard 24-variable
    ceiling** applies **per rule**, derived by simulating `HCLoader`'s exact traversal order — not
    something a generated handler can discover from field metadata alone
    `[VERIFIED — api-surface-layer1.md:106-131, change-set-contract.md:654-667]`. `MoAffixProcess.Input`
    is a second discovered-footprint case: its `Output` mappings resolve **positionally**
    (`ContentRA.IndexInOwner + 1`), so a generated `move` handler that treats this field like any other
    `owning/seq` would silently corrupt the very cases the manifest flags as most dangerous
    `[VERIFIED — api-surface-layer1.md:133-137, change-set-contract.md:685-690]`.
  - **L0's true field list is admitted to be unknown** — issue B21: "a field being *read* by `HCLoader`
    does not mean an object can be *created* with only those fields set. LibLCM factories and model
    invariants will require fields the parser never reads, and the manifest has no `required` column to
    derive them from" `[VERIFIED — issues.md:45]`. This means even the *first* generated slice's scope is
    not yet computed, only estimated.
  - **~300 of 473 in-scope rows (roughly two-thirds) are classified by field-name heuristic, not an
    explicit citation (issue B18)**, and ADR 0012 raises the stakes on exactly this gap: *"kind generation
    now reads these classifications directly, so a wrong row becomes a wrong shipped kind"*
    `[VERIFIED — issues.md:42, adr/0012:133-136]`. A generator is only as sound as its input data, and
    two-thirds of that input has not been individually verified against source.

**Assessment.** The leverage claim is directionally real — the underlying shape analysis is careful,
cross-checked by two independent reviews (semantic and structural, per `api-surface-layer1.md:1-6`), and
the basic-scalar half of the ratio (8 of ~12, or 8 of ~25, handlers) is genuinely mechanical. But "332
kinds from 12 handlers" understates the work by omitting the class→construct map, the naming
normalization, the multi-construct fan-out rule, and the four semantics-driven exceptions
(feeding/index-as-identity/positional-reference/owning-atomic-detach) that the manifest itself had to grow
new columns to represent. None of these exceptions are hidden — the documents are unusually candid about
each one, tagged as open issues rather than solved problems — but their existence means the generator, when
built, will be a generator-plus-substantial-hand-curated-metadata system, not a from-first-principles
transform of raw LibLCM reflection data. That is still a reasonable and probably correct architecture; it
is not the "12 handlers and you're basically done" reading the raw ratio invites, and because no generator
has been run even once, the actual collapse ratio — after the exceptions are coded, not just catalogued —
remains **unproven**, exactly as the task brief characterized it.

---

## 4. What LCAtom gets right that alternatives don't

Read fairly, several design choices are load-bearing and not merely aspirational:

- **The change/assessment/receipt separation is a real, structurally enforced guarantee, not a
  convention.** Apply is bound to a specific prior Assessment and hard-refuses to run without one — "a
  bare apply is a hard error" `[VERIFIED — issues.md:16, A2 fixed]` — and the binding is enforced by a
  footprint-digest check, not by caller discipline `[VERIFIED — change-set-contract.md:731-734]`. This is
  implemented, not merely documented: `ChangeSetApplier.Apply` was specifically hardened to close this gap
  `[VERIFIED — issues.md:16]`.
- **Reviewability via effect comparison, not plan comparison, is a genuinely subtle and correct design
  choice.** Comparing the Mutation Plan (the LibLCM lowering) would false-positive on every internal
  optimization; comparing effects (the read-back state delta) does not. The drift-class taxonomy
  (Identical / Same-nature-wider-scope / Changed-values / Changed-meaning) is a real answer to a real
  problem — "did a bulk find-and-replace's *scope* grow, or did one of its *values* silently change" is a
  distinction most review tooling collapses `[VERIFIED — change-set-contract.md:550-563]`.
- **Atomic apply through LibLCM's own unit-of-work machinery, with real hazards found and fixed, is
  concrete evidence of engineering discipline, not just design intent.** Issues A3/A4/C15 — a
  rollback-cleanup path that could commit a stray mutation, an assess-then-rollback that poisons
  headword/homograph caches with no repair path — were found by reading LibLCM's actual undo-stack code,
  not hypothesized, and were fixed by discarding the poisoned cache rather than attempting an unsafe
  repair `[VERIFIED — issues.md:17-18, 57]`. This is the kind of correctness work that a naive "just call
  the LibLCM API" wrapper would not do, and it is real, shipped, and covered by the 82/82 test suite
  `[VERIFIED — build-stages.md:54-56]`, run against a real `LcmCache`, not mocks.
- **Semantic operations over raw property mutation closes a real, documented bug class.** The
  `create`-into-occupied / implicit-detach rule for `owning/atomic` replacement exists specifically because
  the alternative — `SetPartOfSpeech` silently orphaning an MSA in the tools LCAtom is meant to replace —
  is a real, named defect this design prevents by construction, not by convention
  `[VERIFIED — change-set-contract.md:281-305, adr/0009:107-114]`.
- **The Contract layer's engine-neutrality is real, not aspirational, at the code level.** `Contract` and
  `Model` genuinely have zero `SIL.LCModel` reference and target `netstandard2.0`
  `[VERIFIED — src/SIL.LCAtom.Contract/SIL.LCAtom.Contract.csproj`, `src/SIL.LCAtom.Model/...csproj`, both
  confirmed by direct `grep` for `TargetFramework`/`PackageReference`]`. A Python or Rust implementation of
  the same JSON schema, canonicalization, and digest rules could exist without touching LibLCM at all —
  this is exactly the layer the cross-process protocol (B13) would put on the wire, and it is the one part
  of the "reach every platform" story that is actually true today, independent of the Runner/Host
  Android/Linux question addressed above.
- **The comparison-footprint and comparison-class analysis (unordered / positional / feeding /
  index-as-identity) is a real, HCLoader-grounded piece of domain knowledge that no generic diff/patch
  tool would derive on its own** — the alpha-variable 24-per-rule ceiling and the `MoAffixProcess.Input`
  positional-reference hazard were found by reading `HCLoader.cs` and LibLCM's `IPhRegularRule` code
  directly, and they are exactly the kind of silent-corruption risk a naive property-level diff tool would
  miss `[VERIFIED — change-set-contract.md:654-690, citing HCLoader traversal order]`.

These are genuine strengths, and they distinguish LCAtom from "a thin wrapper that calls LibLCM setters" —
which is a fair description of what Flexicon and, per LCAtom's own documents, MiniLcm's FwData bridge
mostly are `[VERIFIED — minilcm-evaluation.md:148, "no MiniLcm-level guard code found"]`. The caveat that
must sit next to every one of these strengths, per the task's own discipline, is that most of them are
proven **for one operation** (`setGloss`) and one snapshotter (`LexSenseSnapshotter`)
`[VERIFIED — build-stages.md:44]`, not for the 472 other in-scope fields the design claims to cover.

---

## 5. Honest cost, risk, and failure modes

**What has to be true for this bet to be right:**

1. The generator (§3) has to actually collapse the work the way the shape analysis predicts, including
   the hand-curated exceptions — not just for the 12/332 HC-reachable slice but eventually for the
   25/~1,000 full-surface slice, since the lexical catalog is "resequenced, not descoped"
   `[VERIFIED — adr/0012:129-131]`.
2. The cross-process protocol (B13) has to get built, chosen, and shipped — today it is not even a
   settled design, only a set of constraints and a same-day reshape
   `[VERIFIED — issues.md:37]` — before *any* non-.NET consumer (Flexicon in Python, PanGloss in Rust,
   the AI agents the project is explicitly built for) can reach LCAtom's semantics at all. This item is
   the actual Android/Linux/cross-language gate; it is not one gap among several, it is the gate every
   other capability sits behind for any consumer that is not an in-process .NET caller.
3. The custom ICU native-dependency wall (§2b) has to either not matter in practice (because Android
   access always goes through a server, and the product accepts that as final) or get solved by a porting
   project that is currently unscoped, unstaffed, and unmentioned in the one phase (`Phase 9`) that would
   own it.
4. Someone has to actually resource the project past the walking-skeleton stage. At 42 commits
   `[VERIFIED — `git log --oneline | wc -l``]`, one shipped operation, no CI
   `[VERIFIED — `ls .github/workflows` → does not exist]`, and extensive design documents dated the same
   week as the code they describe, the project's own comparison document names this risk explicitly: *"If
   LCAtom's build stalls indefinitely ... the practical recommendation degrades to 'MiniLcm is what
   actually exists,' regardless of which is structurally cleaner."* `[VERIFIED — minilcm-evaluation.md:340-343]`

**What failure looks like, concretely, if the bet is wrong:**

- The generator gets built but the hand-curated exceptions (naming map, multi-construct fan-out,
  feeding/index-as-identity/positional-reference semantics) turn out to require as much per-construct code
  as writing 900 kinds by hand would have — at which point the "12/25 handlers" pitch collapses into
  "roughly as much work as Flexicon's ~150-method inventory, just organized differently," and the
  project's central efficiency argument is gone even though the correctness argument (atomic apply,
  effect-based review, semantic operations) may still hold.
- B13 never gets resolved, or gets resolved in a way (e.g., a heavyweight per-project daemon
  `[VERIFIED — issues.md:37]`) that reintroduces exactly the hosting/multi-tenancy complexity the product
  boundary explicitly disclaims owning `[VERIFIED — README.md:89]` — in which case LCAtom either grows
  scope it says it doesn't want, or stays a Windows/Linux-desktop-only, single-process tool that Python
  and Rust consumers cannot actually reach, despite being named as the motivating consumers on page one
  of the README `[VERIFIED — README.md:106-122]`.
- Android support gets deferred indefinitely under the client/server framing, and "Android and Linux
  support are non-negotiable" quietly becomes "Linux support is real, Android support is a promise about
  a client that doesn't exist yet" — which is a materially different claim than "one API surface,
  everywhere," and one this report's evidence already supports as the likely outcome absent a dedicated
  porting effort.
- The 300-of-473 heuristically-classified manifest rows (issue B18) turn out to contain systematic
  errors once audited, and because the generator reads classifications directly
  `[VERIFIED — adr/0012:133-136]`, those errors ship as wrong kinds rather than surfacing as review
  friction — a correctness regression in the one area (grammar authoring against a mission-critical
  linguistic dataset) where LCAtom's entire value proposition is *not* getting this wrong.

**What failure does *not* look like:** total abandonment. Even in the worst case, the Contract layer's
canonical-JSON/digest/ID machinery, the comparison-footprint taxonomy, and the documented HCLoader
hazard list (C1-C15) are genuine, reusable artifacts regardless of which execution engine ultimately wins
— LCAtom's own comparison document makes the same point about the reverse case (MiniLcm's referential
integrity work would not be wasted either) `[VERIFIED — minilcm-evaluation.md:280-293]`. The risk is not
that the work evaporates; it is that "LCAtom is the one API surface, on every platform" turns out to
overstate what a Windows/Linux-only change-authorship engine, reachable from a phone only through an
unbuilt protocol, actually delivers.

---

## Verdict

LCAtom is a well-designed **change-authorship and review contract** with a genuinely strong correctness
story on the one slice it has built, and a serious, self-aware body of design work — but it is not, and
cannot become without a client/server split, a single API surface that runs everywhere the requirement
demands. Linux is real today, gated behind one native-dependency detail (the `icu-fw` package) that CI
already exercises successfully; Android is not reachable at all without either an unscoped, unattempted
native-ICU porting project or accepting that the phone never runs LCAtom's actual semantics and instead
carries its change sets as opaque payloads to be interpreted somewhere else — a conclusion LCAtom's own
newest internal document reaches independently. Combined with a cross-process protocol that remains
undesigned (issue B13, open as of this writing) and a generated-kinds bet whose leverage ratio is
projected from static shape analysis rather than measured from a working generator, "LCAtom becomes the
one API surface" is best read as "LCAtom becomes the one change-authorship *contract*, executed on
Windows and Linux, reachable from mobile only as a client of something else" — a materially narrower and
more honest claim than the phrase on its face suggests.

**Confidence: medium.** The architectural and native-dependency findings (TFMs, DllImport sites, CI
matrix, FwLiteMaui's explicit Android exclusion, the absence of any cross-process code) are drawn directly
from source and are high-confidence. The generated-kinds soundness assessment is medium-confidence because
it reasons from a careful but unexecuted design; the true collapse ratio will only be known once a
generator exists. The overall verdict is bounded by how much weight to put on LCAtom's own most recent
design document (`one-api-problem.md`, dated the same day as this brief) reaching a compatible conclusion
independently — treated here as corroborating internal evidence, not as an external check, since it comes
from the same repository and the same design process being evaluated.

**What I could not verify:**

- Whether the LibLCM Linux CI leg is passing *right now* — I read the workflow definition and its gating
  logic but did not execute it or query GitHub Actions for the latest run status.
- Whether SIL or any affiliated project has an unpublished or in-progress Android build of the custom
  ICU4C "fw" fork; I can only report that no evidence of one exists in the three repositories and package
  caches available locally.
- Whether .NET for Android's native-library resolver would, in principle, successfully resolve a
  P/Invoke declared as `"icuuc70.dll"` against a correctly-packaged `libicuuc70.so` without source changes
  to `CustomIcu.cs` — this is a plausible-but-untested mechanical question about .NET's Android runtime
  that I did not test.
- The internals of `SIL.Harmony` (the CRDT engine underneath MiniLcm's mobile store) — not checked out in
  this environment; relevant only as background for the client/server alternative, not load-bearing for
  this report's core findings.
- Real-world resourcing plans or timelines for LCAtom past its current walking-skeleton state — this is
  an organizational fact outside any repository's source.
- Whether the discrepancy between the "~1,100" (`api-surface-layer1.md`) and "915"
  (`adr/0012`) full-surface generated-kind estimates reflects a real reconciliation the authors intended,
  or is simply an unreconciled figure carried across two documents written days apart.
