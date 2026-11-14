# Decoder performance: findings

State of the managed decoder after the work on branch `feature/refactor` (commits `9ac0cac` to `37b7a76`),
compared with the original port (`master`, `8b39066`) and with upstream C OpenJPEG (commit `8314119b`, built with
`/arch:AVX2 /D__SSE__ /D__SSE2__`, see `tools/reference/build.ps1 -Simd`).

All measurements: Windows x64, AVX2 CPU, .NET 8 unless stated, samples from
`UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks/Data`. Every change listed here keeps the decoded samples
bit-identical to upstream C: the conformance + non-regression files in both thread modes (280 files; 315 since the
parity tool also scans subfolders such as `htj2k/`) and the 1 404 sampled test-PDF streams after each commit; the
full 15 276-stream PDF corpus was checked at `9ac0cac`.

## Results

### Release 0.1.16.1 vs `f04357e` (NativeAOT stopwatch harness)

`0.1.16.1` is the tag of `8b39066` (`master`; the tag exists on the remote only). Both built with `benchmarks/aot`
(same harness, samples and flags), `win-x64`, 15 iterations per run, 3 runs alternating, minimum of the medians.
`1` = single thread, `all` = all cores. Allocated bytes are `GC.GetTotalAllocatedBytes(true)` per decode: they count
every allocation, not what is live at once. The first decode, which fills the buffer pools, allocates within a few MB
of the median, so only the median is shown.

| Sample | Threads | Time 0.1.16.1 | Time `f04357e` | Alloc 0.1.16.1 | Alloc `f04357e` |
|---|---|---|---|---|---|
| MOZILLA-10719-0_3 | 1 | 1432.9 ms | 744.3 ms (-48%) | 285.5 MB | 125.1 MB (-56%) |
| MOZILLA-10719-0_3 | all | 378.5 ms | 258.4 ms (-32%) | 1243.2 MB | 154.0 MB (-88%) |
| MOZILLA-LINK-3167-0_10 | 1 | 278.7 ms | 85.9 ms (-69%) | 106.0 MB | 48.5 MB (-54%) |
| MOZILLA-LINK-3167-0_10 | all | 103.9 ms | 35.1 ms (-66%) | 220.2 MB | 50.7 MB (-77%) |
| MOZILLA-LINK-3181-0_5 | 1 | 383.5 ms | 67.2 ms (-82%) | 201.0 MB | 87.0 MB (-57%) |
| MOZILLA-LINK-3181-0_5 | all | 117.7 ms | 28.1 ms (-76%) | 267.8 MB | 87.4 MB (-67%) |
| PDFBOX-1845-0_49 | 1 | 157.7 ms | 35.1 ms (-78%) | 79.0 MB | 34.3 MB (-57%) |
| PDFBOX-1845-0_49 | all | 43.2 ms | 12.5 ms (-71%) | 104.3 MB | 34.4 MB (-67%) |
| MOZILLA-10026-0_0 | 1 | 113.0 ms | 17.3 ms (-85%) | 79.6 MB | 34.4 MB (-57%) |
| MOZILLA-10026-0_0 | all | 24.9 ms | 9.4 ms (-62%) | 114.0 MB | 34.5 MB (-70%) |
| MOZILLA-8179-0_1 | 1 | 42.3 ms | 25.4 ms (-40%) | 5.8 MB | 4.2 MB (-28%) |
| MOZILLA-8179-0_1 | all | 8.3 ms | 7.5 ms (-10%, noise) | 17.2 MB | 4.6 MB (-73%) |
| MOZILLA-LINK-4649-0_10 | 1 | 104.5 ms | 30.8 ms (-71%) | 31.6 MB | 14.4 MB (-54%) |
| MOZILLA-LINK-4649-0_10 | all | 37.4 ms | 16.2 ms (-57%) | 70.0 MB | 16.1 MB (-77%) |

The baseline run-to-run spread on 10719 (1 thread) was about 8% (1484 to 1607 ms), the branch's 4.6%; every gap is
larger. After `f04357e` the work below changed the all-cores numbers (see "All cores").

### Original port vs `9ac0cac` (BenchmarkDotNet, ShortRun)

Decode time and allocations per image. `1` = single thread, `all` = all cores.

| Sample | Threads | Time before | Time after | Memory before | Memory after |
|---|---|---|---|---|---|
| MOZILLA-10026-0_0 (1-bit page) | 1 | 112 ms | 32 ms (-71%) | 81 MB | 36 MB |
| MOZILLA-10719-0_3 (RGB lossless, 140 tiles) | 1 | 1259 ms | 959 ms (-24%) | 339 MB | 185 MB |
| MOZILLA-LINK-3167-0_10 (RGB 9/7, 15 tiles) | 1 | 253 ms | 137 ms (-46%) | 110 MB | 52 MB |
| MOZILLA-LINK-3181-0_5 (RGB 9/7, 1 tile) | 1 | 350 ms | 182 ms (-48%) | 203 MB | 89 MB |
| PDFBOX-1845-0_49 (grey 9/7 page) | 1 | 145 ms | 70 ms (-52%) | 80 MB | 35 MB |
| MOZILLA-10719-0_3 | all | 375 ms | 298 ms (-20%) | 1297 MB | 214 MB (-84%) |
| MOZILLA-LINK-3181-0_5 | all | 110 ms | 49 ms (-56%) | 270 MB | 89 MB |

Main causes: the thread scheduler (single-threaded mode still bounced every code-block through the thread pool),
a new T1 context and new DWT buffers per job, and the SIMD ports listed below.

### Since `9ac0cac` (stopwatch harness, warm, single thread, three-file round)

| Step | Round | Notes |
|---|---|---|
| `9ac0cac` | 1270 ms | |
| T1 (`b518738`) | ~1170 ms | -8% |
| DWT (`ed9a31b`) | see table below | DWT only measured per stage |

BenchmarkDotNet, `9ac0cac` vs T1 + DWT + the decoder items below (ShortRun, .NET 8). Allocations unchanged.

| Sample | 1 thread before | 1 thread after | all cores before | all cores after |
|---|---|---|---|---|
| MOZILLA-10026-0_0 | 35.8 ms | 24.5 ms (-32%) | 12.6 ms | 13.0 ms (noise) |
| MOZILLA-10719-0_3 | 1054 ms | 802 ms (-24%) | 317 ms | 298 ms (-6%) |
| MOZILLA-8179-0_1 | 37.8 ms | 28.3 ms (-25%) | 8.8 ms | 7.8 ms (-11%) |
| MOZILLA-LINK-3167-0_10 | 152 ms | 96 ms (-37%) | 60.9 ms | 47.9 ms (-21%) |
| MOZILLA-LINK-3181-0_5 | 189 ms | 93 ms (-51%) | 49.1 ms | 37.7 ms (-23%) |
| MOZILLA-LINK-4649-0_10 | 52.0 ms | 33.3 ms (-36%) | 25.5 ms | 21.6 ms (-15%) |
| PDFBOX-1845-0_49 | 79.0 ms | 38.5 ms (-51%) | 20.5 ms | 15.0 ms (-27%) |

### T1 branch-free updates (`8b58ba5`)

BenchmarkDotNet, `584e2a6` vs `8b58ba5` (ShortRun, .NET 8). Allocations unchanged.

| Sample | 1 thread before | 1 thread after | all cores |
|---|---|---|---|
| PDFBOX-1845-0_49 | 42.5 ms | 35.6 ms (-16%) | -5% |
| MOZILLA-LINK-4649-0_10 | 32.9 ms | 29.2 ms (-11%) | -2% |
| MOZILLA-LINK-3181-0_5 | 97.9 ms | 88.4 ms (-10%) | 0% |
| MOZILLA-8179-0_1 | 25.6 ms | 23.4 ms (-9%) | -15% |
| MOZILLA-10719-0_3 | 722 ms | 676 ms (-6%) | -7% |
| MOZILLA-10026-0_0 | 23.4 ms | 22.7 ms (-3%) | -2% |
| MOZILLA-LINK-3167-0_10 | 86.2 ms | 83.9 ms (-3%) | noise |

Per coding pass, single thread, three-file round (minimum of 5 warm runs; upstream timed with a temporary patch of
`opj_t1_decode_cblk`):

| Pass | C# `584e2a6` | C# `8b58ba5` | Upstream C |
|---|---|---|---|
| Significance | 288.7 ms | 285.4 ms | 223 ms (1.28x) |
| Refinement | 228.7 ms | 187.3 ms (-18%) | 143 ms (1.31x) |
| Cleanup | 242.9 ms | 236.5 ms | 181 ms (1.31x) |

Run-to-run noise on this machine is up to ~13% on single runs; compare minimums of several runs.

### T1 MQ decode (`b187729`)

BenchmarkDotNet, `9b60201` vs `b187729` (ShortRun, .NET 8). Allocations unchanged.

| Sample | 1 thread before | 1 thread after | all cores |
|---|---|---|---|
| MOZILLA-10719-0_3 | 689 ms | 623 ms (-9.5%) | +4.5% (noisy: StdDev 4.6 ms vs 0.7 ms) |
| MOZILLA-LINK-4649-0_10 | 27.5 ms | 25.6 ms (-6.8%) | -1.4% |
| MOZILLA-8179-0_1 | 23.4 ms | 21.9 ms (-6.4%) | -4.4% |
| MOZILLA-LINK-3167-0_10 | 78.2 ms | 73.2 ms (-6.4%) | -1.5% |
| MOZILLA-LINK-3181-0_5 | 67.8 ms | 66.1 ms (-2.6%) | -2.4% |
| MOZILLA-10026-0_0 | 16.2 ms | 15.8 ms (-2.3%) | -1.6% |
| PDFBOX-1845-0_49 | 28.4 ms | 27.9 ms (-1.8%) | +1.4% |

Per coding pass, single thread, three-file round (2 warm-up rounds, then 8 timed; minimum of runs alternating
between a `9b60201` worktree and the change; upstream timed in the same session with the same temporary patch of
`opj_t1_decode_cblk`):

| Pass | C# `9b60201` | C# `b187729` | Upstream C |
|---|---|---|---|
| Significance | ~268 ms (1.20x) | ~248 ms (1.11x) | 222.8 ms |
| Refinement | ~174 ms (1.19x) | ~154 ms (1.05x) | 146.8 ms |
| Cleanup | ~217 ms (1.23x) | ~199 ms (1.13x) | 176.0 ms |

The machine ran faster than when `8b58ba5` was measured; compare within this table. A/A noise of the alternating
runs: about ±2.5%.

### Stage times vs upstream C (single thread, per decode)

Upstream timed with a temporary `QueryPerformanceCounter` patch in `tcd.c` (not committed).

| Sample | T1 C | DWT C | DWT C# before | DWT C# `ed9a31b` | DWT C# before `f9b203e` | DWT C# `f9b203e` |
|---|---|---|---|---|---|---|
| MOZILLA-10719-0_3 (5/3) | 562 ms | 14 ms | ~42 ms | ~34 ms | 31.8 ms | 25.8 ms |
| MOZILLA-LINK-3181-0_5 (9/7) | 27 ms | 57 ms | 171 ms | 97 ms | 64.7 ms | 55.9 ms |
| MOZILLA-LINK-3167-0_10 (9/7, tiled) | 52 ms | 15 ms | 64 ms | 53 ms | 23.2 ms | 22.6 ms |
| Three-file total | 641 ms | 86 ms | 267 ms | ~185 ms | 119.7 ms | 104.3 ms |

The `before` and `ed9a31b` columns include tier-0 warm-up rounds and overstate the C# DWT time. The last two
columns use the corrected probe (13 rounds, first 3 discarded, minimum of 3 runs), measured back to back.
MOZILLA-LINK-4649-0_10 (9/7, tiled): 14.8 ms -> 14.4 ms. Three warm-up rounds were still too few for the tiled
files, whose rounds are short: their numbers here include unoptimised code. The table below uses 60.

### 9/7 DWT stages vs upstream C (single thread, per decode, 60 warm-up rounds)

Upstream timed with a temporary `QueryPerformanceCounter` patch around the same six stages in `dwt.c`
(`opj_dwt_decode_tile_97`, AVX2 build, minimum of 5 runs; not committed). Both sides lift the same number of
elements (5692 `v8dwt_decode` calls on 4649, 10827 on 3167).

