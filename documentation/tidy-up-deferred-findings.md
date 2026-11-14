# Deferred findings

Performance or bug observations made during the tidy-up phase. Not fixed there (the tidy-up changes form only);
each needs its own change, verification (`.claude/skills/verifying-jpx-decoder-changes`) and commit.
Progress of the tidy-up itself: [`tidy-up-progress.md`](tidy-up-progress.md).

## `Internal/BIO.cs`

Only read, not tested. Check against upstream `bio.c` before acting.

| # | Kind | Finding |
|---|---|---|
| 1 | Possible bug | `BIO.ByteAlign`: when the buffer holds `0xff` and `_data_pos == _end_pos` already, `_data_pos++` moves past the end and the `== _end_pos` test never fires, so it returns `false` and `Position` is one too large. Upstream `opj_bio_inalign` returns `FALSE` when `bp >= end`. |
| 2 | Inconsistency | `BIO.ReadBool` returns `false` at the end of the data (as upstream), while `Read` throws `EndOfStreamException` unless `zeroAtEnd`. Same input, two behaviours. |
| 3 | Possible bug | `BIO.Read` does not check `n` (documented max 8). With `n > 8`, `8 - n` is negative and the shift is wrong without any error. `ReadUInt` is the only guarded entry. |
| 4 | Performance | `WBIO.Write` writes one bit at a time through `WriteBit` (encoder only; no effect on decoding). |
| 5 | Performance | `BIO` is a class allocated per packet header (`Tier2Coding.cs:575`); a `ref struct` or a pooled instance would avoid the allocation. Not measured. |

## Array creations that could be `stackalloc` (review, nothing changed)

Review of every array creation in `OpenJPEG/` (180 `new T[...]` expressions, found with a Roslyn pass over the whole folder).
Not measured: these are candidates, each needs an A/B benchmark and the usual parity run.

What decides it:

- the element type must be unmanaged (`byte`, `int`, `uint`, `float`, `double`, `bool`, `IntOrFloat`, ...); 15 of the 68 local
  creations hold references (`ImageComp[]`, `TagNode[]`, `int[][]` ...) and cannot be `stackalloc`;
- the array must not escape: no field, return value, lambda, iterator or `async` method. 112 of the 180 are not plain locals
  (fields, arguments, assignments to fields such as `tcp.rates`, `flags`, `cblkdatabuffer`) and are out;
- the size must be small and bounded. `stackalloc` of a variable size needs a guard
  (`n <= 128 ? stackalloc T[n] : new T[n]`) and never goes inside a loop (the stack space is only given back at method exit);
- every callee that takes the array needs a `Span<T>` parameter (all targets have `Span<T>` through `System.Memory`; nothing in the
  repository uses `stackalloc` or `SkipLocalsInit` yet, and `stackalloc` zeroes like `new`, so behaviour is the same).

### Worth doing (decode path, small, non-escaping)

| # | Where | Now | Change |
|---|---|---|---|
| 1 | `Tier1Coding.DecodeHTCblk`, `U_q` (lines near 1896 and 2231) | `uint[] U_q = new uint[2];` **inside the quad-pair loop**: one 24-byte array per iteration, about 500 per 64x64 code block of an HTJ2K stream | Declare once before the loops (`Span<uint> U_q = stackalloc uint[2];`) and make `ht_dec.decode_init_uvlc` / `decode_noninit_uvlc` take `Span<uint> u`. HTJ2K only | **Applied and measured, see below.**
| 2 | `Tier1Coding.DecodeHTCblk`, `qinf` (line near 1654) | `new uint[2]`, once per HT code block, indexed only | `Span<uint> qinf = stackalloc uint[2];` |
| 3 | `TagTree.Create` and `TagTree.Init`, `nplh` / `nplv` | four `new int[32]` (128 bytes each) per tag tree built, i.e. per precinct and band of every tile; indexed only | `Span<int> nplh = stackalloc int[32];` (and `nplv`) |
| 4 | `Tier2Coding.DecodePackets`, `first_pass_failed` | `new bool[_image.numcomps]` inside the `pino` loop, indexed and `.Length` only | Hoist out of the loop and use `numcomps <= 128 ? stackalloc bool[n] : new bool[n]` (per tile; small gain) |
| 5 | `PacketIterator.CreateDecode` and `InitialiseEncode`, `tmp_data`, `tmp_ptr` | `new uint[numcomps * 132]` (about 1.6 KB for RGB) and `new uint[numcomps]`, per tile; passed to `GetAllEncodingParameters(uint[], ref uint[])` | `Span<uint>` parameters (the `ref` is not needed) and a guarded `stackalloc`. Per tile only; small gain |
| 6 | `MCT.DecodeCustom`, `current_data`, `current_result` | two `new IntOrFloat[n_comps]` per call (custom MCT only), indexed only | Guarded `stackalloc IntOrFloat[n_comps]` (`IntOrFloat` is an unmanaged explicit-layout struct) |

### Possible, encoder side or tiny gain

