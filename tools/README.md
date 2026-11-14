# Tools

Developer tools for keeping the C# port in line with upstream C OpenJPEG. None of this ships in the package.

## reference/ — upstream C build

`build.ps1` clones https://github.com/uclouvain/openjpeg into `.reference/openjpeg` at the commit recorded in
`UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG/UPSTREAM.md`, and builds:

- `opjdump` — decodes a file with the C library and writes the raw component samples (format in `opjdump.c`).
- `openjp2` shared library — P/Invoked by the native benchmarks.

```powershell
./tools/reference/build.ps1                 # scalar C build  -> .reference/build/bin
./tools/reference/build.ps1 -Simd           # SSE2/AVX2 build -> .reference/build-simd/bin
./tools/reference/build.ps1 -Commit master  # another upstream commit
```

Needs CMake and a C compiler (Visual Studio "Desktop development with C++" on Windows).

## parity/ — bit-exact comparison

```powershell
# Every file in the folders, C vs C#. Exit code = number of MISMATCH + CS_FAIL files.
dotnet run -c Release tools/parity/parity.cs -- compare .reference/build/bin/opjdump.exe parity.csv <dir> [<dir>...]

# JPX streams from PDFs (optional cap on distinct streams per PDF)
dotnet run -c Release tools/parity/parity.cs -- extract <pdfDir> <outDir> [maxPerPdf]

# Environment switches for compare: PARITY_THREADS=1 (inline single-threaded path), PARITY_AREA=1 (decode the
# centre quarter: partial-decoding paths), PARITY_FORCE_V512=1 (512-bit SIMD paths, emulated without AVX-512).
# DOTNET_EnableAVX2=0 / DOTNET_EnableHWIntrinsic=0 select the narrower or scalar fallbacks. Folders are scanned recursively.

# One file, with a full stack trace on failure
dotnet run -c Release tools/parity/parity.cs -- one <file>
```

## perf/ — stage timings, A/B and against upstream

For finding and sizing a speed change; whole-decode claims still go through BenchmarkDotNet
(`benchmarks/compare-versions.ps1`). Run nothing else meanwhile.

```powershell
# Baseline commit vs working tree, alternating runs, minimum per stage (probe: tile | t1 | dwt53 | dwt97)
./tools/perf/ab.ps1 -Probe dwt53 -Files MOZILLA-10719-0_3 [-Baseline <ref>] [-Runs 6] [-Warm 5] [-Rounds 10]
./tools/perf/ab.ps1 -Probe t1                       # default: the 10719 + 3181 + 3167 round

# The same stages in upstream C (patched copy of .reference/openjpeg, AVX2 build), for the same files
./tools/perf/ab.ps1 -Probe dwt53 -Files MOZILLA-10719-0_3 -NoBaseline
./tools/perf/upstream.ps1 -Files MOZILLA-10719-0_3
```

- `instrument.cs` patches `Stopwatch` timers into a source tree (`apply <root> <probe>`, `restore <root>`) by
  matching whole source lines. If the timed code changed, it stops with the line it could not find: update the
  anchor table at its top.
- `probe.cs` decodes the files single-threaded for warm-up and measured rounds and prints `perf <stage> <ms>`.
- `ab.ps1` puts the baseline in a git worktree under `%TEMP%/jpx-perf/base` (kept for the next run), instruments
  and builds both sides, and restores both source trees afterwards, also on failure.
- `upstream.ps1` applies `upstream-timers.patch` (`timespec_get` timers, printed at exit) to a worktree of
  `.reference/openjpeg` and reuses the build until the commit or the patch changes. Each file is decoded once per
  process, so C has no warm-up to discard.

## Regenerating the test reference table

`UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/Reference/jpx-reference.tsv` holds, for the first 20 distinct JPX streams
of every test PDF, the MD5 of the stream and the MD5 of the C decoder's output. `ReferenceParityTest` checks the
C# decoder against it. Regenerate it after adding a test PDF or syncing upstream:

```powershell
./tools/reference/build.ps1
dotnet run -c Release tools/parity/parity.cs -- extract UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/Documents .reference/testjpx 20
dotnet run -c Release tools/parity/parity.cs -- hashes .reference/build/bin/opjdump.exe UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Tests/Reference/jpx-reference.tsv .reference/testjpx
```

## Benchmarks

See `UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks/Program.cs` and `benchmarks/compare-versions.ps1`.

### NativeAOT

BenchmarkDotNet 0.15.8 fails to build NativeAOT jobs with the .NET 10 SDK, so `benchmarks/aot` is a small stopwatch
harness over the same samples. It prints the first decode (cold) and the median of the rest (warm), in ms and in
allocated MB per decode:

```powershell
# vcvarsall.bat in VS 2026 (18) calls vswhere.exe through PATH; without this the native link step fails
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
dotnet publish benchmarks/aot -c Release -r win-x64 -o .reference/aot
.reference/aot/AotDecode.exe 10
dotnet run -c Release --project benchmarks/aot -- 10   # same harness under the JIT, for comparison
```
