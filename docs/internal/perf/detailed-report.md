# cop vs CodeQL — Performance Comparison

**Target repo:** `C:\git\FoundryMachine` — a real .NET app
(289 first-party C# files, ~47K LOC; CodeQL extracted 378 source files incl. generated).
**Date:** 2026-06-18 · **Machine:** local dev box (Windows).
**Tools:** cop `2026.6.18.3` · CodeQL CLI bundle `2.25.6` (C# extractor, `--build-mode=none`).

*For the one-page executive version with charts, see [`executive-summary.md`](executive-summary.md).*

> **Question being answered:** *Is "agent cop" faster than CodeQL for the kind of
> ad-hoc, structural questions an agent asks about a codebase?* Short answer: **yes, by
> 1–2 orders of magnitude end-to-end**, because CodeQL must build a database before it can
> answer anything, and its per-query cost is far higher.

---

## TL;DR (headline numbers)

| Scenario | cop | CodeQL | cop advantage |
|---|---|---|---|
| **One-time setup before *any* question** | **0 s** (none) | **274–1,648 s (4.6–27.5 min)** DB build | ∞ (cop has no setup) |
| **Ask 1 question, fresh repo** (setup + answer) | **~7 s** | **~318–1,692 s** (DB build + ~44 s first query) | **~44×–230×** |
| **Ask 5 questions** (warm, ignoring CodeQL's 27.5-min build) | **6.6 s** | 22.1 s | **3.3×** |
| **Ask 100 questions** (warm, ignoring CodeQL's build) | **8.8 s** | 57.2 s | **6.5×** |
| **Per-extra-check cost** | **0.023 s** | 0.37 s (warm) / 4.4 s (cold) | **16×–190×** |

**Even giving CodeQL every advantage** (database already built, queries already compiled,
all checks in one invocation), cop is **3–6× faster** and the gap **widens** with more checks.
Counting the mandatory database build (4.6 min source-only … 27.5 min as-is), cop is
**~44–230× faster** to answer the first question about a repo.

---

## 1. One-time setup cost (the big one)

cop has **no** setup: `cop check.cop -t <repo>` parses source on the fly and answers.

CodeQL must first build a database. Measured, buildless (`--build-mode=none`, the *fastest*
C# option and the closest analog to cop since neither compiles the project):

| CodeQL database build | time | notes |
|---|---:|---|
| **Repo as it sits on disk** | **1,648 s (27.5 min)** | 7.5 min just scanning the repo's 5,215 `bin/` DLLs (self-contained .NET runtime) to resolve refs; then ~20 min extracting source **+ referenced assemblies** → 2.4 GB DB |
| **Source-only tree** (fair best case: `bin/obj/out/node_modules` stripped) | **274 s (4.6 min)** | what a CodeQL expert would aim for; **6× faster and more accurate** — it even resolved all 15 `Thread.Sleep` calls the as-is DB missed |

Either way, CodeQL needs **minutes** before it can answer the first question. cop needs **none** —
and cop automatically ignores `bin/obj/out`, so it never pays the trawling cost in the first place.

---

## 2. Single-question latency

An agent typically asks a *handful* of ad-hoc questions. End-to-end time to get one answer
(medians; CodeQL assumes the 1,648 s database already exists):

| | cop | CodeQL (DB pre-built) |
|---|---:|---:|
| 1 question (single check) | **~7 s** | cold query **~44 s** · warm query **~11 s** |
| 5 questions (one invocation) | **7.5 s** (`cop-combined.cop`) | `database analyze` **~20 s** |

- **cop:** ~7 s parse+check, **zero** prerequisite. Asking 5 questions costs **7.5 s** — barely
  more than one, because cop parses once and runs all checks in the same process.
- **CodeQL:** a freshly-written query (cold compile + eval) is **~44 s**; an already-compiled
  query re-run (warm) is **~11 s**; all five via `database analyze` is **~20 s** — *on top of*
  the one-time **1,648 s** database build.

CodeQL's per-query cost is dominated by **QL compilation** (compiling the query against the
C# library) and evaluator/JVM startup — overhead cop doesn't have for structural checks.

---

## 3. Scaling: 5 → 20 → 100 checks

This directly tests the common counter-argument: *"CodeQL parses once and queries many times,
so it should scale better with the number of checks."* In practice it does **not**, because
cop's **combined mode also parses once** and runs every check in a single process — and cop's
per-check evaluation (a predicate over in-memory collections) is far cheaper than CodeQL's
per-query compile + evaluate + interpret.

Both tools run **all N checks in one invocation** (cop: one `.cop` file; CodeQL:
`database analyze` over N `.ql` files), so each amortizes its fixed cost across the N checks.

| # checks (N) | cop (median) | CodeQL warm¹ | CodeQL cold² |
|---:|---:|---:|---:|
| 5   | **6.64 s** | 22.09 s | 84.33 s |
| 20  | **7.33 s** | 33.35 s | 176.88 s |
| 100 | **8.82 s** | 57.18 s | 498.90 s |

¹ warm = database already built **and** all queries already compiled (best case for CodeQL).
² cold = database already built, queries compiled fresh (what you pay when writing new checks).
Neither CodeQL column includes the one-time **274–1,648 s** database build. (Timings are on the
as-is DB; re-running the warm suite on the smaller source-only DB was only ~15% faster — e.g.
analyze-5 was 16.9 s vs 20.4 s — so the conclusions are unchanged.)

**Per-additional-check cost (slope, N=5→100):**

| | seconds per extra check |
|---|---:|
| **cop** | **0.023** |
| CodeQL warm | 0.37  (**16× steeper**) |
| CodeQL cold | 4.36  (**190× steeper**) |

cop's curve is essentially **flat** (parse dominates; 95 extra checks add ~2 s). CodeQL's
grows steeply. **There is no crossover** — cop has both a lower fixed cost *and* a lower
per-check cost, so its lead grows without bound as checks increase.

---

## 4. Correctness / capability notes (being fair both ways)

Performance only matters if both tools answer the same question. Result counts on the same repo
(ground truth = regex over source):

| Question | ground truth | cop (distinct) | CodeQL — as-is DB | CodeQL — source-only DB |
|---|---:|---:|---:|---:|
| Console.Write/WriteLine calls | 1,070 | 1,026 | 1,067 | 1,068 |
| Thread.Sleep calls | 15 | 12 | **0** ⚠ | **15** ✓ |
| Class declarations | — | 319 | 468 | 447 |

**The tools agree on the substance** — for structural questions, cop and a properly-built CodeQL
DB find essentially the same things. Two honest caveats, one each way:

- **CodeQL (clean DB) is slightly *more* accurate here.** With the source-only database it nailed
  Console (1,068 vs truth 1,070) and *all* 15 Thread.Sleep calls. cop under-counts a little —
  partly due to a bug found during this exercise (below).
- **CodeQL is sensitive to repo state; cop is not.** The *as-is* database (built over a tree
  containing `bin/` self-contained-runtime DLLs) not only took **6× longer** to build but also
  **silently returned 0 Thread.Sleep** — conflicting runtime assemblies (2,031 "assembly
  conflicts" in the extractor log) broke type binding. cop parses source directly and is immune
  to this entire class of configuration pitfall.
- **cop bug found & filed** (KrzysztofCwalina/cop#31): `cb.Calls`/`cb.Statements` duplicate
  findings combinatorially with nesting depth (one line emitted 1,536×). It inflates *output*
  and slightly perturbs distinct counts, but timings are parse-bound so it doesn't change the
  performance conclusions.
- Class counts differ because CodeQL counts nested/partial/generated classes (incl. generated
  Razor sources) that cop's source-level model doesn't.

> **Takeaway:** this is a **speed** story, not a correctness story. For ad-hoc structural
> questions both tools land in the same place; cop just gets there in seconds with no database,
> no compilation, and no sensitivity to what's sitting in `bin/`.

### When CodeQL is the right tool
This benchmark covers **structural / lint-style questions** (call sites, type shapes, simple
patterns) — the bulk of what an agent asks. CodeQL's expensive database + query model exists to
do **whole-program dataflow / taint / security analysis** (interprocedural, path-sensitive) that
cop does not attempt. For that class of question, CodeQL's cost is justified and it can answer
things cop can't. The claim here is narrow and accurate: **for fast, ad-hoc, structural code
questions — the agent use case — cop is dramatically faster.**

---

## 5. How to reproduce

Everything lives in `C:\Users\kcwalina\cop-codeql-bench\`:

```
cop-checks\        q1..q5 .cop (one question each)
cop-combined.cop   all 5 base questions, single parse
codeql-queries\    q1..q5 .ql + qlpack.yml              (matched semantics)
scale\cop\         cop-scale-{5,20,100}.cop             gen-scale.py (generator)
scale\codeql\      check_001..100.ql
databases\         foundry-csharp (2.4 GB, as-is)  foundry-csharp-srconly (source-only)
run-benchmark.ps1  scale-bench.ps1  scale-bench-ql.ps1  db-srconly.ps1   (harnesses)
results\           timings.csv  scale-timings*.csv  REPORT.md  db-create-*.{log,time}
```

- DB build (as-is): `codeql database create databases\foundry-csharp --language=csharp --build-mode=none --source-root=C:\git\FoundryMachine`
- DB build (source-only, fair): `db-srconly.ps1`
- Scaling: `scale-bench.ps1` (cop) + `scale-bench-ql.ps1` (CodeQL).
- Per-question: `run-benchmark.ps1`.