| Stage (ms) | 3167 C | 3167 `f9b203e` | 3167 `247af34` | 4649 C | 4649 `f9b203e` | 4649 `247af34` | 3181 C | 3181 `f9b203e` | 3181 `247af34` |
|---|---|---|---|---|---|---|---|---|---|
| h interleave | 2.30 | 1.73 | 1.79 | 0.60 | 0.47 | 0.48 | 5.86 | 5.77 | 5.91 |
| h lifting | 1.81 | 4.03 | 2.27 | 0.54 | 1.06 | 0.61 | 7.29 | 8.91 | 7.93 |
| h write-back | 3.05 | 1.32 | 1.34 | 0.73 | 0.36 | 0.37 | 8.97 | 4.98 | 4.95 |
| v interleave | 1.75 | 1.96 | 1.88 | 0.55 | 0.53 | 0.49 | 11.97 | 14.20 | 11.37 |
| v lifting | 1.92 | 4.07 | 2.21 | 0.55 | 1.08 | 0.62 | 7.23 | 9.26 | 8.38 |
| v write-back | 1.71 | 2.60 | 1.53 | 0.36 | 0.76 | 0.38 | 8.67 | 5.10 | 5.46 |
| **DWT total** | 13.5 - 14.0 | 16.8 | **12.1** | 3.7 - 3.8 | 4.8 | **3.5** | 51.2 | 50.7 | **45.6** |

With 3 warm-up rounds (closer to a process that decodes a few images) `247af34` changes little: 3167 19.1 ms, 4649
12.0 ms. That time is unoptimised JIT code, not this code (removed in `f844085`, see below). With tiered
compilation off, `f9b203e` already ran 3167 in 15.1 ms and 4649 in 4.2 ms; under dynamic PGO the vertical write-back
became slower (2.5 ms vs 1.4 ms on 3167), which `247af34` removes.

### 5/3 DWT stages vs upstream C (single thread, per decode)

Upstream timed with the same `dwt.c` patch (whole horizontal and vertical loops of `opj_dwt_decode_tile`, AVX2
build, minimum of 5 runs). C# timed the same way, around the whole loops: the earlier probe timed each row call,
which added ~200k timer pairs on 10719. C# with 5 warm-up rounds (these rounds are long), minimum of 3 runs.

| Stage (ms) | 10719 C | 10719 `24e3cb2` | 10719 `d48f2d2` | 10026 C | 10026 `24e3cb2` | 10026 `d48f2d2` |
|---|---|---|---|---|---|---|
| horizontal | 5.60 | 8.88 | 9.05 | 2.36 | 6.17 | 3.18 |
| vertical | 6.56 | 10.60 | 8.74 | 4.52 | 9.36 | 6.04 |
| **DWT total** | 12.55 | 19.95 | **18.25** | 6.98 | 15.74 | **9.43** |
| total, tiering off | | 40.12 | 17.75 | | 13.04 | 9.42 |

MOZILLA-10719-0_3: 3507x2480, 256x256 tiles, 3 components. MOZILLA-10026-0_0: 2496x3512, one tile, 1 component.

`436da40` measured back to back with `d48f2d2` in a later session (same probe; the machine ran faster that day, so
compare within this table, not with the one above):

| Stage (ms) | 10719 `d48f2d2` | 10719 `436da40` | 10026 `d48f2d2` | 10026 `436da40` |
|---|---|---|---|---|
| horizontal | 7.84 | 6.85 (-13%) | 3.09 | 3.01 |
| vertical | 7.84 | 7.80 | 5.14 | 5.32 |
| **DWT total** | 16.02 | **15.05** | 8.23 | **8.42** (noise) |

`df30330` vs `e7ba98d`, same probe, runs alternating between a worktree of `e7ba98d` and the change (6 each,
minimum per side); upstream timed in the same session (minimum of 7 runs):

| Stage (ms) | 10719 C | 10719 `e7ba98d` | 10719 `df30330` | 10026 C | 10026 `e7ba98d` | 10026 `df30330` |
|---|---|---|---|---|---|---|
| horizontal | 5.17 | 6.62 | **5.17** (-22%) | 2.82 | 2.95 | **2.70** (-8%) |
| vertical | 5.99 | 7.11 | 7.01 | 6.06 | 5.68 | 5.64 |
| **DWT total** | 11.70 | 14.07 | **12.52** | 9.02 | 8.64 | **8.34** |

The horizontal pass now matches upstream on 256x256 tiles and is faster on the large tile. Whole-decode
BenchmarkDotNet numbers for `df30330` have not been taken yet.

`b793e62` vs `df30330`, same probe and A/B setup (vertical pass; upstream from the table above):

| Vertical (ms) | Upstream C | `df30330` | `b793e62` |
|---|---|---|---|
| MOZILLA-10719-0_3 (256x256 tiles, stride 256) | 5.99 | 7.12 | **6.19** (-13%) |
| MOZILLA-10026-0_0 (one tile, stride 2496) | 6.06 | 4.92 | **3.50** (-29%) |

The vertical pass is now within 3% of upstream on 256x256 tiles and 1.7x faster on the large tile. Whole-decode
BenchmarkDotNet numbers have not been taken for `b793e62` either.

Probes behind it (10719, A/B against `df30330` or `e7ba98d`):

| Experiment | Vertical |
|---|---|
| Copy-back from the temp buffer to the tile skipped (wrong output, timing only) | 7.19 -> 2.96 ms |
| Tile data allocated on a 64-byte boundary (pinned arrays, retried until aligned) | -13.5% (7.11 -> 6.15 ms) |
| Rows written back in the loop, 1 KB stride | +5% to +7% (10026: -11% to -18%) |

`tilec.data` (`new int[]`) is only 8-byte aligned in .NET: on 10719 it started at all eight offsets mod 64. Upstream
allocates it 16-byte aligned (`opj_aligned_malloc`) and its vertical temp buffer 32-byte aligned.

`180fb8e` vs `b3c0a25`, same probe and A/B setup, two runs on 10719; upstream timed in the same session:

| Vertical (ms) | Upstream C | `b3c0a25` | `180fb8e` |
|---|---|---|---|
| MOZILLA-10719-0_3 (stride 256) | 6.52 | 6.87 | **6.40 / 6.26** (-6.8% / -8.9%) |
| MOZILLA-10026-0_0 (stride 2496) | 3.93 | 3.90 | **3.15** (-19.3%) |

The vertical pass is now faster than upstream on both samples. Probes behind it (10719 unless noted):

| Experiment | Vertical |
|---|---|
| Copy-back skipped (wrong output, timing only) | 7.06 -> 4.04 ms |
| tmp in a pinned buffer cached across tiles, 64-byte aligned | -11% |
| Same buffer, 8 bytes off a 64-byte boundary | -2% |
| Direct stores into the tile (below), 10026 only | -14% |

C# T1 was only measured for the three files together: 967 ms at `9ac0cac`, about 900 ms after `b518738`.

### Whole-tile stages (`tools/perf/ab.ps1 -Probe tile`, after `e17678e`)

Single thread, ms per round of five samples (10719, 3181, 3167, 1845, 10026), minimum of 3 runs, 10 warm-up
rounds:

| Stage | Default | Tiering off | Tiering and ReadyToRun off |
|---|---|---|---|
| T2 | 18.5 | 17.8 | 17.5 |
| T1 | 658.8 | 713.8 | 727.2 |
| DWT | 88.3 | 85.4 | 87.3 |
| MCT | 4.3 | 4.0 | 4.4 |
| DC level shift | 13.7 | | |
| Tile to image (`UpdateImageData`) | 37.3 | | |
| Round | 835.6 | 881.9 | 899.7 |

- T1 is 87% of the round. Dynamic PGO (tiered compilation) is worth about 8% on it: keep `AggressiveOptimization`
  off the T1 passes.
- ReadyToRun: no stage is slower with the precompiled framework code, so no other copy has the `Memmove` cost
  found in `d48f2d2`.
- The DC level shift and the copy into the image are about 6% of the round together.

Per sample (same probe, minimum of 3 runs): the copy into the image only costs on tiled images (10719 27.2 ms
of 631, 3167 6.3 of 71, 4649 2.0 of 25; single-tile images hand the tile buffer over), the DC level shift most
on the 9/7 colour image 3181 (6.3 ms of 72).

### DC level shift in the MCT pass (`7aefdb2`)

`ab.ps1 -Probe tile`, `0429ecf` vs `7aefdb2`, samples 3181, 3167, 10719, 1845, 6 runs:

| Stage (ms) | Warm, before | Warm, after | First decode, before | First decode, after |
|---|---|---|---|---|
| MCT | 4.33 | 5.95 | 5.65 | 8.57 |
| DC level shift | 12.47 | 3.83 | 25.69 | 5.19 |
| **MCT + DC level shift** | 16.80 | **9.78 (-42%)** | 31.34 | **13.76 (-56%)** |
| Round | 783.1 | 769.6 (-1.7%) | | |

"First decode": one decode per process, no warm-up (8 runs). The DC level shift left after the change is the
grey image (1845), which has no MCT. On 3181, 1845 and 10026 the first step alone (optimised loops, no fusion)
took the DC level shift from 17.9 to 10.3 ms on a first decode and from 9.4 to 8.9 ms warm.

### 9/7 DWT on a first decode (`f844085`)

`ab.ps1 -Probe tile`, `032eab4` vs `f844085`, the 9/7 samples 3181, 3167, 1845 and 4649, runs alternating:

| ms per round | Before | After |
|---|---|---|
| DWT, first decode (`-Warm 0 -Rounds 1`) | 279.8 | **83.1 (-70%)** |
| Round, first decode | 611.1 | **427.5 (-30%)** |
| DWT, warm | | -0.9% (noise) |
| Round, warm | 189.1 | 188.7 |

On a first decode the 9/7 DWT had cost 3.4 times its warm time; it is now close to warm. The 5/3 DWT (kernels
marked since `d48f2d2`) was 24.2 ms on a first decode against 19.6 ms warm, and `UpdateImageData` and `MCTDecode`
were 2-3 ms slower than warm: not changed. Most of the first-decode cost left is T1 (see `AggressiveOptimization`
under "Things that did not help").

### All cores: DWT job count (`aedd1d1`) and worker threads (`378c0d4`)

Machine: i9-14900HX, 8 performance + 16 efficiency cores, 32 threads. All numbers are this machine's: the thresholds
below were not tried on other core counts.

**Stage times, all cores** (`PROBE_MT=1 ./tools/perf/ab.ps1 -Probe tile -NoBaseline -Warm 10 -Rounds 10`, minimum of
3 runs, ms per round of MOZILLA-10719-0_3; 3507x2480, 140 tiles of 256x256):

| Stage | `f04357e` | `aedd1d1` | `378c0d4` |
|---|---|---|---|
| T2 | 13.3 | 14.2 | 14.2 |
| T1 | 89.4 | 119.6 | **65.9** |
| DWT | **127.5** | 15.1 | 15.0 |
| Tile to image | 21.1 | 16.4 | 13.2 |
| **Round** | 263.6 | 179.6 | **120.7** |

The DWT of 10719 took 127 ms on all cores against 12 to 19 ms on one thread: every resolution level of every tile
was split into up to 32 jobs (down to one row each), each renting a pooled buffer and going through
`Parallel.For`, for passes of about 20 us. `aedd1d1` caps a pass at `samples / 65536` jobs (inline below 128K
samples). T1 got slower in that step (89 to 120 ms): the DWT had kept the pool threads awake between tiles.

Threshold (minimum samples per job) sweep, same probe:

| Min samples per job | 10719 round (ms) | 7-file round (ms) |
|---|---|---|
| 1 (no cap) | 275.1 | 426.8 |
| 16384 | | 317.0 |
| 32768 | 179.5 | 303.2 |
| 65536 | 179.6 | 305.2 |
| 131072 | 188.6 | 312.6 |
| 262144 | 189.4 | |

Flat from 32K to 131K; 65536 taken. The seven files are the samples of the tables above.

**Whole decodes, NativeAOT harness, all cores** (alternating A/B, 3 runs each, minimum of the medians; each pair is
its own run, so the `aedd1d1` column differs a little between them):

