# The "one API over LibLCM" problem

*2026-07-27. Companion to [minilcm-evaluation.md](minilcm-evaluation.md). Scope: what MiniLcm and
LCAtom each actually sit on, why "one ring to rule them all" is harder than it looks, and what the
ways around it are.*

## First, a correction to the framing

MiniLcm **is not** a thing that bypasses LibLCM. `MiniLcm` is an *interface* — `IMiniLcmApi`, plus
model classes, validators, and normalization wrappers — with **two independent implementations**:

| | Goes through LibLCM? | Storage | Ships on |
| --- | --- | --- | --- |
| `MiniLcm` | — (contract only, no `SIL.LCModel` reference at all) | — | everywhere |
| `FwDataMiniLcmBridge` | **Yes.** `PackageReference Include="SIL.LCModel"`, calls `.Delete()`, `LcmCache`, real UOW | `.fwdata` | **Windows only** |
| `LcmCrdt` | **No.** EF Core + SQLite + `SIL.Harmony`; zero LibLCM reference | its own SQLite DB | Windows, Android, iOS, macCatalyst, web |

The split is not philosophical, it is load-bearing and enforced in the build:

```xml
<IncludeFwDataBridge>false</IncludeFwDataBridge>
<IncludeFwDataBridge Condition="…GetTargetPlatformIdentifier('$(TargetFramework)') == 'windows'">true</IncludeFwDataBridge>
```
— `FwLiteMaui/FwLiteMaui.csproj:26-27`, gating the `ProjectReference` at `:98-99`.

**So: on Android and iOS there is no LibLCM at all.** That is the answer to "why not." LibLCM needs
the ICU4C native runtime, `structuremap`, and the desktop `.fwdata` file model. FieldWorks Lite is a
mobile product. The CRDT backend exists because LibLCM cannot go where the product needed to go — not
because someone preferred CRDTs in the abstract.

**LCAtom has the same shape, with one backend instead of two.** `Contract` and `Model` are
`netstandard2.0` and LibLCM-free by design; `Runner` and `Host` take `SIL.LCModel` 11.0.0-beta0150.
The difference is that LCAtom has never had to make its LibLCM-free layer *executable* against a
non-LibLCM store. MiniLcm has, and that is where all the difficulty lives.

## What "one ring" would actually have to bind

Five distinct binds, in rough order of how hard they are to get out of.

**1. The narrow-waist tax.** A shared interface can only express what its *narrowest* implementation
can honour. `IMiniLcmApi` today is the intersection of "things LibLCM can do" and "things a CRDT
store can do." Extending it to grammar forces a choice: either `LcmCrdt` grows a full phonological
and morphological model (230 fields, 30 constructs, all of it new), or the interface grows methods
that throw `NotSupportedException` on mobile. The second option is not one ring — it is one ring
with a hole in it, and every caller has to know which platform it is on.

**2. Two stores means two truths, and that bill is already being paid.**
`FwLiteProjectSync/CrdtFwdataProjectSyncService.cs:115-146` is a hand-enumerated list of seven
per-type reconcilers — `WritingSystemSync`, `PublicationSync`, `PartOfSpeechSync`,
`SemanticDomainSync`, `ComplexFormTypeSync`, `MorphTypeSync`, `EntrySync` — each invoked **twice**,
once per direction, against a saved snapshot. That is not CRDT machinery; it is ordinary diff/patch
code. Every new construct adds another such pair. Grammar would add roughly thirty.

**3. An interface can abstract data shapes. It cannot abstract semantics.** Both backends can
implement "set this item's order to 3.5." Only one of them is *wrong* about what that means. In
phonology, order is a feeding relation between rules — rule 7's output is rule 8's input — and
`LcmCrdt/Changes/SetOrderChange.cs:14-18` stores it as `entity.Order = Order`, an absolute scalar
merged last-writer-wins. A shared interface makes the two backends look interchangeable at exactly
the point where they are not. This is the bind I would worry about most, because it fails *silently*:
the API call succeeds, the data is well-formed, and the grammar now means something else.

