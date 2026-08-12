---
name: code-comments
description: >-
  Use when writing, reviewing, or cleaning up comments and XML doc comments in this repo's C# —
  `<summary>`/`<remarks>` blocks, inline `//` notes, and commit-adjacent prose that has leaked into
  source. Enforces comments that explain WHY, and forbids project state in code: no plan or issue
  references (`docs/plan-motif.md`, `MOT-22`, `D8`, `B18`), no dates, no wiring status, no changelog
  narrative, no agent/process attribution. Caps IMPLEMENTATION comments (`//`, and `///` on a private
  member) at ONE line, while API docs on public/internal members may run long form as appropriate, and
  forbids bare behavioural claims about other code entities ("X refuses Y", "the only caller is Z") —
  those become a cited test or a reword. Trigger on "add a comment", "document this class", "fix the
  doc comment", "this comment is too long", any doc/code mismatch cleanup, and whenever a plan or
  issue reference is encountered in code — remove it on sight.
---

# Comments and XML doc comments

Ported from PanGloss's rule of the same name. The intent is identical; the mechanics differ because
C# validates `cref` and Rust does not, and because this repo's rot is issue-shaped rather than
plan-shaped.

## The one rule

**A comment explains what the code cannot: why this, why not the obvious alternative, what breaks if
you change it. Everything else is noise or a lie waiting to happen.**

Code says what it does. Git says when and by whom. `docs/plan-motif.md` and `docs/issues.md` say where
the project is. A comment that duplicates any of those three has no reason to exist and will
eventually contradict its source.

## Forbidden in source code, without exception

Delete these on sight, in any file you touch, whether or not you are otherwise working on it. Do not
"correct" them — remove them.

**Plan and issue references are banned in string literals too, not only in comments.** An exception
message travels further from the code than any comment does: it reaches someone holding the message
and nothing else. `"...which is the defect docs/issues.md D9 records"` tells that reader nothing they
can act on, and goes stale on exactly the same schedule. Say the constraint instead, and name the type
to edit with `nameof` so the refusal is a hand-off rather than a dead end.

A repo-relative Markdown path is *not* banned from a message string. The two bans have different
reasons — an issue reference **rots**, while a `docs/…md` path is merely **unopenable from a tooltip**
— and the reader of a generator's exception is standing in a checkout, so a path helps them.

| Forbidden | Why | Instead |
|---|---|---|
| Plan and issue references — `docs/plan-motif.md`, `MOT-22`, `docs/issues.md D8`, `issue B18`, and the **bare** register id on its own: `D8`, `A1`, `J44`, `Q16`, `E12` | A pointer into memory you do not own. Issues get closed, renumbered, superseded; the comment survives and misleads. The bare form is the one that survives a sweep — it reads as a word, not a citation, so "D8's rule is…" sat unnoticed through a full pass | State the constraint itself. See the ADR carve-out below for the one project artifact that is stable enough to cite |
| Slice and wiring status — "slice 4 is the fourth shape", "not wired into X yet", "today exactly one field", "a later increment widens this" | True the day it is written, false the day the next slice lands, and nothing checks it | Say what the type OWNS. Let a test assert the count if the count matters |
| Dates — "verified 2026-08-11", "corrected 2026-08-05", "as of today" | Git has the date and is never wrong about it | Nothing. If a measurement matters, put the number in a test or an evidence doc |
| History and changelog narrative — "MOT-22 first shipped this as set-only", "the first version pinned commits and tripped within the hour", "this used to read…" | Describes code that no longer exists. A reader cannot tell the live claim from the dead one | Nothing. `git log -p` answers it better. Keep the resulting *rule*, drop the story |
| Attribution and process notes — "an earlier agent reported this as unextractable", "verified by a subagent", "the owner asked for" | Process shape leaking into code shape | Nothing |
| Restating the code — `// increment i`, `/// Gets the name` on `Name` | Pure maintenance liability | Nothing. Rename the thing if it is unclear |
| Commented-out code | Dead weight; nobody dares delete it | Delete it. Git has it |

### The ADR carve-out, and its limit

**ADR citations are allowed.** `docs/adr/` records are immutable by construction — an ADR is amended
or superseded in place with its number intact, never renumbered — which is exactly the property plan
sections and issue IDs lack. `ADR 0022 decision 1` is a stable coordinate.

**The limit:** cite an ADR for a *decision*, never for a *status*. "Verbs are derived, not authored
(ADR 0022 decision 1)" is durable. "ADR 0022 says five rows" was true, became false, and sat in a
heading for six days — an ADR number does not make the sentence around it true. Quote the constraint,
cite the number, and do not paraphrase the ADR's own claims about counts.

## Generalise the incident to the class

The most common thing worth SAVING is buried in the most common thing worth deleting: a comment that
justifies a guard by retelling the incident that produced it. Keep the mechanism, drop the war story.