| Sample | `f04357e` | `aedd1d1` | | `aedd1d1` | `378c0d4` |
|---|---|---|---|---|---|
| MOZILLA-10719-0_3 | 255.0 ms | 173.5 ms (-32%) | | 182.4 ms | 118.8 ms (-35%) |
| MOZILLA-LINK-3167-0_10 | 36.9 | 33.6 (-9%) | | 32.9 | 29.7 (-10%) |
| MOZILLA-LINK-3181-0_5 | 28.4 | 27.4 (-4%) | | 27.8 | 26.8 (-4%) |
| PDFBOX-1845-0_49 | 12.2 | 12.3 (noise) | | 12.1 | 11.2 (-7%) |
| MOZILLA-10026-0_0 | 9.9 | 9.8 (noise) | | 9.5 | 8.6 (-9%) |
| MOZILLA-8179-0_1 | 6.7 | 6.1 (-9%) | | 6.5 | 4.1 (-37%) |
| MOZILLA-LINK-4649-0_10 | 16.4 | 15.9 (-3%) | | 15.5 | 10.9 (-30%) |

Single thread did not change in either step (within +-5%; 10026 can swing 9%). Allocation: 10719 all cores 153.7 MB
to 127.7 MB with the job cap (no per-job buffers at the small levels), unchanged by the pool.

**Where T1 time went on 10719** (all cores, per tile; 204 code-blocks per tile, measured with temporary counters in
`Tier1Coding.ThreadWorker` and `TileCoder.RunThreads`, 20 rounds including warm-up):

| | `Parallel.ForEach` | Pool, no spin | Pool, spin 2047 |
|---|---|---|---|
| Wall time of the T1 call | 0.90 to 1.03 ms | 0.56 ms | 0.45 ms |
| CPU time of all blocks | 6.2 to 7.3 ms | 9.9 ms | 9.9 ms |
| Longest block | 0.11 to 0.14 ms | 0.25 ms | 0.24 ms |
| Threads that ran a block | 10.9 to 12.9 | 27.6 | 28.6 |
| Average first start after the call | 0.37 to 0.40 ms | 0.17 ms | 0.06 ms |
| T1 stage, ms per round | 111 to 131 | 65 | 52 |
| Round, ms | 173 to 181 | 125 | 131 |

Single-threaded, a block costs 20 us (560 ms over 140 tiles x 204). Perfect scaling would be 0.19 ms per tile, and
the longest block is a 0.11 ms floor; the join after the last block was 0.01 ms. So the time was not the work and
not the join: the .NET thread pool wakes its workers one at a time (each woken worker requests the next), so most
threads arrived after the work was mostly done. Thread caps with `Parallel.ForEach` (before the pool): T1 345 ms at
2 threads, 204 at 4, 138 at 8, 131 at 16, 123 at 32. The CPU time per block is also 1.5x its single-thread value on
32 threads (efficiency cores, shared caches), which is not scheduling.

**Tasks instead of dedicated threads** (same batch and counter, only the way the workers are started; 10719, 4 runs,
all cores):

| Workers started with | T1 (ms per round) | Round (ms) |
|---|---|---|
| Dedicated threads, signalled directly (`378c0d4`) | **63.7** | **113.0** |
| `ThreadPool.UnsafeQueueUserWorkItem`, one item per worker | 109.8 | 159.8 |
| `Task.Run`, one task per worker | 109.1 | 159.6 |

Both go through the .NET thread pool, which is what wakes threads one after another, and both land on the
`Parallel.ForEach` figure above. A `Task` with `TaskCreationOptions.LongRunning` is a dedicated thread, so it
would only move the `new Thread` call.

### Single-tile images on all cores: MCT and DC level shift (`dd7e282`)

After `378c0d4` the stages of the single-tile images (the usual PDF case) on all cores, `PROBE_MT=1 ab.ps1 -Probe tile`,
30 warm-up rounds, 20 rounds, minimum of 3 runs, ms per decode:

| Sample | Round | T2 | T1 | DWT | MCT | DC shift | Tile to image | In no stage |
|---|---|---|---|---|---|---|---|---|
| MOZILLA-LINK-3181-0_5 (9/7 RGB) | 27.0 | 0.4 | 3.0 | **12.3** | **4.4** | | | 7.0 |
| PDFBOX-1845-0_49 (9/7 grey) | 12.4 | 0.3 | 2.6 | **5.3** | | **1.8** | | 2.3 |
| MOZILLA-10026-0_0 (1-bit page) | 9.6 | 0.3 | 1.2 | **3.4** | | **1.5** | | 3.2 |
| MOZILLA-LINK-3167-0_10 (tiled) | 27.5 | 0.7 | 7.7 | 7.8 | 1.9 | | 6.9 | |
| 3181, one thread | 62.4 | 0.4 | 11.1 | 40.1 | 4.5 | | | |

The MCT (and the DC level shift of the grey images) ran on one thread: 4.4 ms of 27 on 3181, 1.8 of 12.4 on 1845, the
same as single-threaded. They are per-sample loops, so `dd7e282` splits them over the workers in ranges of at least
65536 samples (inline below 131072). Probe, same setup, ms (round in brackets):

| Min samples per range | 3181 MCT (round) | 1845 DC shift (round) |
|---|---|---|
| inline | 4.66 (27.40) | 1.89 (12.52) |
| 8192 | 2.00 (26.06) | 0.41 (11.99) |
| 16384 | 1.91 (25.85) | 0.46 (11.14) |
| 32768 | 1.98 (25.96) | 0.40 (10.57) |
| 65536 | 1.95 (25.42) | 0.43 (11.37) |
| 131072 | 2.00 (26.10) | 0.48 (10.82) |
| 524288 | 2.12 (25.77) | 0.49 (10.97) |

Flat over a wide range; 65536 taken. 10026 DC shift 1.62 -> 0.38 ms. Whole decodes, NativeAOT harness, all cores
(alternating A/B against `378c0d4`, 3 runs each, minimum of the medians):

| Sample | `378c0d4` | `dd7e282` |
|---|---|---|
| PDFBOX-1845-0_49 | 11.5 ms | 9.7 ms (-16%) |
| MOZILLA-LINK-3181-0_5 | 26.5 | 23.0 (-13%) |
| MOZILLA-10026-0_0 | 8.7 | 7.7 (-11%) |
| MOZILLA-LINK-3167-0_10 | 29.8 | 27.5 (-8%) |
| MOZILLA-LINK-4649-0_10 | 11.3 | 10.5 (-7%) |
| MOZILLA-10719-0_3 | 116.8 | 120.1 (noise: 65K-sample tiles stay inline) |
| MOZILLA-8179-0_1 | 4.2 | 4.3 (noise) |

Single thread and allocation did not change. The MCT is still about 2 ms on 3181 (fork/join and memory traffic; the
single-thread cost of the loop is 4.7 ms). What is left on single-tile images on all cores is the DWT (12.3 of 27 ms
on 3181, 3.3x faster than on one thread on 32 threads) and 2 to 7 ms per decode that no stage timer covers.

### DWT level by level on all cores, and the 5/3 vertical job width (`b53fdb3`)

Temporary timers around the horizontal and vertical pass of every resolution level of `decode_tile_97` and
`decode_tile` (not committed; median per component and level over the measured rounds, warm-up dropped), run with
`PROBE_MT=1` and without. `level 4` is the full resolution; the samples of a level are the plane the two passes cover.

| ms per component | Samples | 3181 9/7, h | v | 1845 9/7, h | v | 10026 5/3, h | v |
|---|---|---|---|---|---|---|---|
| Level 4, 1 thread | 7.4M / 8.7M / 8.8M | 4.75 | 7.19 | 5.58 | 8.07 | 2.13 | 2.99 |
| Level 4, all cores | | 1.62 | 2.30 | 1.58 | 2.47 | **0.44** | **2.24** |
| Level 3, 1 thread | 1.8M / 2.2M / 2.2M | 0.95 | 1.30 | 1.08 | 1.67 | 0.58 | 0.65 |
| Level 3, all cores | | 0.57 | 0.77 | 0.63 | 0.75 | 0.23 | 0.72 |
| Level 2, all cores | 0.46M / 0.54M / 0.55M | 0.23 | 0.25 | 0.20 | 0.27 | 0.12 | 0.37 |

Levels 0 and 1 are at most 0.1 ms each (inline). Level 4 is two thirds of the DWT of a 9/7 image. The 5/3 vertical
pass of 10026 was the outlier: 5 times its horizontal pass on all cores and 1.3x faster than one thread.