**4. The two systems are not the same kind of API.** MiniLcm is nouns and verbs — `CreateEntry`,
`UpdateSense`, `DeletePartOfSpeech` — executed immediately. LCAtom is one noun, a change set, carrying
a generated kind namespace, assessed before it is applied, hashed, reviewable, and separable into
Change Set / Assessment / Receipt. "Unify them" usually means "make one look like the other," and
both directions lose something real: MiniLcm gains ceremony it does not need for a live edit box,
LCAtom loses the reviewability that is its whole reason to exist.

**5. Referential integrity is LibLCM's, and it does not travel.** `CmObject.Delete()` →
`ClearIncomingReferences()` cleans up atomic references, reference collections, *and* reference
sequences (`liblcm` `CmObject.cs:1728-1733`, `Vectors.cs:782-785`, `:1836`). Anything writing through
LibLCM inherits that. `LcmCrdt` had to re-implement it from scratch as the
`GetReferences`/`RemoveReference` contract (`MiniLcm/Models/IObjectWithId.cs:24-26`) — for thirteen
lexical types. Grammar's reference graph is an order of magnitude denser. **Any backend that does not
sit on LibLCM has to rebuild referential integrity for every construct it adds.**

## Ways around it

**A. Two rings, honestly named** *(what the evaluation recommends)*. A query/interactive-edit API
(MiniLcm) and a change-authorship/review API (LCAtom). Cost: a developer must know which to reach
for. Benefit: neither is deformed to fit the other. The honest objection is that "we now have two
APIs" is precisely what your boss is trying to get away from.

**B. One ring at the *contract* level, not the method level** — the option I think is most worth
examining. Do not extend `IMiniLcmApi` to grammar. Instead make **LCAtom's change set the lingua
franca**, and give MiniLcm exactly one new capability: *apply an LCAtom change set*. Then:

- there **is** one API for describing a change to a FieldWorks project, everywhere, for everyone;
- MiniLcm's method surface does not grow by thirty constructs;
- the FwData backend can apply lexical *and* grammar change sets, because it has LibLCM underneath;
- the CRDT backend applies the lexical ones natively and **carries grammar change sets as opaque
  synced payloads it stores but does not interpret** — the exact posture ADR 0011 already establishes
  for LCAtom and reports, pointed the other way.

That last point is the interesting one. A phone does not need to *understand* a phonological rule
change to sync it, show that it is pending, carry its PanGloss report, and let someone approve it.
Interpretation happens where LibLCM is. This gets grammar onto mobile as a **reviewable object**
without porting phonology to SQLite.

**C. Narrow the waist at the data layer instead.** Declare LibLCM the single authority and demote the
CRDT store to a replica/cache. Clean in principle; in practice it means mobile becomes read-mostly or
online-only, which is a product regression FieldWorks Lite users would feel immediately. I would not
propose this without knowing how much offline editing actually happens in the field.

**D. Do nothing about grammar.** Worth naming rather than pretending it is not an option. If grammar
experimentation turns out to serve a handful of linguists, the cost of a second system may exceed the
benefit. This is a product question, not a technical one, and it is the cheapest way out if the
answer is yes.

## Recommendation

**B, with A as its honest description.** Pursue one *contract* rather than one *interface*. The thing
that should be universal is the vocabulary for describing a change — that is what every consumer
(Flexicon, PanGloss, Linguistic Assistant, FieldWorks Lite, a reviewer, an AI) genuinely shares. What
should stay plural is the execution surface, because one of the two backends physically cannot run
LibLCM and no amount of interface design will change that.

Framed for your boss: *"One API" is the right goal, and the right layer for it is the change format,
not the method list. Unifying the method list would mean either shipping phonology to a phone's
SQLite database or shipping an interface that throws on half its platforms.*

**Before committing, the two things worth checking:** how much genuinely offline mobile editing there
is (decides whether option C is even live), and whether grammar review on mobile is wanted at all
(decides how much of option B's payload-carrying is worth building).