| Incident-shaped (delete the narrative) | Class-shaped (keep) |
|---|---|
| "The first version pinned each repo at a commit and refused to run within the hour, because the FieldWorks checkout advanced by one commit adding three Avalonia test files." | "Pinned by file content, not by commit: a commit hash moves for every unrelated change in a large repository." |
| "MOT-22 shipped this as `set`-only on the argument that a clear would be a synonym for `set 0`; the other ten enum fields refute it." | "Every enum field carries the derived `set\|clear`; `clear` writes the zero member rather than erasing." |
| "An earlier research pass reported the `.chm` as unextractable. It was extractable — `hh.exe -decompile` finished in seconds." | "Extraction is Windows-only (`hh.exe`), so it is a dev-time step and its output is committed." |

The test: **would a reader on a different machine, a year from now, act differently knowing the date
and the process name?** If no, it is decoration on a real reason — keep the reason.

Two things this does not license. Do not generalise away a *number the code depends on* — a
threshold's value and units stay. And if an incident is genuinely the only evidence for a surprising
claim, put the evidence in a test and let the comment state the claim; a measurement that exists only
inside a comment is unverifiable anyway.

## `cref` is checked here, and that changes one rule

PanGloss bans code-to-code doc links because Rust's intra-doc links rot unobserved — 551 broken ones
were found the first time the lint was enabled. **C# is different: `<see cref="X"/>` is resolved by
the compiler, and a broken one is warning CS1574.** With `TreatWarningsAsErrors` or a warning sweep,
it cannot silently rot the way Rust's did.

So `cref` stays. But know exactly what it buys: **it proves the name resolves, never that the sentence
around it is true.** Renames and deletions are caught; semantic drift is not. When a comment's argument
is load-bearing, the argument belongs in a test and the comment belongs in one line.

## Sort every claim by what a machine can falsify

The forbidden list finds project state. It does not find a comment that is simply **false about
behaviour**, and that is the class that costs the most, because it looks maintained.

| Tier | Looks like | What to do | Who checks it |
|---|---|---|---|
| **Executable** | "an out-of-range value is rejected rather than clamped" | a test, cited as ``pinned by `TestName` `` | `dotnet test`; the checker verifies the name exists in the tree |
| **Quoted contract** | a verbatim sentence from a contract document or data file | quote it, name the document in prose, and cite the test that settles it | never reword a quotation to dodge a claim verb — cite instead |
| **Resolved** | any reference to another type or member | `<see cref="Foo"/>` | the C# compiler (CS1574) |
| **Durable external** | a paper, an algorithm, an upstream issue, LibLCM or HermitCrab behaviour with a `file:line` | keep it | nothing needed — it does not rot on our side |
| **Project state** | plans, issues, dates, slice status, history | delete | the hygiene checker |

## Length: one line for implementation comments