Thread count (`JPX_DWT_THREADS`, a temporary cap on the DWT's thread count), level 4, h / v in ms, DWT stage in
brackets:

| Threads | 3181 9/7 | 10026 5/3 before the fix | 10026 5/3 after the fix |
|---|---|---|---|
| 2 | 3.11 / 3.81 (27.9) | 1.00 / 1.31 (3.6) | |
| 4 | 2.10 / 3.69 (23.8) | 0.83 / 1.14 (3.7) | 0.62 / 0.89 (2.5) |
| 8 | 1.51 / 2.46 (17.5) | 0.62 / 1.17 (3.5) | 0.62 / 0.78 (2.5) |
| 16 | 1.55 / 1.81 (15.7) | 0.65 / **1.96** (4.5) | 0.63 / 0.92 (2.8) |
| 32 | 1.45 / 1.94 (18.2) | 0.37 / **2.28** (4.3) | 0.46 / 0.83 (2.4) |

The 5/3 vertical pass got slower from 8 threads up. The horizontal pass splits rows and kept scaling; the vertical
pass splits columns, in jobs of `rw / num_jobs` columns (78 for 2496 wide and 32 jobs), so a job's range ended inside
a cache line that the next job also wrote on every row. Rounding the job width up to a multiple of 16 ints
(`b53fdb3`), 10026, 32 threads, level 4 vertical pass; the first row of identical code shows the run-to-run spread
(2.2 to 2.6 ms):

| Job width rounded up to | Level 4 v (ms) | DWT stage (ms) |
|---|---|---|
| none | 2.28 | 4.32 |
| 16 | **0.78** | 2.46 |
| 32 | 1.07 | 2.41 |
| 64 | 0.99 | 2.54 |
| 128 | 0.91 | 2.33 |
| 256 | 1.10 | 2.61 |

Whole decode, NativeAOT harness, all cores, alternating A/B against `dd7e282`: MOZILLA-10026-0_0 9.4 -> 8.0 ms
(**-15%**); the other six samples (9/7 or tiled with 65K-sample tiles, so on another path) within +-6%, single thread
and allocation unchanged. The machine ran noisier that session (10719 all cores 151 ms against ~118 in earlier ones);
the runs alternate, so the pair is still fair.

Not a memory-bandwidth limit: an in-place pass over a 29.5 MB plane (one level-4 plane, `Vector<int>` add) takes
0.86 ms on 1 thread, 0.35 on 8 and 0.22 on 24 or 32 (270 GB/s: the plane sits in the 36 MB L3), against 1.6 to 2.3 ms
for the real 9/7 passes.

The same rounding on the 9/7 vertical pass (job widths of 8 columns, rounded up to 16, 32, 64) changed nothing: 3181
level 4 v 1.93 / 1.96 / 1.85 / 1.94 ms for none / 16 / 32 / 64, 1845 2.17 / 2.11 / 2.18 / 2.24. The 9/7 passes
plateau from 8 threads (h) and 16 threads (v) at 1.5 to 2 ms for a different reason, not found yet.

### Start and finish of the jobs of a 9/7 pass

Temporary wrapper around `Parallelism.RunAll` for the horizontal and vertical jobs of `decode_tile_97` (start, end and
thread of every job, relative to the call), full-resolution passes only (more than 5M samples), `PROBE_MT=1`, 30
warm-up rounds, 20 rounds, median over the last third of the passes, ms:

| | 3181 h (32 jobs) | 3181 v (16 jobs) | 1845 h (32 jobs) | 1845 v (16 jobs) |
|---|---|---|---|---|
| Job creation (renting buffers, cloning) | 0.004 | 0.002 | | |
| Wall time of `RunAll` | 1.07 | 1.39 | 1.09 | 1.67 |
| First job starts after the call | 0.009 | 0.011 | | |
| Average job start | **0.22** | 0.18 | | |
| Last job start | 0.36 | 0.34 | | |
| Job duration, min / average / max | 0.14 / 0.41 / 0.78 | 0.39 / 0.67 / 1.14 | 0.20 / 0.44 / 0.82 | 0.51 / 0.89 / 1.45 |
| Return after the last job ends | 0.001 | 0.001 | | |
| Distinct threads | 28 | 16 | 29 | 16 |

- Job creation and the join cost nothing. The workers start late: of the 32 horizontal jobs of one pass, 3 started
  within 0.02 ms, 5 at about 0.12 ms and the rest between 0.19 and 0.36 ms (the caller signals 31 events one after
  another and each worker has to be woken by the OS). The average start is a fifth of the pass.
- Jobs of the same size differ by 3 to 5x in duration. The fastest horizontal job (0.14 ms) runs at the single-thread
  rate (4.75 ms / 32 jobs = 0.15 ms), the slowest at about a fifth of it, and the late starters are the slow ones:
  with most of the 32 threads busy they share cores (hyper-threads) and run on efficiency cores. The CPU time of all
  jobs is 2.6x the single-thread time. A pass lasts as long as its last start plus its slowest job.

Finer jobs (`DwtJobCount` with `JobsF` times the threads, the wavelet buffer rented when the job runs instead of when
it is created, so no more buffers are live), DWT stage / round in ms, 3 runs:

| Jobs | 3181 DWT | 3181 round | 1845 DWT | 1845 round |
|---|---|---|---|---|
| 1x threads (now) | 13.75 | 24.67 | 5.20 | 10.43 |
| 2x | 12.90 | 24.17 | 5.40 | 10.63 |
| 4x | 16.41 | 27.53 | 6.22 | 11.67 |
| 8x | 18.74 | 29.89 | 6.01 | 11.29 |
| 16x | 18.98 | 30.12 | 5.71 | 11.08 |

(3181 vertical pass: 1.49 ms with 16 jobs, 1.29 with 32, 1.76 with 64; the slowest job of 64 still took 1.09 ms.)

Workers that spin for a bounded time after a batch before blocking (`Stopwatch`-timed loop of `Thread.SpinWait`, so the
back-to-back h and v passes meet hot workers), probe, 3 runs. The same code run five times gives the noise: 3181 DWT
13.7 to 14.2 ms, 1845 4.9 to 5.5, 10026 1.84 to 1.96.

| Spin | 3181 DWT (round) | 1845 DWT (round) | 10026 DWT (round) | 10719 T1 / DWT (round) | 3167 round | 4649 round |
|---|---|---|---|---|---|---|
| none | 14.30 (25.5) | 5.58 (11.7) | 1.91 (7.43) | 65.9 / 14.8 (122.1) | 28.7 | 13.00 |
| 20 us | 12.22 (24.6) | 5.85 (10.8) | 1.85 (7.49) | 65.2 / 15.4 (119.7) | 29.8 | 13.58 |
| 50 us | 12.30 (24.9) | 6.11 (10.9) | 1.63 (7.33) | 64.7 / 16.1 (120.9) | 29.4 | 13.63 |
| 100 us | 11.97 (24.0) | 6.08 (10.9) | 1.57 (7.35) | 63.9 / 17.7 (122.8) | 28.7 | 13.71 |
| 200 us | 11.67 (23.8) | 5.92 (11.1) | 1.39 (6.97) | 52.7 / 19.7 (116.8) | 28.8 | 14.19 |

The large single-tile images gain in the DWT (3181 -16 to -18%, 10026 up to -27%) and 3 to 7% in the round; 1845
gains nothing; the small tiled 4649 loses 5 to 9%; on 10719 T1 gets faster and the inline DWT slower, because the
spinning workers share cores with the thread running the serial stages.

### Setup work outside the stage timers, and the uninitialised tile buffer (`37b7a76`)

On single-tile images on all cores 2 to 7 ms per decode were in no stage (a quarter of 3181, a third of 10026).
Temporary timers (not committed) around `ReadHeader`, `CompressionInfo.Decode`, `J2K.DecodeTiles`, `ReadTileHeader`'s
`InitDecodeTile`, `TileCoder.DecodeTile`, the allocation of the tile buffers (`AllocTileComponentData`) and the move
into the output image, per decode, median of the last third of 50 decodes (30 warm-up rounds), ms, with the stage
probe's round:

| | 3181 all | 1845 all | 10026 all | 3181 1 thread | 1845 1 thread | 10026 1 thread |
|---|---|---|---|---|---|---|
| Round (probe) | 25.15 | 11.45 | 7.87 | 66.57 | 30.24 | 17.77 |
| `CompressionInfo.Decode` | 24.52 | 11.19 | 7.41 | 66.72 | 29.65 | 17.55 |
| `TileCoder.DecodeTile` | 24.09 | 11.00 | 7.22 | 66.33 | 29.50 | 17.39 |
| of which **allocating the tile buffers** | **4.96** | **2.11** | **2.74** | 5.17 | 1.95 | 2.74 |
| `InitDecodeTile` | 0.38 | 0.22 | 0.18 | 0.35 | 0.14 | 0.14 |
| `ReadHeader` | 0.04 | 0.02 | 0.02 | 0.03 | 0.02 | 0.02 |
| Move into the output image | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |

The gap between the round and `Decode` is the probe's own `CompressionInfo` / `MemoryStream` setup (0.3 to 0.6 ms).
The only large piece is the zeroed `new int[n]` of the tile components: 3 x 29.5 MB for 3181 (88 MB), 20% of its
decode on all cores, 19% of 1845's and 37% of 10026's.

`37b7a76`: `AllocTileComponentData` takes the buffer from `GC.AllocateUninitializedArray<int>` (NET5+; older targets keep
`new int[]`). Whole decodes, NativeAOT harness, alternating A/B against `b53fdb3`, 3 runs, minimum of the medians:

| Sample | 1 thread before | now | all cores before | now |
|---|---|---|---|---|
| MOZILLA-10026-0_0 | 15.7 ms | 15.6 | 7.4 | **5.6 (-24%)** |
| MOZILLA-LINK-3181-0_5 | 62.3 | 58.2 (-7%) | 22.9 | **17.8 (-22%)** |
| PDFBOX-1845-0_49 | 31.0 | 30.0 (-3%) | 9.6 | **8.3 (-14%)** |
| MOZILLA-10719-0_3 | 727.0 | 730.2 | 120.1 | 115.8 (-4%) |
| MOZILLA-LINK-3167-0_10 | 79.3 | 81.9 | 27.2 | 27.3 |
| MOZILLA-8179-0_1 | 24.4 | 23.6 | 4.3 | 4.2 |
| MOZILLA-LINK-4649-0_10 | 27.9 | 28.4 | 10.6 | 10.7 |

The first decode of a process gains the same: all cores 3181 24.0 -> 18.9 ms (-21%), 1845 11.8 -> 9.2 (-22%), 10026
8.5 -> 7.0 (-18%); one thread 10026 16.6 -> 15.3 ms. Allocation is the same (same arrays). The tiled images (10719,
3167, 4649) do not change: their tile buffer is allocated once per decode and reused.

### Other large allocations: what is left, and the poison check of the output image

Allocation per decode by type, one thread, CoreCLR net8.0 JIT, after `37b7a76`: an `EventListener` on the runtime's
`GCAllocationTick` events (one per ~100 KB), summed per type name over 10 decodes after 5 warm-up decodes. The
runtime's own total (`GC.GetTotalAllocatedBytes(true)`) is unchanged by the listener (10719: 178.4 MiB without it,
178.9 with it). MiB per decode:

| Type | 3181 | 1845 | 10026 | 10719 (tiled) | 3167 (tiled) |
|---|---|---|---|---|---|
| Total | 89.00 | 35.19 | 35.61 | 178.88 | 52.25 |
| `Int32[]` | 84.44 | 33.18 | 33.46 | 100.37 | 44.93 |
| `TagNode[]` | 2.03 | 0.88 | 1.22 | **54.71** | 3.64 |
| `TcdSeg` / `TcdSeg[]` | 0.57 | 0.28 | 0.32 | 9.01 | 1.39 |
| `TcdCblkDec` | 0.55 | 0.22 | 0.16 | | |
| `T1CBLKDecodeProcessingJob` | 0.41 | 0.13 | 0.15 | 1.70 | 0.32 |
| `Byte[]` | 0.06 | | | 8.22 | 0.33 |
| `Single[]` (9/7 wavelet buffer) | 0.19 | 0.13 | | | 0.87 |

On the single-tile images the `Int32[]` is the tile buffers (3 x 28.1 MiB on 3181, one buffer on the others): already
uninitialised. On the tiled images it is the output image (99.3 MiB of 10719's 100.4): the only large zeroed
allocation left. The rest are small objects: on 10719 the tag trees and segments that `InitDecodeTile` builds for
each of the 140 tiles (54.7 MiB of `TagNode[]`, 30% of the allocation).

The NativeAOT harness reports 125 MiB for 10719, not 178: the harness builds against .NET 10, which stack-allocates
small non-escaping objects and arrays. The earlier BenchmarkDotNet figure (185 MB, `9ac0cac`) agrees with the JIT run.

Poison check of the output image (`J2K.UpdateImageData`, `new int[width * height]` on the first tile) and of the
partial-decoding buffer (`tilec.data_win`): both allocated with `GC.AllocateUninitializedArray` and filled with
`0x5A5A5A5A` (temporary, env switch):

| Corpus / mode | Result |
|---|---|
| Conformance + non-regression, default | **314/315**: `issue726.j2k` differs in 61440 of 62080 samples (the first 640 are right) |
| Conformance, `PARITY_AREA=1` (`data_win`) | 315/315 |
| PDF streams | 1404/1404 |

`issue726.j2k` is a truncated stream that decodes successfully with most of the image never covered by a tile;
upstream gives zeros there (it clears the image when the first tile does not cover it), and so did the zeroed array.
So the output image cannot be left uninitialised as it is. `data_win` can (area decoding only, which PdfPig does not
use).

What zeroing the output image costs (`ab.ps1 -Probe tile`, HEAD against the uninitialised image, 3 tiled samples
10719 + 3167 + 4649, minimum of 6 runs, 20 warm-up rounds), ms per round:

| | One thread: zeroed | uninitialised | All cores: zeroed | uninitialised |
|---|---|---|---|---|
| Tile to image | 32.05 | 26.79 (-16%) | 23.71 | 18.14 (-23%) |
| Round | 690.3 | 686.6 (-0.5%) | 160.9 | 149.5 (-7%) |

About 5.5 ms: 0.8% of a one-thread round and about 3.5% of an all-cores one (the all-cores round moved 7%, but T1 and
T2, which the change does not touch, moved -4% and -6% in the same run). Not taken: a correct version has to track
which tiles were written and zero the rectangles of the others (components with subsampling and the reduce factor
included), for that gain. `issue726.j2k` would be its regression test.

### Per-tile setup on tiled images: `InitDecodeTile` timed

The tag trees and segments that `InitDecodeTile` builds for each tile are 54.7 MiB of `TagNode[]` and 9 MiB of
`TcdSeg` per decode on 10719. Temporary timers (not committed) around `InitDecodeTile`, `ReadTileHeader` (marker
parsing, tile-part data copy, `InitDecodeTile`) and `TileCoder.DecodeTile`, with call counts, `ab.ps1 -Probe tile`,
20 warm-up rounds, median of 20, ms per decode:

| Sample (tiles) | Mode | Round | `InitDecodeTile` total (per tile) | Share of round | `ReadTileHeader` total | `DecodeTile` per tile |
|---|---|---|---|---|---|---|
| 10719 (140) | 1 thread | 586.5 | 4.05 (0.029) | 0.7% | 6.76 | 3.99 |
| 10719 (140) | all cores | 111.4 | 3.97 (0.028) | **3.6%** | 5.91 | 0.66 |
| 3167 (15) | 1 thread | 69.96 | 0.50 (0.033) | 0.7% | 0.59 | |
| 3167 (15) | all cores | 26.73 | 0.53 (0.036) | 2.0% | 0.62 | |
| 4649 (12) | 1 thread | 25.38 | 0.32 (0.026) | 1.2% | 0.39 | |
| 4649 (12) | all cores | 13.68 | 0.38 (0.031) | 2.3% | 0.47 | |

Setting up a tile costs 0.03 ms, the same on one thread and on all cores (it is serial), against 4.0 ms (one thread)
or 0.66 ms (all cores) to decode it. The 0.39 MiB of tag trees per tile of 10719 are gen-0 allocations built in about
29 us. Reusing the tile structures across tiles could save 3.6% of 10719's all-cores round at most (less, since part
of the 29 us is filling the structures, not allocating them) and 1% or less on one thread, for a change that touches
a large object graph. Not done.

