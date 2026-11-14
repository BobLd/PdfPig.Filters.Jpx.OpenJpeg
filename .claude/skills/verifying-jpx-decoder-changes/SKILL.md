---
name: verifying-jpx-decoder-changes
description: Use when changing any code under UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG (bug fixes, performance work, SIMD/vectorisation, memory pooling, upstream syncs), or when a JPX/JPEG 2000 image decodes wrongly, crashes, or looks different from other PDF viewers.
---

# Verify JPX decoder changes

## Overview
The decoder must stay **bit-identical** to upstream C OpenJPEG. That includes SIMD code: upstream's SSE2/AVX2 build
gives exactly the same samples as its scalar build, so tolerance-based comparison is never acceptable.
Speed claims on whole decodes and memory use BenchmarkDotNet. Single stopwatch runs prove nothing; per-stage
timers in alternating A/B runs are for finding and sizing a change (REQUIRED for speed work:
optimising-jpx-decoder-performance).

## Quick reference (from repo root, PowerShell)
| Goal | Command |
|---|---|
| Build C reference (scalar) | `./tools/reference/build.ps1` |
| Build C reference (SIMD, for benchmarks) | `./tools/reference/build.ps1 -Simd` |
| Parity on a folder of .j2k/.jp2/.jpx | `dotnet run -c Release tools/parity/parity.cs -- compare .reference/build/bin/opjdump.exe out.csv <dir>...` |
| Debug one file (full stack trace) | `dotnet run -c Release tools/parity/parity.cs -- one <file>` |
| Unit tests (includes bit-exact check vs C hashes) | `dotnet test -c Release -f net8.0` |
| Managed vs native C speed | `dotnet run -c Release --project UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks -f net8.0 -- --filter *NativeComparison*` |
| Before/after a change | `./benchmarks/compare-versions.ps1 -Baseline master` |
| NativeAOT, cold vs warm | `benchmarks/aot` harness, see `tools/README.md` (needs the VS Installer folder on PATH) |

## Corpora
- OpenJPEG conformance + non-regression files: `git clone --depth 1 --filter=blob:none --sparse https://github.com/uclouvain/openjpeg-data`
  then `git sparse-checkout set input/conformance input/nonregression` (about 200 MB, many deliberately corrupt files).
- PDF streams: `dotnet run -c Release tools/parity/parity.cs -- extract UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/Documents <outDir>`.

Run parity on both before calling a change done. Expected: no `MISMATCH` or `CS_FAIL` (exit code 0).
Also run it with `PARITY_THREADS=1`, `PARITY_AREA=1` (partial/area decoding), `PARITY_FORCE_V512=1` (512-bit paths,
emulated without AVX-512), `DOTNET_EnableAVX2=0` (the narrower `Vector<T>` fallbacks ARM uses) and
`DOTNET_EnableHWIntrinsic=0` (scalar) when touching the paths they cover. `opjdump` must be rebuilt
(`tools/reference/build.ps1`) for `PARITY_AREA`.
`BOTH_FAIL` is fine; `C_FAIL_CS_OK` means C# is more lenient, so look at it but it is not a failure.

Unit tests: the expected result is 289/294 on net8.0 and net472 (as of `3f2e2e6`; new tests raise both counts). The 5 failures are files upstream C rejects too
(GHOSTSCRIPT-691843-0, MOZILLA-LINK-10026-0, PDFBOX-2204-0, PDFBOX-2292-0, PDFBOX-3037-0). Read the counts with
`grep -E '^\s*(failed|succeeded|total):'`; the "Failed!" summary line alone says nothing.

A new branch is only covered if a corpus file reaches it. For new or rare paths, add a temporary static counter per
branch, decode both corpora, and check every count is non-zero before trusting parity.

## Common mistakes
- Running two parity jobs at once on an old copy of the tool: they shared a temp file. Current tool uses one per process.
- Benchmarking while another CPU-heavy job runs: results are meaningless. Run benchmarks alone. Visual Studio with
  ReSharper open on the repo reanalyses edited files: expect noisy runs and "permission denied" on temporary edits.
- One long background job for all modes: on low memory it can be killed part-way. Run the parity modes and the two
  test frameworks as separate foreground steps.
- Writing off a single `CS_FAIL` in one mode as a flake: `a3_mono.j2c` failed once with `DOTNET_EnableAVX2=0` from a
  thread-timing bug that was already there (pooled T1 instance, empty first code-block, `c437b39`). Get the stack
  with `parity.cs -- one <file>` in that mode, and run the file several times.
- Using MSVC flags only: MSVC never defines `__SSE__`/`__SSE2__`. `build.ps1 -Simd` adds them; without them the C
  build silently takes scalar paths.
- Comparing colour spaces numerically: C# `COLOR_SPACE` uses JP2 `enumcs` values; the parity tool maps them.
- Fused multiply-add (`Fma`, `MathF.FusedMultiplyAdd`) in the 9/7 DWT or ICT: it changes rounding and breaks parity.
  Use separate multiply and add, in the same order as the C code.
