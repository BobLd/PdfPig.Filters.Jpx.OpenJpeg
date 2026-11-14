# Upstream tracking

This folder is a C# port of the OpenJPEG `openjp2` library: https://github.com/uclouvain/openjpeg
It started as a copy of https://github.com/notBald/OpenJPEG.net (via the BobLd fork) and is now maintained here.

| | |
|---|---|
| Upstream repo | https://github.com/uclouvain/openjpeg |
| Synced to commit | `8314119b067c0fc77834731168daaebd379fdb12` (2026-09-05, after v2.5.4) |
| Library path | `src/lib/openjp2` |

Update the commit above every time upstream changes are ported. See `.claude/skills/sync-openjpeg-upstream/SKILL.md`
for the procedure, and `tools/` for the reference decoder and parity checker.

## File map (C -> C#)

| upstream `src/lib/openjp2` | here |
|---|---|
| `j2k.c` | `J2K.cs`, `CodingParameters.cs` |
| `jp2.c` | `JP2.cs` |
| `openjpeg.c` | `CompressionInfo.cs` |
| `cio.c` | `CIO.cs` |
| `tcd.c` | `Internal/TileCoder.cs`, `Internal/TcdImage.cs` |
| `t1.c` | `Internal/Tier1Coding.cs` |
| `ht_dec.c` | `Internal/ht_dec.cs` |
| `t2.c` | `Internal/Tier2Coding.cs` |
| `pi.c` | `Internal/PacketIterator.cs` |
| `dwt.c` | `Internal/DWT.cs` |
| `mct.c` | `Internal/MCT.cs` |
| `mqc.c`, `mqc_inl.h` | `Internal/MQCoder.cs` |
| `bio.c` | `Internal/BIO.cs` |
| `tgt.c` | `Internal/TagTree.cs` |
| `sparse_array.c` | `Internal/SparseArray.cs` |
| `invert.c` | `Internal/Invert.cs` |
| `t1_luts.h`, `t1_ht_luts.h` | `Internal/T1Luts.cs`, `Internal/T1HTLuts.cs` |
| `image.c` | `JPXImage.cs`, `Internal/ImageComp.cs` |

Each ported function has a `<remarks>` or comment tag such as `2.5.3 - opj_j2k_read_sot`, naming the upstream
version it was last checked against and the C function name. Search for the C name to find the C# code.

## Deliberate differences

- `COLOR_SPACE` keeps the raw JP2 `enumcs` values (sRGB = 16, ...) instead of `OPJ_CLRSPC_*`, and also keeps
  CMY, YCCK and CIELab, which upstream reports as `UNKNOWN`.
- `DecompressionParameters.IgnoreColorLookupTable` (PdfPig) skips palette application but keeps the palette
  on the image, so `JPXImage.ApplyIndex()` can apply it later.
- Managed code only. No native interop in the library.
- `opj_thread.c` is not ported. `Internal/Parallelism.cs` stands in for the thread pool: T1 code-block jobs and
  DWT row/column jobs run through `Parallel.For`/`Parallel.ForEach` (calling thread included), and exceptions
  come back to the caller. `DecompressionParameters.DisableMultiThreading` runs everything inline.
- T1 contexts are cached per thread (`Tier1Coding.Rent/Return`), like upstream's per-thread `opj_t1_t`.
  DWT job buffers come from `ArrayPool`.
- Upstream's compile-time variants that its default build leaves out are not ported: `TWO_PASS_VERSION` and
  `STANDARD_SLOW_VERSION` (DWT, documentation only), `ENABLE_EMPTY_PACKET_OPTIMIZATION` (T2 encoder) and the
  `dump_state` of `opj_dump` (`DUMP_FLAG` has no effect). When syncing, skip changes inside those blocks.

## SIMD

Upstream's SSE2/AVX2/NEON code is ported with `System.Numerics.Vector<T>`, so one code path covers x64 and ARM.
Every vector path does the same operations in the same order as the scalar code, so output stays bit-identical.
Upstream's own SIMD build gives the same samples as its scalar build, and `tools/parity` checks this for C#.