The tile-to-image copy (`UpdateImageData`, 13 ms of 10719's all-cores round) was sized by reading, not timed
with a change: it is a `Buffer.BlockCopy` row loop, 0.09 ms per tile (3 components x 256 KB), mostly first-touch page
faults on the zeroed output image. Splitting it over threads would pay a wake-up (0.2 ms on average, see the 9/7 job
trace) for less work than that. Not tried.

### Managed vs native C, same process (BenchmarkDotNet `NativeComparisonBenchmarks`, before `b518738`)

| Threads | Managed / native |
|---|---|
| 1 | 1.17 - 1.67x slower |
| all | 0.45 - 0.89x (faster; upstream's thread pool barely helps on these files) |

### JIT vs NativeAOT (`benchmarks/aot` harness, after `ed9a31b`)

First decode in a fresh process vs median of the next 9.

| Sample, 1 thread | JIT first | JIT warm | AOT first | AOT warm |
|---|---|---|---|---|
| MOZILLA-10719-0_3 | 1110 ms | 794 ms | 836 ms | 836 ms |
| MOZILLA-LINK-3167-0_10 | 341 ms | 134 ms | 122 ms | 108 ms |
| MOZILLA-LINK-3181-0_5 | 121 ms | 113 ms | 110 ms | 115 ms |
| PDFBOX-1845-0_49 | 54 ms | 52 ms | 50 ms | 49 ms |

NativeAOT has no warm-up cost and is within 1.0 - 1.4x of native C single-threaded. Under the JIT, the large T1
and DWT methods start as unoptimised tier-0 code, so a process that decodes a few images pays 1.3 - 2.6x.

## What was changed

### T1 (`b518738`)

- MQ contexts hold their packed state entry (`Qe | nmps << 16 | nlps << 24 | mps << 31`) instead of a pointer to a
  state object. The common decode path is one load; the table (padded to 128) is read only on a transition.
- The 10 coding passes decode on a local copy of the MQ registers (`MqcRegs`, upstream's
  `DOWNLOAD/UPLOAD_MQC_VARIABLES`). It must be filled by field assignments: a non-inlined struct-returning call
  pins the struct to the stack.
- Renormalisation in one shift (`lzcnt`) when no byte is needed.
- `UpdateFlagsMacro` takes and returns the flags value (no `ref` to a local); lookup tables are `static readonly`;
  arrays and the zero-coding orientation are read into locals once per pass.
- One T1 context per thread, reused across code-blocks (was: one per code-block).

### T1 branch-free updates (`8b58ba5`)

- The JIT compiled three choices that depend on a just-decoded bit as conditional jumps, which mispredict about half
  the time; C compilers emit branch-free code for the same upstream macros:
  - the sign of a newly significant coefficient (`v ? -oneplushalf : oneplushalf`, 51 sites) -> `NegateIf`,
    `(x ^ -m) + m`;
  - the refinement update (`(v ^ (x < 0)) ? poshalf : -poshalf`, 10 sites) -> `RefineSample`;
  - the refinement context (`Getctxno_mag`) -> `MAG + ((nb | mu << 1) - (mu & nb))`.
- The MQ context is passed to each `Decode` call instead of living in `MqcRegs`: it is set right before every
  decode, so it no longer occupies a register across the loops. This reduced spills in two passes but did not change
  timings on its own.

### T1 MQ decode (`b187729`)

- `ByteIn` (reads the next byte, once per 8 decoded bits) is inlined into the pass decoder. It was kept out of line
  as a rare path, but any call inside the pass loops clobbers the caller-saved registers, so the JIT kept the loop
  variables (`i`, `data`, `flagsp`) in stack slots for the whole loop. Passes -5% (significance), -8% (refinement),
  -9% (cleanup); every run of the change was faster than every baseline run. The instance `ByteIn()` used by the
  rare paths (segmentation symbol, tail-row step helpers) stays non-inlined.
- MPS/LPS exchange without a branch on `a < qeval`: `lps = cond ? 1 : 0` (`setcc`), then
  `d = mps ^ lps` and the next state is `(e >> (16 + 8 * lps)) & 0x7F`. A further -1% to -4% per pass, confirmed
  in a second A/B run. MSVC's build of upstream does the same with `cmovae`.

### DWT (`ed9a31b`, earlier SIMD in `9ac0cac`)

- 9/7 lifting steps and the ICT/RCT, DC level shift: `Vector<T>` (all targets, ARM included; integer ones .NET 7+).
- 5/3 vertical pass: `Vector<int>` port of `opj_idwt53_v_cas0/1_mcols_SIMD`, 16 columns.
- 5/3 horizontal pass, cas0: port of upstream's AVX2 branch (`e0e0c802`).
- 9/7 vertical pass: one vector load/store per 8-sample row instead of `Buffer.BlockCopy` per row.
- 9/7 horizontal pass: gather and write-back through AVX 8x8 transposes (not in upstream, which is scalar here).
- `interleave_h` fast path: the alignment test ported from upstream checked array indices, which excluded every odd
  element. Removed (both paths give the same result).
- Lifting loops step vector indices directly (the JIT emitted three signed divisions per iteration).
- `int_add_no_overflow` / `int_sub_no_overflow`: `unchecked(a + b)` instead of an explicit-layout union.

### DWT per-row overhead (`f9b203e`)

- 5/3 vertical pass: the 16 decoded columns are written back with vector stores (upstream
  `opj_idwt53_v_final_memcpy`) instead of one `Array.Copy` call per row.
- 5/3 row copy-back after the horizontal pass: span copies instead of `Buffer.BlockCopy`.
- `idwt53_h_cas0_avx2`: one bounds check per row up front, then unchecked `LoadUnsafe` / `StoreUnsafe`.
- Whole-tile 5/3 decode: the working buffer is rented from `ArrayPool` instead of allocated per tile.
- 9/7 vertical pass (`VerticalStrips97`, single-threaded and job paths): two adjacent 8-column strips share each
  sweep over the rows. Each row read then covers a whole 64-byte line, and each page is walked once instead of
  twice; the interleave was TLB-bound on wide images (9.4 KB row stride on 3181). Each strip still goes through the
  same interleave, lifting and write-back, so the output is unchanged. Not in upstream.

### 9/7 lifting and vertical copies (`247af34`)

- `v8dwt_decode_step1/2`: one range check per call, then unchecked `Unsafe.Add` accesses. Step 2 carries the right
  neighbour of each update to the next one in a register (two loads per element instead of three) and step 1 is
  4x unrolled, both as in upstream's `_sse` versions. Same operations in the same order, so bit-identical. Lifting:
  -45% on 3167, -43% on 4649, -10% on 3181.
- `CopyWaveletRowsBack`, `CopyWaveletRowsBackPair`, `v8dwt_interleave_v_pair`: one range check per call, then
  unchecked unaligned vector loads and stores.
- Each fast path falls back to the checked loop if its range check fails; none did on the three samples. Only for
  8-float vectors (AVX2, and the default 256-bit `Vector<T>` with AVX-512); compiled out on netstandard2.1, which has
  no `Unsafe` class (the other targets get it in-box or through `System.Memory`).

### 5/3 DWT (`d48f2d2`)

- `idwt53_v_cas0/1_mcols`: with two vectors per 16 columns (AVX2), both halves share one sweep over the rows, as
  upstream's AVX2 code does, instead of two sweeps of 8 columns. One range check per call (`ColumnsInRange`: rows
  0 .. len - 1 and tmp), then unchecked unaligned loads and stores, including the copy back
  (`CopyColumnsBackUnchecked`).
- `idwt53_h_cas0_avx2`: one range check per row covers the vector loop and the scalar tail, which now carries the
  previous output in a register; a checked scalar fallback (`idwt53_h_cas0_checked`) handles anything else.
- `CopyRowBack`: own unchecked AVX loop for the end-of-row copy (.NET 7+), replacing `Span.CopyTo` in the five 5/3
  horizontal paths. `Span.CopyTo` runs the precompiled (ReadyToRun) `Buffer.Memmove` until tier-up, or always with
  tiering off, and that was 3x slower on these rows: 10719 horizontal pass with tiering off 31.6 ms, 9.5 ms with
  `DOTNET_ReadyToRun=0`. A bounds-checked `Vector<int>` loop fixed tiering off but cost 4 ms tiered on 10719.
- `[MethodImpl(AggressiveOptimization)]` on `idwt53_h`, `idwt53_h_cas0_avx2`, `idwt53_v_cas0/1_mcols` and
  `CopyColumnsBackUnchecked`. Under tiering the vertical kernel never left OSR code in 25 rounds of 10026 (one large
  tile: `decode_tile` runs once per round), and the fully optimised horizontal path took 10026 from 5.5 ms to
  3.4 ms. Unlike the blanket attempt below, these are plain vector loops with nothing for PGO to improve.

### 5/3 horizontal pass per row range (`436da40`)

- `idwt53_h_rows`: the single-thread loop and the threaded jobs make one call per range of rows instead of one
  `idwt53_h` call per row. For even-start rows on AVX2 it checks the range once for all rows (first row start to last
  row end, and the temp buffer), then runs the row kernel `idwt53_h_cas0_avx2_row` inlined in its loop. Anything
  else (cas1, no AVX2, the 512-bit path) still goes through `idwt53_h` row by row.
- `CopyRowUnchecked`: the unchecked copy loop of `CopyRowBack`, shared with the row kernel.
- 10719 horizontal pass -13%; 10026 (one large tile, few rows) unchanged.

### 5/3 horizontal AVX2 row kernel (`df30330`)

Rows of 18 samples or more; shorter rows keep the scalar loop. Same values as upstream, different loop:

- Both even vectors of a step (samples `b .. b + 7` and `b + 1 .. b + 8`) are computed from loads
  (`idwt53_h_cas0_avx2_step`). Upstream computes one and shifts it into the other with a lane permute, a broadcast,
  a blend and an extract; with the unpacks and the 128-bit store extracts that was about 8 shuffle-port uops per 16
  outputs, now 4. Out[0], which the general formula gets wrong at `b = 0`, is fixed after the loop.
- The scalar tail (up to 7 iterations, on every row whose length is a power of two) is replaced by one more vector
  step at `b = dn - 9`, the last one that stays inside the row. It overlaps the previous step and rewrites the
  same values.
- Outputs are written straight into the row; only the `sn` even inputs are copied to the temp buffer first. Step
  `b` writes `row[2b .. 2b + 15]` and the next step reads odd inputs from `row[sn + b + 7 ..]`, so writes stay
  behind reads (`b < sn`). The last step's odd inputs can lie under earlier outputs, so they are loaded before the
  loop, with the odd values the end of the row needs. Upstream writes the whole row to the temp buffer and copies
  all `len` values back; the copy-back alone measured 1.1 ms of 5.8 ms on 10719 (timed by skipping it).
- `CopyRowUnchecked` steps a `nuint` index (no sign extension per access).
- 10719 horizontal: -15% from the first two items and the `nuint` copy, -22% with the in-row writes.

### 5/3 vertical pass (`b793e62`)

On 10719 the copy-back from the temp buffer to the tile was more than half the pass (see the probes above). Same
values as upstream, different memory traffic:

- `idwt53_v_cas0_mcols2`: two adjacent 16-column strips (four vectors per row) in one sweep over the rows, for
  even-start columns on AVX2. A strip whose rows do not start on a 64-byte boundary spans two cache lines per row,
  one of them shared with the next strip; strip by strip it was fetched twice, the second time after eviction.
  10719 -13%, as much as 64-byte-aligned tile data gave. Upstream's AVX-512 build also does 32 columns per sweep.
- `idwt53_v_cols`: one loop for the single-thread path and each job (pairs, then single strips as upstream, then
  the narrow remainder). The temp buffer is twice upstream's: 2 x 16 columns x the tallest level (pooled; 10719
  +16 KB, 10026 +256 KB per thread).
- When the stride is not a multiple of 128 ints, output row `k + 1` is written into the tile in step `k`, right
  after that row is read (each tile row is read once, and its output is in the temp buffer by then); only the
  rows left are copied back after the loop. On 10026 the separate copy-back re-read a 1756-row strip from L2.
  10026 -18% on its own, -29% with the pairs. With a stride of a multiple of 128 ints, rows 4 or 8 apart share
  address bits 0-11, the write-backs falsely delay the next loads (4K aliasing), and the pass got 5-7% slower,
  so it is not used there. Both kernels (`mcols`, `mcols2`) have it.
