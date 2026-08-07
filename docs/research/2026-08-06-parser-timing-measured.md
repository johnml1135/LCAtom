# Measured 2026-08-06 — what a grammar reload costs, and what a corpus reanalysis costs

**In plain terms:** reloading a grammar after an edit takes about a tenth of a second, so that is a
non-issue and needs no new interface. Re-analysing a whole corpus is a different story: it took 8 seconds
per thousand words on one language, 4 seconds on another, and **33 seconds on a third — a thousandfold
spread**, driven by how hard the language's morphology is rather than by anything we control. On the hardest
of the three, one word in six could not be analysed within five seconds at all. That last fact matters more
than the speed: a coverage figure that counts "we gave up waiting" the same way it counts "the grammar
cannot analyse this" is not measuring the grammar.

## Why this was measured

Two numbers were recorded as unmeasured in
[ADR 0031](../adr/0031-collaboration-follows-the-data-not-the-surface.md) and
[the cross-repo plan](../plan-cross-repo.md), with the instruction *"do not propose an incremental-add API
before measuring"* and an expectation that corpus reanalysis would be **"cheap, because it scales with
corpus size rather than grammar size."** Both were about the loop
[ADR 0032](../adr/0032-stem-assessment-is-pangloss-supplied-lexicon.md) is built around: a grammar author
tries a rule, reanalyses, reads a coverage number, and tries another.

The expectation about corpus reanalysis was wrong about the constant, and the correction changes what that
loop can look like.

## Method

`pangloss batch <grammar> <words> <out.tsv>` from a release build (`cargo build --release -p pg-cli`), on a
20-core Windows machine. The output TSV carries a per-word millisecond column, so summed per-word time and
total wall time separate analysis cost from load cost. Grammars and word lists are PanGloss's own checked-in
samples (`samples/data/`). Each throughput run is the **first 40 words** of the matching list with a
5-second per-word cap (`--word-timeout-ms 5000`).

Load cost was isolated by running a one-word list three times and subtracting the per-word column, so what
remains is process start plus grammar load.

## Grammar load: ~0.1 s, and the question it settles

| run | total wall | per-word | remainder = startup + load |
| --- | --- | --- | --- |
| 1 | 147 ms | 0 ms | **147 ms** |
| 2 | 147 ms | 0 ms | **147 ms** |
| 3 | 123 ms | 0 ms | **123 ms** |

A 1 MB Amharic HC grammar, loaded in about an eighth of a second including process startup. Earlier notes
posited that "a reload costing 40 ms needs no new interface and one costing four seconds needs one badly."
This is the first case. **No incremental-add API is needed on load-cost grounds** — for the stem loop (which
ADR 0032 already routes through the supplied-lexicon overlay) or for the grammar-editing loop, where a rule
change genuinely does require a rebuild.

## Corpus reanalysis: three grammars, three orders of magnitude

First 40 words each, 20 cores, 5-second per-word cap:

| grammar | mean per word | slowest word | timed out | wall for 40 |
| --- | --- | --- | --- | --- |
| Indonesian | **1 ms** | 12 ms | 0 | 97 ms |
| Sena | **151 ms** | 2,035 ms | 0 | 2,575 ms |
| Amharic | **1,327 ms** | 5,716 ms | **7 of 40** | 9,927 ms |

Extrapolated to the 6,973 wordforms measured in the Sena 3 project:

| grammar's profile | wall time for ~7,000 wordforms |
| --- | --- |
| Indonesian-like | **~17 seconds** |
| Sena-like | **~7.5 minutes** |
| Amharic-like | **~29 minutes**, with roughly a sixth of words abandoned |

**So "cheap" is not a property of the system; it is a property of the language.** The claim that reanalysis
is cheap because it scales with corpus size was right about the shape and wrong about the constant — the
constant ranges over three orders of magnitude, and the hard end is not iterable. Sena is the honest number
for the Sena 3 corpus and it is tolerable; Amharic is not something a person tries twelve variants against
in an afternoon.

Two caveats. This is the default (HermitCrab) engine; `--engine=foma` exists and was not measured. And
Amharic is genuinely hard — Semitic root-and-pattern morphology, 417 phonemes and 8 phonological rules in
the sampled project — and PanGloss ships an `amharic-worst-words.txt` separately, so the pathology is known
rather than surprising. These runs used the ordinary word list, not that one.

## The finding that matters more than the timings

**On Amharic, 7 of 40 words hit the 5-second cap.** A coverage number computed with a cap therefore mixes
two different facts:

- the grammar cannot analyse this word — a real gap, and the thing the author wants to see;
- we stopped waiting — a fact about the machine, the thread count, and the cap.

Conflating them corrupts coverage as an objective function in both directions. A rule change that makes
analysis *faster* would look like improved coverage without explaining anything more. A busy machine would
look like a regression. And an author iterating toward a coverage target would chase both.

So a coverage report must **count timeouts as their own category**, never as failures, and record the cap
and the thread count it ran under. That is a concrete addition to ADR 0032 §4, which already requires a
coverage figure to cite its corpus, that corpus's hash, the lexicon overlay revision, and the grammar
identity: **it must also cite the per-word cap and the number of words that hit it.** A coverage figure
whose timeout count is non-zero is a lower bound, and should say so rather than presenting itself as a
measurement.

## What this leaves open

- **The `foma` engine is unmeasured** and may change the picture entirely on the hard end. Worth one run
  before anyone designs around the HermitCrab numbers.
- **Sampling instead of full-corpus** is the obvious answer for a hard grammar, and it interacts with the
  coverage-ramp question (`I37`): a delta against the previous run over a *fixed sample* is cheap and
  comparable, where a delta over the whole corpus is neither on Amharic.
- **These are 40-word prefixes**, chosen to be reproducible rather than representative. The extrapolations
  assume the prefix's difficulty profile holds across the list, which for Amharic is the assumption most
  likely to be wrong in either direction.
