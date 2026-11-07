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

# One file, with a full stack trace on failure
dotnet run -c Release tools/parity/parity.cs -- one <file>
```

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