- `CopyColumnsBackUnchecked` takes a first row.

### 5/3 vertical pass, odd-start columns (`e17678e`)

- `idwt53_v_cas1_mcols2` and the in-loop write-back in `idwt53_v_cas1_mcols`: the `b793e62` changes for columns
  whose top row is on an odd coordinate (`cas1`); `idwt53_v_cols` dispatches both.
- Not timed: no benchmark sample uses this path. In the corpora it runs only in about 15 conformance files, at
  most about 90k samples (under 0.1 ms) per decode, and in none of the 1 404 PDF streams. Ported for
  consistency. A temporary counter confirmed that the conformance files run all four new variants (pairs and
  single strips, with and without the in-loop write-back), so parity covers them.

### DC level shift (`7aefdb2`)

- The DC level shift and its vector row kernels ran once per tile component, so on single-tile images they never
  reached tier-1 code: after 22 decodes of 3181 the float row kernel was still instrumented tier-0 code (each
  `Vector<T>` operation a call) and the component loop OSR code. The per-component loops are now
  `TileCoder.DcShiftInt/DcShiftFloat` with `[AggressiveOptimization]` (plain loops, nothing for PGO), the row
  kernels inlined into them.
- Not in upstream: when the DC level shift of components 0 .. 2 covers exactly the samples the MCT transforms
  (whole decoded resolution, no stride, same wavelet; 9/7 precision 24 bits or less), `MCT.DecodeDcShift` and
  `MCT.DecodeRealDcShift` do both in one pass: one read and write of the three components instead of two. Each
  sample goes through the same operations in the same order. In the 9/7 kernel a vector of 8 whose r, g or b
  values are not all within |f| < 2^22 (or are NaN) takes the scalar formula, the same rule the DC level shift
  applies per row. Otherwise the two passes run as before.
- Coverage: the corpora run the fused kernels 6775 times (630 reversible, 6145 irreversible) and the separate MCT
  once; they never reach the per-vector fallback, so `FusedMctDcShiftTest` checks it (values of 2^22 and above,
  NaN, halves) against the MCT followed by the DC level shift. Breaking the fallback makes it fail.

### 9/7 DWT first decode (`f844085`)

- `[AggressiveOptimization]` on the 9/7 methods called once per tile or once per 8-row strip: `decode_tile_97`,
  `VerticalStrips97`, `v8dwt_interleave_h/v/v_pair`, `v8dwt_decode`, `v8dwt_decode_step1/2`, `WriteBackRows8`,
  `CopyWaveletRowsBack` and `CopyWaveletRowsBackPair`. On single-tile images they otherwise ran as tier-0 code
  (each `Vector<T>` operation a call) or OSR code for most of a first decode.
- They are plain vector loops with no virtual calls or type checks, so dynamic PGO had nothing to add: warm
  timings did not move. No code change otherwise, so the output is the same by construction (parity checked
  anyway).

### 5/3 horizontal pass, odd-start rows (`51074b6`)

- `idwt53_h_cas1_avx2_row`: rows whose left-most sample is on an odd coordinate (`cas1`) with at least 9
  high-pass samples now go through an AVX2 row kernel inlined into `idwt53_h_rows`, like the even-start rows
  (`df30330`). Upstream has no SIMD for this case (`opj_idwt53_h_cas1` is scalar). Same structure as the `cas0`
  kernel: only the low-pass inputs are copied out, the outputs go straight into the row, both low-pass vectors
  (k - 1 and k) come from loads, and a last overlapping step replaces the scalar tail. Integer only, same values.
  Shorter rows keep the scalar port.
- Not timed: no benchmark sample uses this path. In the corpora it runs only in the conformance files, 10 366 rows
  and 620k samples over all 315 files (under 1 ms), 25 rows with `PARITY_AREA=1`, and in none of the 1 404 PDF
  streams. Ported for consistency, like `e17678e`. A temporary counter confirmed that 8 162 conformance rows reach
  the new kernel (1 691 of even length, 6 471 odd), so parity covers both right boundaries.
- `Idwt53OddStartRowsTest` compares the kernel with the scalar port on lengths 3 to 70, three rows each, with
  values that make the sums wrap. Breaking the left-boundary fix makes it fail.

### 5/3 vertical pass, temp buffer alignment and direct stores (`180fb8e`)

- `idwt53_v_cols` pins the temp buffer (`GCHandle`) for its loop over strip pairs and starts the strips at the
  first 64-byte boundary in it. A rented .NET array is only 8-byte aligned, so each 32-byte row load or store in
  tmp could split a cache line; upstream's buffer is 32-byte aligned. The probes above show that the alignment is
  what helps, not the pinning. The buffer is rented 16 ints larger to leave room for the offset (falls back to
  offset 0 if it does not fit).
- `idwt53_v_cas0_mcols2`, only with the in-loop write-back (stride not a multiple of 128 ints): from the first even
  output row at or after `sn`, outputs go straight into the tile instead of tmp. Step `i` reads tile rows `i / 2 + 1`
  and `sn + i / 2 + 1`, so rows `sn .. i + 1` have been read already and are not needed again. Those rows skip
  tmp and the copy-back (`CopyRows2Back`, now called for the two remaining ranges). Not in upstream. The odd-start
  pair kernel (`cas1`, not hot) only gets the offset.
- Memory: `ArrayPool` rounds rents up to a power of two, so on power-of-two tile heights the 16 extra ints doubled
  the pooled buffer (256x256 tiles: 32 KB to 64 KB per thread). Fixed in `8dc4686`.

### 5/3 vertical pass, pooled buffer size (`8dc4686`)

- Both pair kernels (`idwt53_v_cas0/1_mcols2`) write their last output row straight into the tile instead of
  tmp. By the tail that row's input is in registers, and the tail only reads a row above it. tmp then needs rows
  0 .. len - 2, so 32 ints are free at its end, more than the offset can take (at most 14). The 16 spare ints are
  gone and the rent is the exact size again: 256x256 tiles back to 32 KB per thread. On other sizes the 16 ints
  only cost memory when the size was a power of two or less than 16 below one; otherwise the rent rounded up to
  the same bucket anyway.
- Same speed (A/B against `1673dae`): 10719 vertical 6.77 -> 6.76 ms. 10026 two runs gave +2.8% and -14.7% while
  the untouched horizontal pass moved -1% and -9.7%, so both were noise; the change does not affect it (no
  doubling there, one row per strip pair).

### DWT job count (`aedd1d1`)

- `DwtJobCount(num_threads, units, samples)` replaces the "one job per thread, at most one per row/column/strip"
  rule in the four threaded passes (5/3 and 9/7, horizontal and vertical): at most `samples / 65536` jobs, at most
  the number of independent rows, columns or strips, at most the thread count (the 9/7 vertical pass keeps its
  half). One job or none runs inline. Rows and columns are independent, so the partition cannot change a value.
- Not in upstream, which splits by thread count (its jobs are cheap on its C thread pool).
- Coverage: with the cap the conformance files still run the threaded branches (temporary counters: 137 passes each
  for the 5/3 horizontal and vertical, 102 each for the 9/7), so parity exercises them.

### Worker threads (`378c0d4`)

- `Parallelism.RunAll` (both overloads: T1 code-blocks and the DWT job lists) no longer uses `Parallel.ForEach` /
  `Parallel.For`. A `WorkerPool` of `ProcessorCount - 1` threads is signalled directly by the caller, which takes
  jobs from the same counter as the workers and then waits (spin, then `Thread.Yield`) until all jobs are done.
- Workers do not spin while idle: a spin window made T1 faster but the round slower (table above).
- A worker is idle, holds a batch, or is retired (no thread). A thread that was idle for 10 s exits, so the threads
  and the T1 context cached per thread do not outlive the work (the .NET pool retires its threads after about as long).
  A caller that finds no idle worker (other decodes use them) runs its jobs alone, so concurrent decodes never wait
  for each other.
- Exceptions: a job that throws is stored, the remaining jobs of the batch are skipped, and the first exception is
  rethrown on the caller. A thread that cannot be started leaves the work to the caller.
- The jobs of a T1 batch are now collected before the first one runs (the generator is no longer enumerated under
  the partitioner's lock).
- Verification beyond parity (which decodes one file at a time and cannot see any of this): a temporary stress
  script in the scratchpad decoded the seven samples from 8 and 32 concurrent decoders (hashes equal to the
  single-threaded decode), checked by reflection that all 31 workers retire after 13 idle seconds and are respawned
  by the next decode, and decoded 640 truncated or bit-flipped streams concurrently without a crash. It is not in
  the repository.

### MCT and DC level shift over the workers (`dd7e282`)

- `Parallelism.RunRanges(count, minPerRange, maxThreads, work)` splits `0 .. count` into at most one range per thread,
  each at least `minPerRange` items and a multiple of 64 long (it starts on a cache line); one range runs inline.
- `TileCoder.MCTDecode`: the four MCT kernels (reversible and irreversible, with and without the fused DC level shift)
  run per range of samples. They now take `Span<int>`, so a range needs no copy. The DC level shift
  (`DcLevelShiftDecode`) splits the rows of each component (`DcShiftInt` / `DcShiftFloat` already took a first row
  and a height). `SamplesPerJob` = 65536.
- Every sample goes through the same operations whatever the split (the 9/7 kernel's per-vector fallback decides per
  vector, and the result of each sample does not depend on its neighbours), so the output is the same.
- `MCT` keeps `int[]` overloads of the four kernels, used only by `FusedMctDcShiftTest`, which reaches them by
  reflection (a `Span` cannot be passed that way).
- Verification: parity was also run with a temporary 64-sample minimum, which splits nearly every image into many tiny
  ranges (default, area, no-AVX2 modes and the PDF corpus: clean), since at 65536 only large images reach the split.

### 5/3 vertical pass: job widths in whole cache lines (`b53fdb3`)

- In `decode_tile` the vertical jobs split the columns into `rw / num_jobs` each; the step is now rounded up to a
  multiple of 16 ints (64 bytes) and the job count recomputed (`ceil(rw / step)`; the last job takes the rest). Not
  in upstream. Columns are independent, so the values do not change.
- Measured in the table above: level 4 vertical pass of 10026 2.28 -> 0.78 ms, whole decode -15% on all cores.
  The width is aligned relative to the start of the array, not to its address (arrays are only 8-byte aligned); it
  worked on 10026, and the sweep (16 to 256) shows the choice of multiple is not critical.
- Verification: parity was also run with `DwtMinSamplesPerJob` temporarily at 256, which makes nearly every image take
  the threaded path with many odd-width jobs (default, area, no-AVX2, 512-bit and the PDF corpus: clean).

### Tile buffer not zeroed (`37b7a76`)

- `TcdTilecomp.AllocTileComponentData` (both of its allocations) goes through `NewTileData`:
  `GC.AllocateUninitializedArray<int>` on NET5+, `new int[]` on the older targets. Upstream's `opj_image_data_alloc`
  is not zeroed either, T1 writes every sample of the tile (an empty code-block as zeros), and the buffer was already
  reused dirty for every tile after the first (it is only reallocated when it is too small), so the corpora never
  depended on zeros there. The single-tile case hands this buffer to the output image.
- Verification: the buffer was filled with `0x5A5A5A5A` on **every** call of `AllocTileComponentData`, also when it
  was not reallocated (temporary, env switch), and both corpora were decoded: conformance 315/315 and PDF streams
  1404/1404 in the default and single-thread modes. A sample that a decode did not write would have kept the pattern
  and failed parity. No negative control was run (no write was skipped on purpose to see the check fail).
  Then the final code: all six parity modes, the PDF corpus in both thread modes, the stress script and the tests.
- The earlier rejection of an uninitialised **output image** ("Things that did not help") is a different case: an
  image for a tiled decode has areas no tile covers. The tile buffer has none: the whole of it is a tile.

### T1 empty code-block on a fresh decoder (`c437b39`, bug fix)

- `allocate_buffers` and `ht_dec_allocate_buffers` called `Array.Clear(_data, ...)` on a null buffer when a T1
  instance's first code-block was empty (`new_datasize` 0; upstream's `memset` of 0 bytes is fine). That threw
  `ArgumentNullException`. It only happens when a pooled per-thread instance starts on such a block, so it depends
  on thread timing. Parity found it once on `a3_mono.j2c` with `DOTNET_EnableAVX2=0`; it was there before this
  session's changes. The fix skips the clear when the buffer is null. No test reproduces it reliably.