- `J2K.WritePLT`: `byte[5]` (`var_bytes`), indexed only, outside its loop; `TileCoder.Rateallocate`: `double[100]` (`cumdisto`), indexed only;
  `MCT.EncodeCustom` (`int[NMatCoeff]`, `int[data.Length]`); `Invert.MatrixInversion` (four arrays of `n_compo` floats / ints, passed to
  `lupDecompose` / `lupSolve` / `lupInvert`, which would need `Span` parameters); `J2K.SetupEncoder` / `SetupMCTencoding` (`float[n]` for the
  MCT matrix). All run once per image or tile in the encoder.

### Not candidates

- Large or unbounded buffers: the `sycc*_to_rgb` lookup tables (`int[max]`, up to 64 K entries each, 9 of them), `DWT.EncodeProcedure`
  (`int[data_size]`), `Scaler.Rezise` and `ShortOrIntOrFloatOrDoubleAr.ToArray` (returned), `J2K.ReadMCT` / `MergePPM` / `WriteSOD`
  (header-sized buffers passed to `CIO.Read(byte[])` and similar), `J2K.CheckPOCval` (`step_l * num_layers`), `CIO.Memcopymove`.
- Arrays that are kept: everything in `JP2.ReadCMAP` / `ReadPCLR` / `ReadCDEF` / `ReadCOLR` (stored in the colour description),
  `PacketIterator.Create*` results, `MQCState.BuildTable` (returned, static), the `Range[]` of `Parallelism.RunRanges`
  (passed as `IReadOnlyList<T>`), and the 15 field initialisers.

### Related, not `stackalloc` (reference-typed arrays)

- `TagTree.Decode` and `TagTree.Encode` allocate `var stk = new TagNode[31];` (about 270 bytes) **per call**, and `Decode` runs for every
  code block of every packet header. A reference array cannot be `stackalloc`'d; reuse one array per `TagTree` (a tree is decoded by
  one thread at a time) or use a `[InlineArray(31)]` struct (net8.0 only; the other targets would keep the array).
- `CIO.Memcopymove` copies through a temporary `byte[_pos - count]` before `Buffer.BlockCopy`; `Buffer.BlockCopy` already handles
  overlapping ranges, so the temporary may not be needed at all (check the callers before removing it).

### Item 1 (`U_q`) applied and measured

Change: `Span<uint> U_q = stackalloc uint[2];` once at the top of `Tier1Coding.DecodeHTCblk` (next to `qinf`), the two
`new uint[2]` inside the quad-pair loops removed, and `ht_dec.decode_init_uvlc` / `decode_noninit_uvlc` take `Span<uint> u`.
Both decoders write `u[0]` and `u[1]` in every branch, so reusing the two slots cannot carry a stale value from one iteration to the next.

Benchmark (`DecodeBenchmarks`, net8.0, `short` job with 15 iterations, HEAD against HEAD plus the change in two short-path checkouts,
3 alternating rounds, the two HTJ2K conformance files `Bretagne1_ht` and `Bretagne1_ht_lossy` as samples; the PDF samples of the
regular benchmark set do not use the HT path):

| Sample | Threads | Time base -> new (3 rounds) | Allocated base -> new |
|---|---|---|---|
| `Bretagne1_ht` (lossless) | multi | 2.03 -> 1.79, 2.10 -> 1.83, 2.14 -> 1.78 ms (-12%, -13%, -17%) | 8.7 -> 5.1 MB (-40.6%) |
| `Bretagne1_ht` (lossless) | single | 14.16 -> 13.71, 15.61 -> 13.99, 14.30 -> 13.72 ms (-3%, -10%, -4%) | 8.6 -> 5.1 MB (-40.7%) |
| `Bretagne1_ht_lossy` | multi | 3.20 -> 2.85, 3.01 -> 2.89, 2.95 -> 2.82 ms (-11%, -4%, -4%) | 8.4 -> 5.0 MB (-41.3%) |
| `Bretagne1_ht_lossy` | single | 11.04 -> 10.80, 11.95 -> 10.99, 11.28 -> 11.01 ms (-2%, -8%, -2%) | 8.4 -> 4.9 MB (-41.3%) |

Faster in all 12 comparisons, allocations down by 41% (about 3.5 MB per decode). Checks: parity 315/315 in the 6 modes (the HTJ2K files
`Bretagne1_ht`, `Bretagne1_ht_lossy`, `byte.jph`, `byte_causal.jhc` match upstream C) and 1 404/1 404 on the PDF streams in both thread modes.

### Item 2 (`qinf`) applied and measured: no gain, kept at the repo owner's request

Change: `uint[] qinf = new uint[2];` is `Span<uint> qinf = stackalloc uint[2];` in `Tier1Coding.DecodeHTCblk` (one array per HT code block, indexed
only, never passed on).

Benchmark: same setup as item 1 (`DecodeBenchmarks`, `short` job, 15 iterations, HEAD, which already has item 1, against HEAD plus the
change, short-path checkouts, 4 alternating rounds, the two HTJ2K files):

