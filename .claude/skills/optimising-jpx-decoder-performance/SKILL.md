---
name: optimising-jpx-decoder-performance
description: Use when making the C# OpenJPEG decoder faster or lighter on memory (T1/MQ, DWT, MCT, copies, SIMD), when a decode stage is slower than upstream C, or when timings of a speed change are noisy, contradictory or show no gain.
---

# Optimising the JPX decoder

## Overview
Measure a stage against upstream C timed the same way, find the cause in the generated code or the memory traffic,
change one thing, keep it only if alternating A/B runs show a gain. Output stays bit-identical (REQUIRED:
verifying-jpx-decoder-changes). `documentation/performance-findings.md` holds every result, rejected attempt and
lesson so far: read it first.

## Loop
1. **Size the gap.** Time the stage in C# and in upstream in the same session, the same way:
   `./tools/perf/ab.ps1 -Probe <tile|t1|dwt53|dwt97> -Files <sample> -NoBaseline`, then
   `./tools/perf/upstream.ps1 -Files <sample>` (see `tools/README.md`). Timers wrap whole loops, not each call.
   Files that decode in a few ms need tens of warm-up rounds (`-Warm`). Never patch `.reference` itself. A stage
   with no probe yet gets one: add anchors to `tools/perf/instrument.cs` and, for C, to `upstream-timers.patch`.
2. **Is it hot?** Before optimising a path, count how often it runs: a temporary static counter, decode both corpora.
   A path no benchmark sample or PDF stream reaches cannot be timed; say so before spending effort on it.
3. **Find the cause.**
   - Generated code: `DOTNET_JitDisasm=<method> DOTNET_JitStdOutFile=<file>` (delete the file first; it appends).
     Take the Tier1 or FullOpts listing, not OSR.
   - What to look for: a non-inlined call inside a hot loop (the loop's values then live on the stack), conditional
     jumps on decoded data, `movsxd` from `int` indices (use `nuint`), and shuffle uops (`vpermd`, `vpbroadcastd`,
     `vpunpck*`, `vextracti128`, `vpextrd`; one port).
   - Upstream's code: `dumpbin /disasm` of its `.obj`, run from PowerShell.
   - To cost one part: time a deliberately wrong build that skips it (for example a copy-back).
4. **Change one thing**, then A/B it (below). Within noise (about ±2.5%) means revert, and record it under
   "Things that did not help".
5. Verify (parity in all modes, tests), commit when asked, then update the findings doc when asked.

## A/B runs
- `./tools/perf/ab.ps1 -Probe <probe> -Baseline <ref> -Files <sample>`: baseline worktree outside the repo, same
  timers in both copies, base/new runs alternating (6 each), minimum per side. Both trees are restored after.
- A stage you did not touch is the control: if it moves, the run is noise. A/A noise is about ±2.5%.
- Machine drift between sessions is ±5-10%: compare only within one A/B run. Whole decodes:
  `./benchmarks/compare-versions.ps1 -Baseline <ref>`, nothing else running.
- One-off probes (a counter, skipping a part, an alignment check) are temporary edits: back up the file, restore
  it after, and confirm with `git status` that nothing is left.

## Patterns that paid off here
| Symptom | Fix |
|---|---|
| Rare call in a hot loop (MQ `ByteIn`) | Inline it: T1 passes -5% to -9% |
| Data-dependent select compiled as a jump | Arithmetic on a 0/1 value (`cond ? 1 : 0` becomes `setcc`) |
| Shuffle-port bound SIMD | Recompute the shifted vector from extra loads |
| Scalar tail after a vector loop | One overlapping vector step that rewrites the same values |
| Copy-back through a temp buffer | Write into the destination once the inputs there are read |
| Strided column passes | Wider blocks per sweep (.NET arrays are only 8-byte aligned, so rows straddle lines) |
| Vector rows in a rented temp buffer split cache lines | Pin it for the loop (`GCHandle`), start at the first 64-byte boundary: 5/3 vertical -7% to -19% |

## Pitfalls
- Power-of-two strides: a strip's rows share few L1 sets, and rows 4 apart (1 KB stride) share address bits 0-11.
  Stores interleaved with the next loads then cause 4K-aliasing stalls. Test both stride kinds (10719: 256,
  10026: 2496).
- Bit-exactness: no FMA; integer adds wrap, so reordering them is safe; float operations stay in C order.
- `[AggressiveOptimization]` loses dynamic PGO: use it only on plain vector loops that otherwise stay in OSR code.
- Fewer spills or instructions does not mean faster: only alternating timings decide.
- `ArrayPool` rents round up to a power of two: a few spare ints on a power-of-two size double the buffer. Find
  the room inside it (`8dc4686`: last row written straight to the destination). A new pinned buffer per call is
  cold memory every time (+45%): pool or reuse it.