## Things that did not help

| Change | Result |
|---|---|
| Removing bounds checks in the refinement pass (`Unsafe.Add`) | No measurable change. Not kept. |
| Looking for more non-inlined calls in the T1 pass loops (after `b187729`) | None left: the passes only call the tail-row step helpers, after their loops, and throw helpers. |
| Output image allocated with `GC.AllocateUninitializedArray` (no zeroing before the tiles are copied in) | Copy into the image -18% on 10719 + 3167 (26.6 -> 21.9 ms), 0.7% of the round. Not kept: areas no tile covers (corrupt or missing tiles) would hold old memory instead of zeros, as upstream gives; doing it safely needs tracking which tiles were written. (The tile buffer, a different allocation, was done in `37b7a76`.) Poison-checked later: `issue726.j2k` (truncated, mostly uncovered) differs, so the zeros are used; the cost of the zeroing is about 5.5 ms per round of 10719 + 3167 + 4649 (3.5% on all cores, 0.8% on one thread). Still not kept. |
| Hoisting `this._mqc` into a local | Within noise. Kept (harmless). |
| Fewer live registers in the T1 passes (MQ context out of `MqcRegs`) | Spills down 9% (significance) and 17% (refinement), up 6% (cleanup); timings unchanged. Kept as a simplification. The number of live values is not what limits these loops; a call inside them was (`b187729`). |
| T1 column loop with one induction variable (flags index, sample index derived) | The JIT cloned the loop, still spilled, and the passes doubled to 14 KB. No gain. Not kept. |
| T1 lookup tables as `ReadOnlySpan<byte>` constant data with masked indices (no bounds checks) | -0.7% / +2.5% / +0.9% per pass: noise. Not kept. |
| One `&` test instead of two in the 17 significance conditions | Significance pass +3.4%. Not kept. |
| Shorter refinement context, `MAG + ((nb & ~mu) \| (mu << 1))` | -2%, within noise. Not kept. |
| `[MethodImpl(AggressiveOptimization)]` on all hot methods | First decode -4% to -33%, warm decode +9% (loses dynamic PGO). Not kept. |
| Same, DWT/MCT/DC-shift only | First decode -5% to -16%, warm +3%. Not kept. |

`AggressiveOptimization` remains an option if most users decode a few images per process. NativeAOT users get the
same benefit without the warm penalty. It is now kept on plain vector loops that are called too few times to
reach tier-1 code and gain nothing from PGO: the 5/3 DWT kernels (`d48f2d2`, faster warm as well), the DC level
shift (`7aefdb2`) and the 9/7 DWT (`f844085`), each with no warm cost when timed alone. The warm penalty in the two
rows above comes from T1 losing dynamic PGO.

| Change | Result |
|---|---|
| Unchecked `CopyBlockUnaligned` row copy and an inlined `idwt53_h_cas0` dispatch (5/3 horizontal) | 10719 8.49 ms vs 8.27 ms: noise. Not kept. |
| 5/3 AVX2 row kernel: refs stepped instead of indexed, two 256-bit stores (`Permute2x128`) instead of four 128-bit ones | No gain on 10719. Not kept. |
| Offsetting the 5/3 temp buffer to rule out 4K aliasing between loads from the tile and stores to the buffer | No change: not the cause. |
| 5/3 row step: `d0 + 2` computed once for both even vectors (one add fewer) | 10719 -0.6%: noise. Not kept. |
| 5/3 vertical: rows written back in the loop on a 1 KB stride (row `k` one step later, or row `k + 1` in the same step) | 10719 +5% and +7% (4K aliasing). Kept only for strides that are not a multiple of 128. |
| 5/3 vertical: direct stores into the tile (`180fb8e`) on a 1 KB stride as well | 10719 +8%: the rows read in the first half have left L1 by then. Kept only with the in-loop write-back. |
| 5/3 vertical, 1 KB stride: direct stores for only the last 32 rows | 10719 +2.8%. Not kept. |
| 5/3 vertical, 1 KB stride: copy-back of the most recently read rows first | 10719 +1.6%: noise. Not kept. |
| 5/3 vertical: tmp in a newly allocated pinned, aligned buffer per tile | +45% (cold memory on every tile). The cached and pooled versions are in the probes above. |
| T1 jobs claimed from an atomic counter over a collected list instead of `Parallel.ForEach`'s locked partitioner (same thread pool) | T1 123 -> 118 ms on 10719: the partitioner was not the cost. Superseded by the pool. |
| Workers started as `Task.Run` or `ThreadPool.UnsafeQueueUserWorkItem` items instead of dedicated threads | T1 64 -> 109 ms, round 113 -> 160 ms on 10719: the thread pool's one-at-a-time wake-up is the cost (table above). Not kept. |
| Worker threads that spin for up to 2047 iterations before blocking (`ManualResetEventSlim` spin count) | T1 65 -> 52 ms, but the round 125 -> 131 ms: the spinning workers compete with the other stages for cores. A spin count of 100 gave the no-spin numbers. Not kept. |
| Caller waiting for the last jobs with `SpinWait.SpinOnce()` | Falls back to `Sleep(1)`, which lasted 3 to 9 ms per tile (T1 stage 514 to 1347 ms on 10719). The pool's wait never sleeps. |
| 9/7 whole-tile wavelet buffer rented from `ArrayPool` (dirty) instead of `new float[]` per tile | Allocation -0.1 to -0.8 MB per decode (at most 1.7%: 3167 48.5 -> 47.7 MB), time within noise. Parity and tests clean. Not kept: not enough. |
| 9/7 vertical job width rounded up to 16, 32 or 64 columns (as for the 5/3 pass) | 3181 and 1845 level 4 vertical pass unchanged (1.9 to 2.2 ms for every width). Not kept. |
| 9/7 passes split into 2x to 16x more jobs than threads (fast cores take more; wavelet buffers rented inside the job) | 3181 DWT 13.75 ms -> 12.9 (2x, noise), 16.4 (4x), 18.7 (8x), 19.0 (16x); 1845 the same shape. Not kept. |
| Workers spin up to 20 to 200 us after a batch, to meet the next pass hot (`Stopwatch`-bounded) | 3181 DWT -16 to -18%, 10026 up to -27%, round -3 to -7%; 1845 no change; 4649 round +5 to +9%; 10719 inline DWT 14.8 -> 19.7 ms at 200 us. Costs CPU on idle workers when decodes share the pool. Not kept; see the table. |
| Aligning the tile data itself | Not done: `tilec.data` is indexed from 0 everywhere and becomes the image's data for single-tile images, so an offset would leak; retrying pinned allocations wastes memory. The paired sweep gets the same gain on 10719. |

## Missing optimisations: in upstream C, not in the C# port

### Decoder (used by the PdfPig filter)

All implemented since the first version of this document (bit-exact, checked with the modes below):

| # | Upstream | Status |
|---|---|---|
| 1 | `opj_t1_clbl_decode_processor` AVX2/AVX-512: code-block copy-out | Done: `HalveSamples` / `ScaleSamplesToFloatBits` (`Vector<T>`) |
| 2 | `opj_t1_clbl_decode_processor` SSE2: dequantisation for partial decoding | Done, same helpers, in place |
| 3 | `ht_dec.c` `population_count`, `count_leading_zeros` | Done: `BitOperations` (.NET Core 3+) |
| 4 | `opj_idwt53_h_cas0` AVX-512 | Done: `idwt53_h_cas0_512` (cross-platform `Vector512`) |
| 5 | `opj_idwt53_v` mcols AVX-512 | Done: `idwt53_v_cas0/1_mcols512`, 16 columns in one register |
| 6 | `opj_dwt_decode_partial_1_parallel` SSE2 | Done: `Vector128` |
| 7 | NEON | Nothing missing: upstream's NEON code is the 9/7 lifting and the 5/3 vertical pass, both covered by the `Vector<T>` code. (The first version of this list wrongly counted the x86-only transposes, which upstream does not have at all, and `opj_v8dwt_interleave_partial_v`, which has no SIMD; the SSE block next to it is the lifting step.) |
| 8 | `IntOrFloat` unions | Removed where they ran per sample (sparse array, decode_tile_97). The ones left run only on scalar tails of under 8 elements. |

Verification of these paths:

- `PARITY_AREA=1` decodes the centre quarter on both sides (partial path). It found a port bug in
  `SparseArrayInt32.read` (int-to-float conversion instead of a bit copy) that broke area decoding of 9/7 images;
  fixed, now 280/280.
- `PARITY_FORCE_V512=1` forces the 512-bit paths (emulated without AVX-512): 280/280, also with `PARITY_AREA=1`.
- `DOTNET_EnableAVX2=0` exercises the SSE-width `Vector<T>` fallbacks (the code ARM runs): 280/280.
- `input/nonregression/htj2k` (4 files) in all modes: the parity tool did not scan subfolders before.

Effect: three-file warm round 1270 ms at `9ac0cac`, ~1170 ms after the T1 commit, ~985 ms now (stopwatch harness).

### Encoder (not used by the filter)

| # | Upstream | Missing in C# |
|---|---|---|
| 9 | `opj_dwt_encode_step1_combined`, `opj_v8dwt_encode_step1/2`, `opj_dwt_encode_and_deinterleave_v` (SSE/SSE2) | SIMD forward DWT ("Snip SSE" notes in `DWT.cs`) |
| 10 | `opj_mct_encode` (SSE2), `opj_mct_encode_real` (SSE/SSE4.1) | SIMD forward colour transforms |
| 11 | `opj_t1_cblk_encode_processor` AVX2/AVX-512 | SIMD quantisation into the T1 input |

### Not a missing feature

The remaining T1 gap after `b187729` is 1.05x to 1.13x per pass on single thread (about 1.3x at
`8b58ba5`). Its hot decode path now compiles to code close to MSVC's build of upstream (`dumpbin /disasm` of
`t1.obj`), which also still branches on the decoded bit; what is left is spread thinly, with no single cause
found. Spill counts did not predict speed; mispredicted data-dependent branches and a call inside the loops did. After `247af34` the warm C#
9/7 DWT is faster than upstream on all three 9/7 samples (tiled and not). After `d48f2d2` the 5/3 DWT is 1.35x
upstream on the large single tile (10026) and 1.45x on 256x256 tiles (10719). `436da40` removes the per-row call and
range-check cost on 10719 (horizontal -13%). Timed in the same session, the remaining horizontal gap there was
1.31x: the kernel was shuffle-port bound, like upstream's, and spent a fifth of its time copying rows back. After
`df30330` the horizontal pass matches upstream on 10719 and is 4% faster on 10026. After `b793e62` the vertical
pass is within 3% of upstream on 10719 (6.19 ms vs 5.99 ms) and 1.7x faster on 10026; its remaining cost on
256x256 tiles is the copy-back into rows that share few L1 sets (1 KB stride), which upstream pays too. After
`180fb8e` it is faster than upstream on both: 6.26-6.40 ms vs 6.52 ms on 10719, 3.15 ms vs 3.93 ms on 10026.

## Next steps

1. T1: nothing left to find by reading the code (no calls left in the pass loops); next would be a hardware
   profiler (VTune, needs admin for its counters) on the significance and cleanup passes.
2. 5/3 DWT: run BenchmarkDotNet for `df30330`, `b793e62` and `e17678e`. Horizontal: port the `df30330` changes
   to the 512-bit row kernel (needs an AVX-512 machine to time) and, beyond upstream, to a `Vector128` kernel for
   ARM and non-AVX2 x64 (odd-start rows, AVX2 since `51074b6`, would need one too). Vertical: the 512-bit kernels
   still go strip by strip, without the aligned tmp. The copy-back on power-of-two strides measured about 3 ms of
   7.06 ms on 10719 before `180fb8e`; three ways around it failed ("Things that did not help").