| upstream | C# | targets |
|---|---|---|
| `opj_v8dwt_decode_step1/2` SSE/AVX (9/7) | `DWT.v8dwt_decode_step1/2` | all |
| `opj_idwt53_v_cas0/1_mcols_SIMD` (5/3 vertical) | `DWT.idwt53_v_cas0/1_mcols` | .NET 7+ (needs `Vector<int>` shifts) |
| `opj_mct_decode` SSE2/AVX2 (RCT) | `MCT.Decode` | .NET 7+ |
| `opj_mct_decode_real` SSE (ICT) | `MCT.DecodeReal` | all |
| `opj_tcd_dc_level_shift_decode` | `TileCoder.DcShiftRowInt/Float` (upstream is scalar) | all |
| `opj_idwt53_h_cas0` AVX2 / AVX-512 | `DWT.idwt53_h_cas0_avx2` / `_512` | .NET 8, AVX2 / 512-bit |
| `opj_idwt53_v_*_mcols_SIMD` AVX-512 | `DWT.idwt53_v_cas0/1_mcols512` | .NET 8, 512-bit |
| `opj_t1_clbl_decode_processor` SSE2/AVX2/AVX-512 | `Tier1Coding.HalveSamples` / `ScaleSamplesToFloatBits` | all (5/3: .NET 7+) |
| `opj_dwt_decode_partial_1_parallel` SSE2 | `DWT.decode_partial_1_parallel` (`Vector128`) | .NET 7+ |
| `ht_dec.c` popcount / clz builtins | `ht_dec.population_count` / `count_leading_zeros` (`BitOperations`) | .NET Core 3+ |

`Simd.Use512` selects the 512-bit paths. The AppContext switch `OpenJpeg.ForceVector512` (or `PARITY_FORCE_V512=1` in
`tools/parity`) turns them on without AVX-512 hardware, where .NET emulates them, so they can be verified anywhere.

Not vectorised yet: encoder paths. Beyond upstream: AVX 8x8 transposes in the 9/7 horizontal pass (x86 only).
Never use fused multiply-add here: it changes rounding.

## Upstream changes ported after 2.5.3

All changes of v2.5.4 under `src/lib/openjp2` are ported (`f809b80c`, `578c67b3`; the rest of that range is
documentation and packaging only), and `CompressionInfo.Version` reports `2.5.4`. The list also covers
the commits after the v2.5.4 tag up to the synced commit.

- `91966314` MCO marker: keep `m_dc_level_shift` when the number of stages is zero
- `1bb9e915` interleaved tile-parts: restart from the main header end, not the last SOT (#1558)
- `91d08b11` `opj_j2k_read_sod`: bounds check before writing the tile-part index
- `f809b80c` `opj_jp2_read_header`: set the colour space only if the header was read
- `578c67b3` encoder: copy the ICC profile into the colour box (C# threw a NullReferenceException)
- `839936aa` `opj_pi_initialise_encode`: overflow check on the `include` allocation

## Port bugs fixed by parity testing against the C library

- `J2K.Create` called `DecoderSetStrictMode(true)`, which also turned off the TPsot==TNsot correction.
  Files with one tile-part too many (`TPsot == TNsot`) decoded wrongly.
- `opj_j2k_need_nb_tile_parts_correction`: stream position not restored on early exit.
- `opj_j2k_read_ppt`: a later PPT marker with a lower `Zppt` shrank the marker array, which crashed
  conformance file `g4_colr.j2c`.
- `JPXImage.color_space` defaulted to `BILEVEL` (0) instead of `UNSPECIFIED` for raw codestreams.
- Thread-pool work items had no exception handling: an exception while decoding a corrupt file on a worker
  thread terminated the host process.
- `DisableMultiThreading` still sent every code-block to the thread pool and waited on an event per job.
- `SparseArrayInt32.read` (float overload, strided path) converted sample values from int to float instead of copying
  their bits, so decoding a sub-area (`SetDecodeArea`) of a 9/7 image gave garbage. Found with `PARITY_AREA=1`.