The kind distinction is standard, not local. [Ousterhout](https://web.stanford.edu/~ouster/cgi-bin/cs190-spring16/lecture.php?topic=comments)
separates *interface documentation* from *implementation documentation* and rules that the second must
not appear in the first; [Java's conventions](https://www.oracle.com/java/technologies/javase/codeconventions-comments.html)
draw the same line syntactically. C# has the split in syntax too: `///` is documentation, `//` is
implementation.

| Kind | Cap | Why |
|---|---|---|
| **API doc** — `///` on a `public` or `internal` type or member, on an interface member, or on an enum member | long form as appropriate | This is the abstraction. Without it a caller must read the body, and there is no interface |
| **Implementation comment** — any `//`, and `///` on a `private` member | **one line, at most 110 characters** | It explains code the reader is already looking at. If one line cannot carry it, the knowledge belongs in the API doc or in a test |

The character cap is not pedantry, it is the same rule stated twice. A five-line block reflowed onto
one four-hundred-character line satisfies the line count and reads worse than what it replaced.
**Shorten the content, not the whitespace.** An interface or enum member carries no access modifier
because it cannot; both are API surface at their type's visibility, and both get long form.

**A reference document REPLACES a long comment; it does not license one.** If the knowledge needs a
paragraph, write it in `docs/research/` and let an *implementation* comment be the one line that
points there. An **API doc may not point there at all** — see below.

## An API doc must be complete, or cite a URL

**Repo-relative Markdown paths** are banned from `///` on public surface (checker:
`api-doc-defers-offline`). The reason is where an API doc is actually read: an IDE tooltip, or the
compiled XML documentation. In neither place can the reader open a path that resolves only inside a
checkout of this repo. The rule is about the reader's position, not about which top directory the path
starts in — `manifest/README.md` is exactly as unopenable as `docs/plan-motif.md`.

So an API doc either says the thing, or points at something anyone can open:

| Instead of | Write |
|---|---|
| `docs/adr/0016-scratch-cache-copy-not-undo.md` | `ADR 0016` — the number is the stable coordinate; the path is not |
| `docs/change-set-contract.md` | "the Change Set contract" |
| `docs/applied-log.md` | "the applied log format" |
| `manifest/README.md` | "the manifest README" |
| `See docs/research/2026-08-06-parser-timing-measured.md` | the measurement itself, in a sentence |
| a URL | a URL — this is the one pointer that always works |

**Inline before you delete.** A pointer that was carrying the rule must have the rule brought across
first; removing it otherwise destroys information, which is worse than the pointer ever was. The
test: a reader who never opens `docs/` understands the API as well afterwards as a reader who did
before. API docs may run long form precisely so this is possible — never compress one to avoid
inlining.

Implementation comments are not covered by this rule. A one-line `//` may still point at a research
document, because the reader of a `//` has the checkout open by construction.

**Deciding whether a long API doc earns its length:** it must tell a caller something the signature
cannot — a precondition, a trap, an invariant to preserve, or a rejected alternative that looks better
than it is. If it narrates what the body does, it is implementation documentation in the wrong place.

`<remarks>` is not a length loophole. It is the right home for a precondition or a rejected
alternative, and the wrong home for a changelog.

## Generated files

`*.g.cs` files are excluded from the checker: they are template output, and the fix for a bad comment
in one is a fix to the emitter that produced it.

The emitters themselves are scanned, and a `//` line written literally inside a raw-string template
is caught by the line-level rules — a plan reference typed into a template ships in a committed file
exactly like one typed anywhere else. **The length rule does not apply inside a template**: the long
block there is the generated file's `<auto-generated>` banner, and that file is length-exempt in
full.

**Length is the only thing a template escapes.** A `///` line inside one still has to be complete: it
lands in a public class and is read from a tooltip like any other API doc, so a `docs/…md` path there
is the same defect one directory further from where anyone would look for it. Two of them survived a
whole sweep this way.

**The gap that remains:** a template whose comment text is assembled by concatenation or
interpolation, rather than written literally at the start of a line, is invisible to the checker.
Apply these rules by hand when you edit one.

## When you find a violation

Remove it in the same commit as whatever you were already doing. Do not open a task, do not annotate
it, do not leave a marker.

The one exception: if deleting the reference would lose a **genuine invariant** tangled up with it,
keep the invariant and drop the reference.

Before: `// Slice4CatalogWriter (MOT-22) is the fourth shape: basic Integer, for the one field
VerbDerivation.Exceptions currently names.`

After: `// Basic Integer standing in for a small closed enum: the payload range-checks against the
manifest's EnumValues, which the other basic templates have no reason to emit.`

## The counterweight — read this before deleting an API doc

Applied without judgement this file will damage the codebase. The strongest argument against it is
Ousterhout's, and it is correct: **without an interface comment there is no abstraction.**

**Reading a method tells you what it does. It never tells you what it must never do.** Negative
constraints — *must reject rather than clamp*, *must not be relaxed to accept a missing citation*,
*never widen this selector without a new drift guard* — have no representation in C# except a test.
Deleting the comment does not make the constraint discoverable; it makes it invisible. Convert it to a
cited test, or keep it. Never just drop it.

**So the target is claims, not words.** A four-line `<param>` explaining what a value means, asserting
nothing about another entity, is doing its job.

## Why this is strict rather than advisory

Doc rot compounds, and the failure is asymmetric: a missing comment costs one reader a minute, while a
confidently wrong one sends them to the wrong conclusion and is *believed*, because it looks
maintained.

There is measured evidence for the second reader this repo now has. **Misleading natural language in
code degrades LLM code reasoning by roughly 23%, and reasoning models show a "reasoning collapse"
failure mode** ([CodeCrash](https://arxiv.org/pdf/2504.14119)): models trust prose over executable
logic. Across 1.3 billion AST-level changes in 1,500 systems, **changes that leave a comment
inconsistent are about 1.5x more likely to be bug-introducing**
([Wen et al.](https://www.inf.usi.ch/lanza/Downloads/Wen2019a.pdf)).

## Sources

- [CodeCrash: LLM fragility to misleading natural language in code](https://arxiv.org/pdf/2504.14119)
- [Wen et al., A large-scale empirical study on code-comment inconsistencies](https://www.inf.usi.ch/lanza/Downloads/Wen2019a.pdf)
- [Ousterhout, A Philosophy of Software Design](https://web.stanford.edu/~ouster/cgi-bin/cs190-spring16/lecture.php?topic=comments) — the counterweight
- [Java code conventions — comments](https://www.oracle.com/java/technologies/javase/codeconventions-comments.html)
- [C# XML documentation comments](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/) and [CS1574](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs1574)
- [No ticket numbers in comments](https://sveljko.github.io/no_ticket_numbers_in_comments/)