3. Single-tile images on all cores (3181: 23 ms per decode after `dd7e282`): the DWT is the largest stage (12.3 ms
   of 27 before the MCT change, 3.3x faster than one thread on 32 threads). The 5/3 vertical pass is fixed
   (`b53fdb3`). The 9/7 passes at the full resolution (two thirds of the 9/7 DWT) plateau at 1.5 to 2 ms from 8 to 16
   threads, 7 to 10 times above the in-cache memory floor. The job trace explains it: the workers start 0.2 ms into
   a 1.1 ms pass on average and equal jobs differ 3 to 5x in speed (efficiency cores, hyper-threads), so a pass lasts
   its last start plus its slowest job. Neither finer jobs nor spinning workers helped overall (see "Things that did
   not help"). What is left is a policy of when to keep workers hot, which nothing tried wins on every image type.
   The 2 to 7 ms per decode that no stage timer covered were the zeroed tile buffer (`37b7a76`); header, tile
   initialisation and the move into the image together are under 0.5 ms. The other large allocations were checked the
   same way: nothing but the output image of tiled decodes is left (it needs its untouched areas zeroed; see the
   poison check above). On tiled images the per-tile structures are the next allocation: 10719 allocates 54.7 MiB of
   `TagNode[]` and 9 MiB of `TcdSeg` per decode, rebuilt for each tile by `InitDecodeTile`; timed: 0.03 ms per tile,
   0.7% to 1.2% of a one-thread round and 2% to 3.6% of an all-cores round, so not worth reusing (see "Per-tile setup
   on tiled images"). The tile-to-image copy is 0.09 ms per tile and too small to split over threads.
   Tiled images (10719: 121 ms per round after `378c0d4`): the serial stages are a fifth of it (T2 14 ms, tile to
   image 13 ms), and T1 is still 66 ms for 6.2 ms x 140 of CPU. Ideas, not tried: overlap the T2 of the next tile
   with the T1 of this one, parallelise the copy into the image, decode whole tiles concurrently (each thread its own
   `TileCoder`: a large change).
4. The pool's design is only tuned on one machine (24 cores, 32 threads, hybrid). Worth timing on a machine with
   fewer cores, and under several concurrent decodes in one process (the stress script checked only correctness).
   The stress script could become a test.
5. First decode: the 9/7 DWT is done (`f844085`). `J2K.UpdateImageData` and `MCTDecode` still never reach tier-1
   code on single-tile images, but cost only 2-3 ms more than warm. What is left is T1, which keeps dynamic PGO.
   The copy into the image (tiled images only) is mostly writing the output: see the uninitialised allocation under
   "Things that did not help".
6. Optional, beyond upstream: ARM (AdvSimd) versions of the 9/7 transposes and of the 5/3 horizontal pass.
7. Decide on `AggressiveOptimization` for the remaining hot methods based on how PdfPig is typically hosted (not
   for T1: it loses the ~8% dynamic PGO gives there).

## How to measure

| What | How |
|---|---|
| Bit-exact parity with C | `tools/parity/parity.cs` (see `tools/README.md`), always with `-c Release` |
| Version-to-version | `./benchmarks/compare-versions.ps1 -Baseline <ref>`; run nothing else meanwhile |
| Managed vs native C | `--filter *NativeComparison*` after `tools/reference/build.ps1 -Simd` |
| NativeAOT, first vs warm decode, allocated MB | `benchmarks/aot` (needs the VS Installer folder on `PATH`); to compare with a release, build it in a worktree of the tag (copy `benchmarks/aot`, `Samples.cs` and `Data` over it) and run both builds alternating |
| Stage times on all cores | `PROBE_MT=1 ./tools/perf/ab.ps1 -Probe tile -NoBaseline` (the timers wrap whole stage calls on the calling thread, so they stay valid with workers) |
| Where time goes | `tools/perf/ab.ps1` (stage timers, A/B vs a baseline commit) and `tools/perf/upstream.ps1` (same stages in C), see `tools/README.md`; `DOTNET_JitDisasm` for generated code |

Lessons from this work:

- `dotnet-trace`'s sampled thread time is safepoint-biased: it attributed 27% of the time to `Array.Clear`, which
  took under 1%. Stopwatch A/B runs and the JIT disassembly were reliable; this profiler was not.
- `DOTNET_JitStdOutFile` appends; delete the file before each run.
- Warm-up depends on round length, not round count: files that decode in a few ms need tens of warm-up rounds
  before tier-1 code runs. Check with `DOTNET_TieredCompilation=0`; if the numbers drop a lot, warm up longer.
  If they rise a lot instead, suspect precompiled framework code, which tiering off never replaces: compare with
  `DOTNET_ReadyToRun=0` as well.
- A method called only a few times per decode (one large tile) can stay in OSR code for the whole run.
  `DOTNET_JitDisasmSummary=1` lists the tier each method reached. It writes one line per compilation
  ("Tier1 with Dynamic PGO", "Tier1-OSR @0x..."): keep the highest tier per method, and look at the hot ones that
  never reach "Tier1". Code called once per tile or per component is the usual case.
- Measure the first decode too (`ab.ps1 -Warm 0 -Rounds 1`): PdfPig often decodes a few images per process, and
  tier-0 code there can cost more than the warm difference (DC level shift: -6% warm, -42% first decode).
- Merging two passes over the same data (MCT and DC level shift) saves a full read and write when the data does
  not fit in the cache, even when each pass is already vectorised.
- Time whole loops, not each call inside them: a timer pair per row call cost several ms on 10719.
- Compare stage by stage against upstream timed the same way. On the tiled files the copy stages were already faster
  than C; the gap was all in the lifting, which totals alone did not show.
- File-based apps (`dotnet run x.cs`) build referenced projects in Debug unless given `-c Release`.
- BenchmarkDotNet finds the benchmark project from the working directory: run each checkout from its own folder,
  and keep comparison worktrees outside the repository.
- BenchmarkDotNet 0.15.8 cannot build NativeAOT jobs with the .NET 10 SDK here (`ArtifactsPath` layout).
- Upstream's `broken.jpc` decodes to more than 2 GB; `opjdump` now refuses dumps above 1 GB.
- Counting stack references in `DOTNET_JitDisasm` output is a poor proxy for speed. Look for conditional jumps on
  decoded data instead, and confirm with per-pass timings against upstream timed the same way.
- A rare, non-inlined call inside a hot loop costs on every iteration, not only when taken: the JIT keeps values
  live across the call in stack slots for the whole loop. In the disassembly, look for loop variables loaded from
  `[rbp-..]` / `[rsp+..]`, then for a `call` in the loop body.
- The JIT turns `cond ? 1 : 0` into `setcc` but does not always if-convert `x = cond ? c : x`; write the
  select as arithmetic on a 0/1 value when the condition depends on decoded data.
- With machine drift of ±5-10% between sessions, compare a baseline worktree and the change in alternating runs
  and keep each side's minimum; A/A runs show the noise floor.
- To read upstream's compiled code, run `dumpbin /disasm` from PowerShell (Git Bash rewrites `/nologo` as a path).
- Count shuffle uops (`vpermd`, `vpbroadcastd`, `vpunpck*`, `vextracti128`, `vpextrd`) in SIMD kernels: x86 cores run
  them on one port. Recomputing a shifted vector from an extra load and three arithmetic ops can beat shifting it.
- To find what a kernel's time is spent on, time a deliberately wrong build that skips one part (for example the
  copy-back), against the baseline in alternating runs.
- With Visual Studio and ReSharper open on the repo, the probes' temporary edits of a source file can fail with
  "permission denied", and background analysis can slow a run. Use alternating A/B runs and check the untouched
  stage (here the vertical pass) as a control.
- Column passes on power-of-two strides: a 1 KB stride maps a strip's rows to 4 of the 64 L1 sets, so a tall strip
  does not stay in L1, and rows 4 apart share address bits 0-11, so stores to one row delay loads from the others
  (4K aliasing). Interleaving stores with the loads of the same pass helped on other strides and hurt on these.
- .NET arrays are 8-byte aligned. 32-byte vector rows of a tile then often straddle cache lines; process wider
  blocks so the straddled lines are fetched once, rather than relying on alignment.
- Time the stages on all cores too, not only on one thread: the DWT was 7 to 10 times slower threaded than inline on
  small tiles, and only the all-cores stage probe showed it. A stage that gets faster can make the next one slower
  (T1 89 -> 120 ms when the DWT stopped waking the pool).
- A fork/join per tile only pays for work much larger than its wake-up cost. Count the threads that actually run a
  job and when each one starts (temporary counters), not only the wall time: here 11 of 32 threads ran jobs, 0.4 ms
  in, which pointed at the wake-up chain and not at the partitioner or the job size.
- The .NET thread pool wakes one worker per request and the woken worker requests the next, so a burst of short
  jobs reaches few threads in time; `Task.Run` and `QueueUserWorkItem` do the same as `Parallel.ForEach`. Dedicated
  threads that the caller signals directly start in 0.17 ms on average.
- `SpinWait.SpinOnce()` ends in `Sleep(1)`, which on Windows can last a whole timer tick (3 to 9 ms here). Wait for
  short jobs with `Thread.SpinWait` and `Thread.Yield`. `SpinOnce(int)` does not exist on netstandard or net46x.
- Idle workers that spin keep their cores and slow the stages that run in between, on a machine with efficiency
  cores and hyper-threads: a stage can speed up (T1 -21%) while the whole round slows down. Judge by the round.
- Look at the single-tile images on all cores as well as the tiled one: once the DWT and T1 were fixed, the loops
  that had never been threaded (MCT, DC level shift) were 15% of the decode. A per-sample loop is the easy case:
  split it by range and the output cannot change.
- Time every pass of the DWT per level, on one thread and on all cores, and sweep the thread count: a pass that
  gets slower with more threads (5/3 vertical, 8 -> 32 threads: 1.2 -> 2.3 ms) is sharing cache lines or another
  resource, and no amount of kernel tuning shows it. Splitting work by column needs job widths in whole cache lines.
- To see why a threaded pass is slow, log every job's start, end and thread against the call: here job creation and
  the join were free, the workers started up to 0.36 ms late, and jobs of equal size differed 3 to 5x in duration.
  That ruled out overhead and bandwidth in one run and pointed at the hardware (efficiency cores, hyper-threads).
- Work that is already fast per job does not get faster with smaller jobs on a hybrid CPU: 4x to 16x more jobs than
  threads made the 9/7 DWT 20 to 40% slower. And spinning workers help a stage that follows its own batches and hurt
  every stage that does not: judge by the round of the small and tiled images too, not by the stage that spins.
- A script that fails to apply an edit leaves the code unchanged and the experiment looking like a flat result:
  check the edit applied (and that a changed setting changes something) before reading a sweep.
- Before blaming memory bandwidth, measure the floor: a plain in-place pass over the same plane took 0.22 ms where
  the real passes took 1.6 to 2.3, because the plane fits the L3 cache.
- When the default threshold hides a split from the corpora, run parity once with a tiny threshold (here 64
  samples): every image then takes the split path, with odd sizes and boundaries.
- Time the phases around the stage timers once the stages are fast: the probe's stages added up to 20 of 25 ms on
  3181, and the rest was one zeroing allocation (88 MB), not the setup code anyone would suspect.
- Before dropping a zero-initialisation, poison the buffer instead of reasoning about it: fill it with a pattern on
  every allocation (also on reuse) and run parity on every corpus. Untouched samples then fail parity instead of
  hiding as zeros. A buffer that is already reused dirty elsewhere in the code (here every tile after the first) is
  a good sign that nothing depends on zeros.
- The poison check can say no as well: for the output image it found a corpus file (`issue726.j2k`) that depends on
  the zeros, which reasoning about "every tile writes its area" had missed. Run it before every such change, and keep
  the failing file as the regression test.
- To see what allocates, attach an `EventListener` to the runtime's `GCAllocationTick` events and sum per type; check
  its total against `GC.GetTotalAllocatedBytes` first. Numbers from a NativeAOT harness built on a newer runtime
  (stack allocation of small objects) differ from the JIT's: compare like with like.
- An allocation total is not a time cost: 54.7 MiB of tag trees per decode (30% of 10719's allocation) cost 4 ms,
  under 4% of the round. Time the code that allocates before restructuring it; small gen-0 objects are cheap.
- Parity decodes one file at a time and cannot see threading faults: check concurrent decodes, an idle timeout and
  respawn, and failing jobs separately.
- Probe scripts that patch source by exact text break when that code changes; match the first and last line, and
  restore every patched file when a step fails.
