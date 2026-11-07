---
name: verifying-jpx-decoder-changes
description: Use when changing any code under UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG (bug fixes, performance work, SIMD/vectorisation, memory pooling, upstream syncs), or when a JPX/JPEG 2000 image decodes wrongly, crashes, or looks different from other PDF viewers.
---

# Verify JPX decoder changes

## Overview
The decoder must stay **bit-identical** to upstream C OpenJPEG. That includes SIMD code: upstream's SSE2/AVX2 build
gives exactly the same samples as its scalar build, so tolerance-based comparison is never acceptable.
Speed and memory are measured with BenchmarkDotNet, never with stopwatch runs.

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

## Corpora
- OpenJPEG conformance + non-regression files: `git clone --depth 1 --filter=blob:none --sparse https://github.com/uclouvain/openjpeg-data`
  then `git sparse-checkout set input/conformance input/nonregression` (about 200 MB, many deliberately corrupt files).
- PDF streams: `dotnet run -c Release tools/parity/parity.cs -- extract UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/Documents <outDir>`.

Run parity on both before calling a change done. Expected: no `MISMATCH` or `CS_FAIL` (exit code 0).
`BOTH_FAIL` is fine; `C_FAIL_CS_OK` means C# is more lenient, so look at it but it is not a failure.

## Common mistakes
- Running two parity jobs at once on an old copy of the tool: they shared a temp file. Current tool uses one per process.
- Benchmarking while another CPU-heavy job runs: results are meaningless. Run benchmarks alone.
- Using MSVC flags only: MSVC never defines `__SSE__`/`__SSE2__`. `build.ps1 -Simd` adds them; without them the C
  build silently takes scalar paths.
- Comparing colour spaces numerically: C# `COLOR_SPACE` uses JP2 `enumcs` values; the parity tool maps them.
- Fused multiply-add (`Fma`, `MathF.FusedMultiplyAdd`) in the 9/7 DWT or ICT: it changes rounding and breaks parity.
  Use separate multiply and add, in the same order as the C code.