| Sample | Threads | Time base -> new, change per round | Mean |
|---|---|---|---|
| `Bretagne1_ht` | multi | +3.1%, +1.1%, +2.2%, -0.2% | +1.5% |
| `Bretagne1_ht` | single | +1.9%, +1.8%, +2.7%, +1.3% | +1.9% |
| `Bretagne1_ht_lossy` | multi | -3.3%, +0.8%, -0.5%, -0.4% | -0.9% |
| `Bretagne1_ht_lossy` | single | -0.9%, +0.8%, +2.6%, +1.3% | +1.0% |

Allocated bytes per decode, exact: `Bretagne1_ht` 5 389 302 -> 5 380 565 (multi) and 5 376 094 -> 5 367 358 (single); `Bretagne1_ht_lossy`
5 200 221 -> 5 191 490 and 5 186 318 -> 5 177 582: 8.7 KB less per decode (-0.2%), about 360 code blocks at 24 bytes each.

Time is inside the run-to-run noise (about +-2.5%), with a small tendency to be slower (11 of 16 rounds positive, single-thread
rows mostly), so there is no speed gain to claim, and the saving is 0.2% of the allocations. Parity 315/315 in the 6 modes, 1 404/1 404 on the
PDF streams, tests 289/294. The project rule for a result like this is to revert it and list it under "did not help" (see
`.claude/skills/optimising-jpx-decoder-performance`); it was committed anyway because the repo owner asked for it after seeing these numbers.
Reverting is a one-line change.

### Item 3 (`TagTree` `nplh` / `nplv`) applied and measured: no speed gain, 0.04% to 0.4% fewer allocated bytes, kept at the repo owner's request

Change: in `TagTree.Create` and `TagTree.Init` the four `new int[32]` (`nplh`, `nplv` in each) are `Span<int> ... = stackalloc int[32]`. They are only
indexed, in short loops, and the bound is the same 32, so an out-of-range index still throws.

Benchmark: `DecodeBenchmarks` on the 7 regular PDF samples, `short` job, 12 iterations, HEAD against HEAD plus the change, short-path
checkouts, 4 alternating rounds (56 comparisons: 7 files x single / multi thread x 4 rounds).

- Time: mean change over all 56 comparisons +0.07%. Per row, mean of 4 rounds: multi thread +0.2% to +2.3% (`PDFBOX-1845` +2.3%, `10719` +1.2%,
  `LINK-3167` +1.1%), single thread -3.5% to +0.5% (`10026` -3.5%, `10719` -1.2%). Single rounds range from -10.7% to +7.0%, the same scatter as
  baseline against baseline (-> noise).
- Allocated bytes per decode, exact (round 1): `MOZILLA-10719-0_3` 187 760 000 -> 187 626 848 (-133 152, -0.07%), `LINK-4649` 18 818 142 ->
  18 779 230 (-38 912, -0.21%), `LINK-3181` -29 358, `8179` -7 607 (-0.15%), `10026` -9 728, `PDFBOX-1845` -9 713 (all below -0.03%).
  The number of tag trees built is small next to the other allocations of a decode (the 128-byte arrays are 1 to 2 per precinct and band).
- Checks: parity 315/315 in the 6 modes, 1 404/1 404 on the PDF streams (single and multi thread), tests 289/294.

Same verdict as item 2: inside the noise, so the project rule (revert, list under "did not help") applies; kept because the repo owner asked for it.

### Items 4, 5 and 6: not applied (estimated gain below the noise)

Items 2 and 3 gave no measurable gain, so items 4 to 6 were sized before applying anything. These are upper bounds worked out from the array sizes, the tile counts of the benchmark samples and the allocated totals above; they were not benchmarked and no code was changed.

| Item | Allocated per tile | Per decode | Share of the allocated total |
|---|---|---|---|
| 4 `first_pass_failed` | one `bool[numcomps]`, about 32 B | `MOZILLA-10719-0_3` (140 tiles): about 4.5 KB | about 0.002% of 187.8 MB |
| 5 `tmp_data` / `tmp_ptr` | `uint[numcomps * 132]` + `uint[numcomps]`: about 1.6 KB (RGB), about 2.2 KB (4 components) | `MOZILLA-10719-0_3`: about 230 KB; `LINK-4649` (12 tiles): about 26 KB | about 0.12% and 0.14% |
| 6 `MCT.DecodeCustom` | two `IntOrFloat[n_comps]`, custom MCT only | none of the 7 samples uses a custom MCT | not measurable |

A few hundred bytes per tile costs well under a microsecond, against milliseconds of decoding per tile; items 2 and 3 (-0.04% to -0.4% allocations, time within noise) show the same order of effect. Item 5 would also change the `GetAllEncodingParameters` signature (`Span<uint>` instead of `uint[]` / `ref uint[]`), and item 4 changes the loop scoping, for no measurable benefit.

Decision: not applied. Reopen only if a workload with many small tiles shows these allocations in a profile.
