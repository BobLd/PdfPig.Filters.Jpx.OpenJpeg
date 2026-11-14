# Tidy-up progress

File-by-file tidy-up of `UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG`. Rules only change form, never behaviour: any
performance or bug observation goes to [`tidy-up-deferred-findings.md`](tidy-up-deferred-findings.md) and is not
fixed here. The inventory of older leftovers (symbols, TODOs, commented-out code) is in [`tidy-up.md`](tidy-up.md).

## Checklist (applied to every file)

Original rules:

1. Correct formatting (indentation, no trailing whitespace, spacing in casts, blank lines between members).
2. `v is null` instead of `v == null`.
3. `v is not null` instead of `v != null`.
4. Remove `using` directives that are not required (`ImplicitUsings` is on: `System`, `System.IO`, `System.Linq`,
   `System.Collections.Generic`, `System.Threading`, `System.Threading.Tasks`, `System.Net.Http`).
5. Declare local variables in the innermost scope that uses them.
6. File-scoped `namespace X;` instead of `namespace X { ... }`.
7. Explicit `private` where the modifier is missing (fields, nested types, methods).

Added from `BIO.cs` (apply to every file):

8. Delete commented-out code (also the `//Debug.Assert(false, ...)` markers; the inventory is in `tidy-up.md`).
9. Narrowest accessibility: a member only used inside its type is `private` (not `internal`).
10. Fields never reassigned after construction are `readonly`.
11. No redundant initialisers (`= 0`, `= false`, `= null` on fields).
12. Braces: always on `for`/`foreach`/`while` bodies and on every branch of an `if` chain that has an `else`; on a plain
    `if` only when its body spans several lines (a one-line `if (x) return;` guard stays as it is). A body written on
    the header line (`for (...) stmt;`) moves to its own braced lines.
13. Expression-bodied read-only properties (`int P => x;`).
14. `sealed` on classes nothing derives from.
15. One type per file, file named after the type.
16. XML doc comments (`///`) instead of `/** */`; every `<param>` documented; the text matches the type.
17. Fix typos in identifiers and comments.
18. One declaration per field when each needs its own comment or modifier (`readonly` on some of a list).
19. A private field that only backs a property becomes an auto-property (`public int P { get; private set; }`),
    and the code uses the property instead of the field.

Added from `DWT.cs`:

20. A local only used by one `for` and assigned in its initialiser (`uint i; ... for (i = 0; ...)`) is declared in the
    initialiser (`for (uint i = 0; ...)`).
21. A local whose first use is a plain assignment inside a nested block is declared there (`int s2; ... s2 = x;` becomes
    `int s2 = x;`). Locals with a non-trivial initialiser, or carried over loop iterations, are never moved (the
    initialiser would run more often or its operands could have changed).
22. A `private` helper called only from its own type is not `internal` (here `decode_tile_97`).
23. Nested classes nothing derives from are `private sealed`.

Added from the repo owner's hand edits in `DWT.cs` (apply to every file):

24. Join declaration and assignment: `T x; ... x = e;` (first use a plain assignment) becomes `T x = e;`. The hand edits
    used `var` when the initialiser has exactly the declared type and kept the explicit type otherwise
    (`ulong data_size = MaxResolution(...)`, which returns `uint`); the script keeps the declared type everywhere.
25. Variable declaration can be inlined: `uint a; F(out a);` becomes `F(out var a);` (script: `out uint a`).
26. Remove unused local variables (`uint i, j;` to `uint j;`) when the initialiser has no effect.
27. Remove a redundant `else` after a branch that always returns, throws, continues or breaks: the `else` body follows
    the `if`.
28. Invert an `if` whose body is the rest of the method (`if (len > 2) { ...; return; }` becomes
    `if (len <= 2) return;` and the body follows). Hand edit only, the script does not do it; remove the bare block it
    leaves (rule 29).
29. Remove bare `{ ... }` blocks that only add a scope.
30. Collection expressions for array initialisers: `new double[][] { new double[] { ... } }` becomes `[ [ ... ] ]`, with
    the `[` on its own line after `=`. Also for a plain assignment (`_marks = new J2kMarker[] { ... }`). Hand edit only;
    the script has no step for it yet.

Added from `ImageComp.cs` (apply to every file):

31. No space after a cast: `(uint) x` and `(ushort) (x)` become `(uint)x` and `(ushort)(x)` (by hand; the formatter does not).
32. A `using Alias = System.Type;` alias is replaced by the type keyword and removed (`OPJ_UINT32` to `uint`) when the
    alias hides nothing (here `System.UInt32`). Other files alias `OPJ_UINT32` to `System.Int32` (`DecodeParms.cs`):
    check before replacing.
33. Unsigned right shift of a signed value: `(int)(((uint)x) >> n)` becomes `x >>> n` (`x >>>= n`). C# 11, the project uses
    language version 12; identical IL for `int`. Only where the operand is a signed integer: unsigned arithmetic that
    is cast later (`Tier1Coding.cs`: `(int)((((uint)width + 7u) >> 3) + 1u)`) is not this pattern.

Added from `Invert.cs` (apply to every file):

34. After the namespace dedent, a wrapped parameter or argument list is re-aligned under the first parameter (the
    formatter keeps the old column): by hand.

Added from the repo owner's hand edit in `J2KTables.cs` (apply to every file):

35. A struct whose fields are all `readonly` and that has no mutating members is a `readonly struct`
    (`internal readonly struct J2kMarker`); same layout, no defensive copies. Structs with mutable fields, or whose
    fields are written after construction, stay as they are (check each; not every struct in `MinorStructs.cs` was looked at).

Added from `MinorStructs.cs` (apply to every file):

36. Top-level types and delegates carry an explicit accessibility (`internal struct JP2Box`, `internal delegate ...`),
    like members do (rule 7). Checked at the end of a file: `grep -nE "^(struct|class|enum|delegate) "` on the dedented
    files.
37. Attribute names without the `Attribute` suffix (`[StructLayout(...)]`, not `[StructLayoutAttribute(...)]`) and without empty parentheses (`[Flags]`, not `[Flags()]`).

Added from the repo owner's hand edit in `PacketIterator.cs` (apply to every file):

38. A field or local that is compared with `null` (`is null` / `is not null`) or assigned `null` is annotated nullable
    (`private bool[]? include;`), since the project has `<Nullable>enable</Nullable>`. Only where the null case is real;
    the code base is not nullable-clean (1 700+ warnings), so this is done per file, not by a sweep.

Added from `ProgOrdChang.cs` (apply to every file):

39. A local assigned once and returned on the next line is returned directly (`return MemberwiseClone();`).

Added from the repo owner's review of `Tier1Coding.cs` (apply to every file):

40. A method that uses no instance state (no `this`, no instance field / property / method / event, no `base`) is `static`
    when it is not part of the public API and not an interface implementation or an override. Call sites through an
    instance (`t1.M(x)`) become `Type.M(x)`. Same behaviour; the call no longer passes `this`. `tools/tidy/tidy.cs static`
    finds them with a semantic model of the whole `OpenJPEG` folder, rewrites the call sites in the file and reports the
    methods called from other files (those are changed by hand, together with the callers).

Deferred check (to run on every file later, not applied yet): nullability is respected and declared with `?` where null is
a real value, and method signatures are correct (parameter and return types, `?`, `out` / `ref`). Rule 38 is the
per-file part of it; the compiler's nullable warnings (CS8600 to CS8625, CS8618) are the guide.

Caution for rules 27 and 29 (found by the verification of `PacketIterator.cs`): taking a `{ ... }` block, or an `else { ... }`
body, out of its scope must not change what a name means afterwards. In the `Next_rpcl` / `Next_pcrl` / `Next_cprl` iterators a
bare block declared `uint compno, resno;` to shadow the iterator's public fields of the same names; the code after the block
assigned the fields (that is how the caller sees the current position). With the block gone the locals shadowed the fields,
the fields were never updated and 20 conformance files failed. The script now skips a removal when a local declared in the
block (or an `out` / pattern variable) has the same name as an identifier used elsewhere in the parent block, and prints
"skipped ... would shadow a name used in the parent block". Re-running it over the committed (HEAD) version of every tidied file
finds only those three blocks. The same applies by hand: never turn a `for (resno = ...)` that assigns a field into
`for (uint resno = ...)`.

Script coverage of rules 20, 21 and 24 (what `tools/tidy/tidy.cs` joins / moves; the rest is flagged and left):

- first use is `v = expr;` as a direct statement of a block, or `out v` in an expression / declaration statement;
- first use is the initialiser of a `for (v = expr; ...)` that contains all the uses of the group (one initialiser);
- the variable is used by several separate top-level statements of the block, each starting as one of the two cases
  above (looking into nested blocks and loops): it is declared in each (`PIComp comp;` ... `comp = comps[compno];` in several loops), unless the groups would
  land in the same block;
- the declaration has no initialiser, or a literal / `default` one (dead because it is overwritten first).

Not done (kept, and reported by the script as "non-trivial initialiser" or "re-evaluated per iteration"): a declaration with
a real initialiser (`var tcp = cp.tcps[tile_no];`, `uint rx0 = f(a, b);`) whose uses are all in an inner block. Moving it
would change when the expression is evaluated (it can throw, or its operands can change in between).

How the mechanical rules are applied: `tools/tidy/tidy.cs` (a Roslyn script, see `tools/README.md`) handles 1, 6, 7, 12,
20, 21, 24, 25, 26, 27 and 29. It parses every file with five `#if` symbol sets (`NET8_0_OR_GREATER`,
`NET7_0_OR_GREATER`, `NETCOREAPP3_0_OR_GREATER`, `NETSTANDARD2_1`, none, plus `DEBUG`) and only changes what all sets
agree on. The rest (`is null`, `using`, 8, 9, 10, 11, 13 to 17, 22, 23, 28, 30 to 39; 40 is done by the `static` step) is done by hand. The result is checked
by building every target (6), then parity (6 modes, 315 + 1 404) and the tests.

Not applied: snake_case field names (`_data_pos`, `bit_buffer`) match the whole port; renaming them is a separate,
repo-wide decision.

## Files

| File | Status | Notes |
|---|---|---|
| `Internal/BIO.cs` | done | below |
| `Internal/WBIO.cs` | done | split out of `BIO.cs`, below |
| `Internal/DWT.cs` | done, verified | see "DWT.cs" below; open items under "Candidate items" |
| `Internal/ht_dec.cs` | done | see "ht_dec.cs" below |
| `Internal/ImageComp.cs` | done, verified | see "ImageComp.cs" below |
| `Internal/Invert.cs` | done, verified | see "Invert.cs" below |
| `Internal/J2KTables.cs` | done, verified | see "J2KTables.cs" below |
| `Internal/MCT.cs` | done, verified | see "MCT.cs" below |
| `Internal/MinorStructs.cs` | done, verified | see "MinorStructs.cs" below |
| `Internal/MQCoder.cs` | done, verified | see "MQCoder.cs" below |
| `Internal/MyMath.cs` | done, verified | see "MyMath.cs" below |
| `Internal/PacketIterator.cs` | done, verified | see "PacketIterator.cs" below |
| `Internal/Parallelism.cs` | done, verified | see "Parallelism.cs" below |
| `Internal/ProgOrdChang.cs` | done, verified | see "ProgOrdChang.cs" below |
| `Internal/SparseArray.cs` | done, verified | see "SparseArray.cs" below |
| `Internal/T1HTLuts.cs` | done, verified | see "T1HTLuts.cs" below |
| `Internal/T1Luts.cs` | done, verified | see "T1Luts.cs" below |
| `Internal/TagTree.cs` | done, verified | see "TagTree.cs" below |
| `Internal/TcdImage.cs` | done, verified | see "TcdImage.cs" below |
| `Internal/Tier1Coding.cs` | done, verified | see "Tier1Coding.cs" below |
| `Internal/Tier2Coding.cs` | done, verified | see "Tier2Coding.cs" below |
| `Internal/TileCoder.cs` | done, verified | see "TileCoder.cs" below |
| `Internal/TileCodingParams.cs` | done, verified | see "TileCodingParams.cs" below |
| `Internal/TileCompParams.cs` | done, verified | see "TileCompParams.cs" below |
| `Util/BitWriter.cs` | done, verified | see "BitWriter.cs" below |
| `Util/LinearInterpolator.cs` | done, verified | see "LinearInterpolator.cs" below |
| `Util/Scaler.cs` | done, verified | see "Scaler.cs" below |
| `CIO.cs` | done, verified | see "CIO.cs" below |
| `CodingParameters.cs` | done, verified | see "CodingParameters.cs" below |
| `CompressionInfo.cs` | done, verified | see "CompressionInfo.cs" below |
| `Constants.cs` | done, verified | see "Constants.cs" below |
| `DecodeParms.cs` | done, verified | see "DecodeParms.cs" below |
| `Enums.cs` | done, verified | see "Enums.cs" below |
| `EventMgr.cs` | done, verified | see "EventMgr.cs" below |
| `J2K.cs` | done, verified | see "J2K.cs" below |
| `JP2.cs` | done, verified | see "JP2.cs" below |
| `JPXImage.cs` | done, verified | see "JPXImage.cs" below |

### `Internal/BIO.cs` and `Internal/WBIO.cs`

Rules 1 to 7:

| Rule | Result |
|---|---|
| Formatting | Trailing whitespace removed (comment lines, blank line in `WriteBit`); `(byte) (` to `(byte)(`; blank line between the `WriteBit` overloads. CRLF and BOM kept. |
| `is null` / `is not null` | Nothing to change (no null comparisons). |
| `using` | `using System.IO;` removed (implicit; only `EndOfStreamException` used). |
| Locals in inner scope | Nothing to change (`skip` already inner; `write_null` in `Flush` is read after the `if`, so it stays outside). |
| File-scoped namespace | Done in both files. |
| Missing `private` | `BIO`: `_data`, `_data_pos`, `_start_pos`, `_end_pos`, `FIRST_BIT`. `WBIO`: `_buf_pos`, `_unfinished_byte`, `_u_pos`, `_target`, `_length`, `_written`. |

Made by hand by the repo owner (now rules 8 to 14, 18 and 19):

- Commented-out `ReadInt` removed.
- `Position` and `Written` expression-bodied.
- Braces added in `BIO.Read` and `WBIO.Write`.
- `BIO` and `WBIO` `sealed`.
- `WBIO._buf`, `_target`, `_length` `readonly`; `_length` and `_written` split into two declarations.
- `WBIO._written` and the `Written` property merged into `public int Written { get; private set; }`; `WriteBit` and
  `Flush` use it directly.

Candidate items applied afterwards:

- `//Debug.Assert(false, ...)` comment in `ReadUInt` removed.
- `bit_buffer`, `n_buf_bits` and `WBIO._buf` made `private` (no use outside their class).
- `_start_pos`, `_end_pos` made `readonly` (split from `_data_pos`).
- Redundant `= 0` initialisers removed (`bit_buffer`, `n_buf_bits`, `_buf_pos`, `_unfinished_byte`).
- `FIRST_BIST` renamed `FIRST_BIT`; `bites` to `bits`, `subtelty` to `subtlety`.
- Doc comments: `end_pos` documented; `ReadUInt` returns "a uint"; the `/** */` comments of `WBIO` turned into `///`
  (the copy of the `_length` comment on `_written` replaced by the `Written` text).
- `WBIO` moved to its own file `WBIO.cs` (with the same licence header).

Check: `dotnet build` of the library, 0 errors. No member visible outside the two files changed (all removed
accessibility was unused elsewhere; searched the repo), so the decoder output cannot change; the parity corpus and
the tests were not re-run.

### `Internal/DWT.cs` (4 528 lines)

| Rule | Result |
|---|---|
| Formatting | Whole file formatted (Roslyn formatter, for each `#if` variant, so disabled regions too): K&R braces to Allman, tab-indented table rows, trailing whitespace, doubled blank lines. CRLF and BOM kept. |
| `is null` | 2: `sa == null` to `sa is null` (`SparseArrayInt32` has no `operator ==`). No `!= null`. |
| `using` | `System` and `System.Threading` removed (implicit; the `Thread*` hits are `DisableMultiThreading` / `Parallelism`). The other five are used. |
| Locals in inner scope | 6 moved: `w` (`MaxResolution`, into the loop), `c` (`EncodeAndDeinterleaveV`, into the `if` block), `s2` twice (merged with its assignment, `int s2 = ...`), `i` and `j` (into their `for` initialisers, rule 20). About 2 400 other locals are already in their innermost block; 298 more are initialised outside a loop on purpose. |
| File-scoped namespace | Done. |
| Missing `private` | 58 members: the constants, tables, delegates, helper methods and the two nested classes `dwt_local`, `v4dwt_local`. |
| Commented-out code (8) | 6 blocks removed (the `c:/temp` wavelet dump, the `faa` probe at `i == 5`, `Debug.Write("Hello")`, the `mem_copy` check in `idwt53_v` and its `Debug.Write("Nah")` twin) and the 4 `//if (tbx0)` style lines. |
| Accessibility (9, 22) | `decode_tile_97` `internal` to `private` (only called inside `DWT`). The other `internal` members are used by other files. |
| Braces (12) | 45 bodies (loops, `else` chains, one `if` with a multi-line body), including 6 `foreach (...) ArrayPool<T>.Shared.Return(...)` on the header line. |
| `sealed` (14, 23) | The 8 nested classes `private sealed`. |
| Typos (17) | `libary`, `wavlet`, `continious`. |
| Nothing to change | readonly (10) and initialisers (11): no fields of the kinds concerned; expression-bodied properties (13), one type per file (15), `/** */` comments (16): none. |

Made by hand by the repo owner afterwards (now rules 24 to 30):

- `new double[] { ... }` initialisers of `dwt_norms` and `dwt_norms_real` turned into collection expressions.
- Declaration and assignment joined (`var num_threads = ...`, `var resno`/`level`/`orient`/`gain`, `var p`/`n`, `var sa`,
  `var ret`, `var s1`/`dc`, `ulong data_size`/`h_mem_size`, `uint tr_ll_x0` ...), `out var` for the `win_*` variables.
- Unused `uint i` removed.
- Redundant `else { return ...; }` removed in `Decode` and `DecodeReal`; one `if` inverted in `idwt53_v` (`if (len <= 2)
  return;`), which left a bare block; the bare block around the `while (--numres != 0)` loop of `decode_tile` removed.

Second pass with `tools/tidy/tidy.cs` over the same rules (22 locals, 6 bare blocks):

- Sibling loop counters: `uint j` of `decode_tile` is now declared in each of its two `for` loops.
- Joined or inlined: `sa` (`DecodePartialTile`), `i_max` (`decode_partial_1`, `decode_partial_1_parallel`), `ret` (`v8dwt_interleave_partial_h`), `d1c`/`d1n`/`s1n`/`s0c`/`s0n` (`idwt3_v_cas0`), `out uint` for the `win_*`
  variables and `uint` for `tr_ll_x0` ... in `DecodePartialTile`.
- Bare blocks removed: the one around the `while (--numres != 0)` loop of `decode_tile_97`, the one after
  `if (len <= 2) return;`, and the four blocks in `GetBandCoordinates` (former `//if (tbx0)`).
- No unused locals or redundant `else` left.

Check: build of all 6 targets, 0 errors (1 743 warnings); parity 315/315 in default, `PARITY_THREADS=1`,
`PARITY_AREA=1`, `PARITY_FORCE_V512`, `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`, 1 404/1 404 on the PDF
streams in both thread modes; tests on net8.0 289/294 (the 5 known failures).

Third pass (after the script learned rule 24 for split variables and `comp.dx`-style member names):

- `uint c` of `EncodeAndDeinterleaveV` is now declared in each of its 5 `for (uint c = 0; ...)` loops (it was the open
  "sibling loop counters" item).
- `int dn` of `opj_idwt53_h_cas1` and `idwt3_v_cas1` joined at its two assignments each (`int dn = ...`).

### `Internal/ht_dec.cs` (1 045 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants (the `population_count` / `count_leading_zeros` fallbacks too), trailing whitespace. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `System.Diagnostics` is used (`Debug`). |
| Locals (21, 24) | 28 joined with their first assignment (`d`, `d1`, `d2`, `suffix_len` in the two UVLC decoders; `val`, `bits`, `tmp`, `unstuff`, `t`, `d_bits` in the MEL / REV / FRWD init and fill functions). |
| File-scoped namespace | Done. |
| Missing `private` | None (already explicit). |
| Commented-out code (8) | 3 blocks removed: the `bytes.Length` check in `ReadUIntLE`, `WriteUIntLE`, the `Debug.Assert(unstuff == false ...)` in `dec_mel`. |
| Accessibility (9) | Nothing to narrow: every `internal` member is used by `Tier1Coding.cs`. |
| `sealed` (14) | `dec_mel`, `rev_struct`, `frwd_struct` `internal sealed`. |
| Initialisers (11) | `only_cleanup_pass_is_decoded = false` to no initialiser. |
| Collection expressions (30) | `dec` (byte table) and `exp` (MEL exponents) by hand. |
| Nothing to change | Braces (12), `else` (27), bare blocks (29), readonly (10): none. |

Check: build of all 6 targets, 0 errors; parity 315/315 in the 6 modes and 1 404/1 404 on the PDF streams in both thread
modes; tests on net8.0 289/294 (the 5 known failures). The set of build warnings of net8.0 is the
same as with the committed file.

### `Internal/ImageComp.cs` (248 lines, public type)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter (trailing whitespace on the blank line after `factor`, `{}` to `{ }`); casts without a space (31). CRLF and BOM kept. |
| `is null` | No null comparisons. |
| `using` | `using System;` removed (implicit); the `OPJ_UINT32` alias removed, `internal OPJ_UINT32 prec` is `internal uint prec` (32). |
| Locals | None to move. |
| File-scoped namespace | Done. |
| Missing `private` | None (every member has a modifier). |
| Braces (12) | 5 `for` bodies in `MakeBILevel` and `ScaleBPC`. The `if (new_prec == prec) return;` guard stays. |
| Expression-bodied (13) | `BPP`, `Prec`, `Width`, `Height`, `Data`. |
| Typos (17) | `Precisision`, `usualy`. |
| Unsigned shift (33) | `data[c] = (int)(((uint)data[c]) >> shift)` to `data[c] >>>= shift` in `ScaleBPC` (suggested by the IDE). |
| Nothing to change | Commented-out code, readonly, initialisers, sealed (public type, see candidates), one type per file. |

Check: build of all 6 targets, 0 errors. The public surface is unchanged (same members, same accessibilities).
Parity and tests: see "Verification runs".

### `Internal/Invert.cs` (222 lines)

| Rule | Result |
|---|---|
| Formatting | Tabs and mixed indentation of `lupDecompose`, trailing whitespace, doubled blank lines; wrapped parameter lists re-aligned (34). CRLF and BOM kept. |
| `is null` | No null comparisons. |
| `using` | `using System;` removed (`Array.Copy` is covered by the implicit usings). |
| Locals in inner scope | 21 moved or joined: `lupDecompose` (`k`, `p`, `lColumnMatrix`, `lDestMatrix`, `dstPermutations`, `t`, `j` into their blocks / loops, `temp` before the pivot loop), `lupSolve` (`k`, `u`, `lIntermediatePtr`, `lGeneratedData`, `lDestPtr`), `lupInvert` (`j`, `i`, `lCurrentPtr`). `k2` is carried across iterations of the `k` loop and stays. |
| Unused locals (26) | `lSwapSize` in `lupDecompose` (the script now also deletes unused locals whose initialiser has no side effect). |
| File-scoped namespace | Done. |
| Missing `private` | `lupDecompose`, `lupSolve`, `lupInvert`. |
| Braces (12) | 2 `for` bodies. |
| Doc comments (16) | The `/** Matrix inversion. */` comment of `MatrixInversion` is a `///` summary. |
| Nothing to change | Commented-out code, readonly, initialisers, sealed (static class). |

Check: build of all 6 targets, 0 errors. Arithmetic and statement order are untouched (only declarations moved);
parity and tests: see "Verification runs".

Third pass: in `lupSolve`, `i`, `j`, `sum`, `lCurrentPtr` and `lTmpMatrix` are now declared in their `for` loops / loop bodies
(`for (int i = 0; ...)`, `float sum = 0f;`, `int lTmpMatrix = lLineMatrix;`, then again in the second loop); in `lupDecompose`, `i` is declared in each of its three `for` loops (two of them inside the `k` loop).

### `Internal/J2KTables.cs` (113 lines)

| Rule | Result |
|---|---|
| Formatting | Block namespace dedented, trailing whitespace (comments, `get `), `{ Mark = mark; ... }` constructor body. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `System.Diagnostics` is used (`DebuggerDisplay`). |
| Locals | None. |
| File-scoped namespace | Done. |
| Missing `private` | `DecMstabent._marks`. |
| `readonly` (10) | `J2KTables.tabMaxSubLevelFromMainLevel` (`static`, never reassigned; the array elements are only read). |
| Collection expressions (30) | `tabMaxSubLevelFromMainLevel` and the `_marks` table in the `DecMstabent` constructor. |
| Typos (17) | `mangeled`, `Maker value`. |
| `readonly struct` (35) | `J2kMarker` (made by the repo owner). |
| Nothing to change | Braces, commented-out code, `else`, bare blocks. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/MCT.cs` (522 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants; the file mixed CRLF and LF line endings, now CRLF only. BOM kept. |
| `is not null` | 2 in `SimpleMccDecorrelationData.Clone` (`ArPtr<T>` is a class without `operator !=`). |
| `using` | `System` removed (implicit). `System.Numerics` (`Vector<T>`), `System.Runtime.InteropServices` (`MemoryMarshal`) and `System.Diagnostics` (`Debug`) are used. |
| Locals in inner scope | 5 in `CalculateNorms`: `i` and `j` into their `for` loops, `lIndex` and `lCurrentValue` joined with their assignment. |
| File-scoped namespace | Done. |
| Missing `private` | `mct_norms`, `mct_norms_real`, `DcShiftFloat`. |
| `readonly` (10) and collection expressions (30) | `mct_norms`, `mct_norms_real`, `ELEMENT_SIZE` (never reassigned; `ELEMENT_SIZE` is read by `J2K.cs`). |
| Commented-out code (8) | The `res.F` / `calc1.F` probe at `i == 12` in `DecodeReal`, and the `//public int data_size;` lines in `MctData`. |
| Braces (12) | 6 bodies. |
| `sealed` (14) | `MctData`, `SimpleMccDecorrelationData`. |
| Nothing to change | Typos, accessibility (every `internal` method is used by another file, except `Getnorm` / `Getnorm_real`, see candidates). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/MinorStructs.cs` (531 lines, 17 small types)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented; the cast spacing `(ELEMENT_TYPE) ((int) type + 1)` came out right with the formatter. CRLF and BOM kept. |
| `is null` | No null comparisons. |
| `using` | `System` removed (implicit; `Array`, `Buffer`, `NotSupportedException`). `System.Diagnostics` (`DebuggerDisplay`) and `System.Runtime.InteropServices` (`StructLayout`) are used. |
| Locals | None to move. |
| File-scoped namespace | Done. |
| Missing `private` | `JP2Color.jp2_has_colr`, then removed by rule 19. |
| Top-level modifiers (36) | `JP2Comps`, `JP2Box`, `JP2cdefInfo`, `JP2cmap_comp`, `JP2pclr`, `JP2Color` now `internal`; `J2K_Action` in `J2KTables.cs` too. |
| `readonly struct` (35) | `StepSize` (both fields `readonly`). The other structs have mutable fields. |
| Auto-property (19) | `JP2Color.HasColor { get; set; }` replaces the field `jp2_has_colr` and its property. |
| Expression-bodied (13) | `ShortOrIntOrFloatOrDoubleAr.IsNull`. |
| `sealed` (14) | `JP2pclr`, `TcdMarkerInfo`. |
| Accessibility (9) | `IntOrFloat` `public` to `internal` (its fields already were). `CompressionParameters.mct_data` (`IntOrFloat[]`) had to follow: `public` to `internal` (only `J2K.cs` reads it; an outside caller could not fill the `internal` fields anyway). |
| Commented-out code (8) | The `JP2cdef` class (with its doc comment) and `//int icc_profile_len;`. |
| Attribute suffix (37) | 3 `[StructLayoutAttribute]` to `[StructLayout]`. |
| Braces (12) | 12 bodies (the `for` loops of the `Copy*` methods). |
| Nothing to change | Typos, `else`, bare blocks, readonly fields. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/MQCoder.cs` (969 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants (the `Decode` / `Renormd` variants), namespace dedented. CRLF and BOM kept. |
| `is null` | No null comparisons. |
| `using` | `System` removed (implicit). `System.Runtime.CompilerServices` (`MethodImpl`) and `System.Diagnostics` (`Debug`) are used. |
| Locals | None to move: the locals are already declared where they are first used. |
| File-scoped namespace | Done. |
| Missing `private` | 16 declarations: `BYPASS_CT_INIT`, `_end`, `_data`, `_backup`, `_bp` / `_start`, `CommonInit`, `Codelps`, `Codemps`, `Renorme`, `Renormd` (both), `ByteIn` (both), `Byteout`, `BuildTable`, `mcqints`. |
| Accessibility (9) | `_end_of_byte_stream_counter` and `Setbits()` `internal` to `private` (not used outside the class). |
| `readonly` (10) | `mcqints` (`static`, never reassigned; a 2-D array, so no collection expression). |
| Expression-bodied (13) | `NumBytes`. |
| Braces (12) | 9 bodies. |
| Typos (17) | `orgiginaly`, `encodies`, `usure whet`, `referes`. |
| Nothing to change | Commented-out code, `else`, bare blocks, `sealed` (`MQCoder` already is). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/MyMath.cs` (171 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; tab-indented bodies of `int_ceildiv` and `int_abs`, trailing whitespace. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons, no `using` directives. |
| Locals | None to move (`l` in the two `floorlog2` functions is returned after the loop). |
| File-scoped namespace | Done. |
| Nothing to change | Missing `private` (all members are `public`), braces, commented-out code, `else`, `sealed` (static class). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/PacketIterator.cs` (2 119 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented; tab-indented blocks in `GetEncodingParameters`, `GetAllEncodingParameters`, trailing whitespace, K&R braces. CRLF and BOM kept. |
| `is null` / `is not null` | 8: `resolutions`, `lResolution`, `resolution_ptrs`, `current_pi.include` and the four `Debug.Assert(... != null)` on `cp` / `image`. |
| `using` | `System` and `System.Collections.Generic` removed (implicit). `System.Diagnostics` (`Debug`) is used. The `OPJ_UINT32 = System.UInt32` alias removed, every `OPJ_UINT32` is `uint` (rule 32). |
| Locals in inner scope (21, 24) | Dozens of declarations moved into their loops / blocks or joined with their first assignment, in `GetEncodingParameters`, `GetAllEncodingParameters`, `UpdateEncode*`, the `Next_*` iterators and `Create*`; `out` variables inlined in 4 calls (`out uint l_tx0` ...). Variables carried across iterations stay outside. |
| File-scoped namespace | Done. |
| Missing `private` | The fields of `PacketIterator` (`_cinfo`, `tp_on`, `include`, `step_*`, `first`, `numcomps`, `comps`, `tx0` ..., `dx` ...), the 9 `Update*` / `Get*` / `CheckNextLevel` helpers and the 5 `Next_*` iterators. |
| Joined declarations (24) | After a review: `comp` / `res` / `compno` / `resno` / `dx` / `dy` of `Next_rpcl`, `Next_pcrl`, `Next_cprl` and `compno` / `resno` of `GetAllEncodingParameters` are declared where they are first assigned (`PIComp comp = comps[compno];`, `for (uint resno = ...)`); `incr_top` moved into its block. |
| Commented-out code (8) | 9 lines: `//bool skip;`, `//var tccps = tcp.tccps;` (twice), `//current_pi.dx/dy = ...`, `//current_pi.first = true;` (twice), `//first = false;` (twice). The four `//uint bound = tcp.numpocs + 1; C# uses pi_ar.Length instead` lines stay: they record a deliberate difference from upstream. |
| Braces (12) | The `if` / `for` bodies written on one line. |
| Typos (17) | `usefull`, `useing`. |
| Nullable (38) | `include` is `bool[]?` (made by the repo owner; the iterator checks `include is null`). |
| Accessibility (9) | Nothing to narrow: every `internal` member is used by another file. |
| Nothing to change | `else`, bare blocks, `sealed` (`PacketIterator` already is; `PIComp` / `PIResolution` below). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".
Fix after verification (20 conformance files failed): the three `Next_*` iterators (`rpcl`, `pcrl`, `cprl`) had their scope blocks removed andtheir position loops turned into `for (uint resno = ...)` / `for (uint compno = ...)` locals that shadowed the fields `resno` / `compno`(see the caution under the checklist). The five loops after the first part assign the fields again (`for (resno = poc.resno0; ...)`);the first part keeps its loop-local `compno` / `resno`, as in the original bare block.

### `Internal/Parallelism.cs` (261 lines, not an upstream port: the thread helper and `WorkerPool`)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; nothing else (already formatted). Plain ASCII, no BOM, CRLF kept. |
| `is null` / `is not null` | 6: `batch.Error`, `Volatile.Read(ref Error)`, `_keepGoing`, `current`, and the two `Interlocked.CompareExchange(...) == null` results. |
| `using` | `System.Runtime.ExceptionServices` is used (`ExceptionDispatchInfo`); nothing else declared. |
| Locals | None to move. |
| File-scoped namespace | Done. |
| Nothing to change | Missing `private` (all explicit), braces, commented-out code, `else`, bare blocks, `sealed` (`Batch<T>`, `Worker`, `RetiredBatch` already are; `Batch` is abstract), readonly, nullable (already annotated: `Exception?`, `Func<bool>?`, `Batch?`). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/ProgOrdChang.cs` (112 lines, public type)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; tab-indented fields and trailing whitespace (the field declarations and their blank lines). CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `using System;` removed (implicit). |
| Locals | `Clone()`: `var poc = (ProgOrdChang)MemberwiseClone(); return poc;` to `return MemberwiseClone();` (39). |
| File-scoped namespace | Done. |
| Nothing to change | Missing `private`, braces, commented-out code, readonly, `sealed` (public type, see candidates). |

The public surface is unchanged (`ProgOrdChang` is reached through the public `CodingParameters.ProgressionOrderChange`).
Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/SparseArray.cs` (717 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented. This file has LF line endings in the working tree (the others are CRLF); kept as it was, BOM kept. |
| `is null` / `is not null` | 6: `src_block` (4), `sa`, `cblk.decoded_data`. |
| `using` | `System` removed (implicit); `System.Runtime.InteropServices` (`MemoryMarshal`) is used. |
| Locals in inner scope | 20 in the two `read_or_write` overloads (`float[]` and `int[]` buffers): `y` / `block_y`, `x` / `block_x`, `block_y_offset`, `block_x_offset`, `src_block` joined or declared in their `for`; the inner `k` in 6 places. `y_incr` / `x_incr` are carried across iterations and stay. |
| File-scoped namespace | Done. |
| Commented-out code (8) | 4 copies of the `//if (x_incr == 4) { ... memcpy ... } //else` block, which left a bare block each: removed (29). |
| Nothing to change | Missing `private` (all explicit), braces, readonly (the fields already are), `sealed` (already). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/T1HTLuts.cs` (269 lines, two lookup tables)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented (the 2 x 128 table rows moved one level left). CRLF and BOM kept. |
| `is null` / `using` | None. |
| Collection expressions (30) | `vlc_tbl0` and `vlc_tbl1`: `= new ushort[] { ... }` to `=` then `[ ... ];` (same constants, same `static readonly`). |
| Nothing to change | Locals, missing `private` (the tables are `internal` and used by `Tier1Coding.cs`), braces, comments. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/T1Luts.cs` (278 lines, lookup tables and their accessors)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace on the table rows, doubled blank lines; table rows re-indented under the `[`. LF line endings kept (this file, like `SparseArray.cs`, is LF in the working tree), BOM kept. |
| `is null` / `using` | None. |
| Missing `private` | The four `short[]` tables `lut_nmsedec_ref`, `lut_nmsedec_ref0`, `lut_nmsedec_sig`, `lut_nmsedec_sig0`. |
| Collection expressions (30) | All 7 tables (`short[]` x 4, `sbyte[]` x 2, `bool[]`) from `= { ... }` / `=` + `{ ... };` to `=` + `[ ... ];`. |
| Nothing to change | Locals, braces, `else`, bare blocks, commented-out code. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/TagTree.cs` (392 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace in the class comment. CRLF and BOM kept. |
| `is null` / `is not null` | 5: `tt`, `node != null` (while condition), `node.parent != null` (twice). |
| `using` | `System` removed (implicit). `System.Diagnostics` stays: `[DebuggerDisplay]` on `TagNode` needs it (an attribute, not a `Debug.` call; a first removal broke the build on every target, so the `using` check is "remove, build all targets"). |
| Locals | None to move. |
| File-scoped namespace | Done. |
| Missing `private` | `_numleafsh`, `_numleafsv`, `_numnodes`, `nodes`. |
| Braces (12) | 16 bodies; the trailing comments `//<-- Invalid parrent` stay on their statement (the script put the closing brace after them at first, now fixed). |
| `sealed` (14) | `TagNode`. |
| Nothing to change | Commented-out code (the `//cinfo.Warn(...)` line is kept: it sits under "C# openJpeg 2.5 doesn't emit this warning"), readonly. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs". Nullability (deferred): `TagNode.parent` is null at the root and `Create` can return `null`.

### `Internal/TcdImage.cs` (635 lines, 11 data classes and a struct)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace (`FloatData `, `data_size `, comments), `#if DEBUG` / `#if NET5_0_OR_GREATER` regions formatted too. CRLF and BOM kept. |
| `is null` / `is not null` | 3: `data` in `AllocTileComponentData`, `data` in `TcdCblkEnc.data_size`, `chunks` in `TcdCblkDec.numchunksalloc`. |
| `using` | `System` removed (implicit). |
| Locals | None to move. |
| File-scoped namespace | Done. |
| Missing `private` | `TcdTilecomp.FloatData` (DEBUG only), `NewTileData`. |
| Expression-bodied (13) | `TcdBand.IsEmpty`, `TcdCblkEnc.data_size`. |
| Commented-out code (8) | `//internal byte[] data_ar;` in `TcdSegDataChunk` (its note rewritten as a plain sentence); in `TcdSeg`, a 45-line `<remarks>` essay (pointer-to-pointer, `Ptr<byte[]>`) that documented only the commented-out `//internal Ptr<byte[]> data` and `//internal int dataindex` and ended "Not relevant for 2.5": removed with them. |
| Braces (12) | 1 body. |
| Nothing to change | Accessibility (the data classes are used by the T1 / T2 / TCD code), `sealed` (already), readonly. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/Tier1Coding.cs` (6 260 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented, K&R braces, trailing whitespace, doubled blank lines. CRLF and BOM kept. |
| `is null` / `is not null` | 16 (`_data` x3, `flags`, `cblkdatabuffer`, `t1.cblkdatabuffer`, `mct_norms`, `cblkdata`, 7 x `cblk.decoded_data`, `tilec.data`). The two `= null //C# if converting to a struct` initialisers are assignments, untouched. |
| `using` | `System` and `System.Collections.Generic` removed (implicit). `System.Runtime.InteropServices`, `System.Numerics`, `System.Runtime.CompilerServices`, `System.Diagnostics` are used. |
| Locals in inner scope | About 140 declarations moved into their loops / blocks or joined with their first assignment (the `val`, `t`, `v`, `sym`, `one`, `half`, `runlen`, `sample_mask` ... temporaries of the cleanup / sigprop / magref passes, `i`, `j`, `y`, `n` in `for`s). Carried values stay outside. |
| File-scoped namespace | Done. |
| Missing `private` | The fields (`_data`, `flags`, `cblkdatabuffer` ...), `Rent`, `Return`, `EncIsTermPass`, `Getwmsedec`, the `Enc*pass` / `Dec*pass` methods, `DumpData`, `HalveSamples`, `ScaleSamplesToFloatBits`, `EncodeJob`, `DecodeJob`, `t_cached` and the rest. |
| Bare blocks (29) | About 37 `{ ... }` scope blocks inside the passes removed (the script checks names do not clash). |
| Commented-out code (8) | `GetFlag` / `SetFlag` (`/////` doc comments and code), the `cblk_count` probes (`//Debug`, `//if (cblk_count > 320)`, `> 74`, `== 18`), the `//if (cblkdatabuffer == null) ... //else Array.Resize` pairs (twice), `//cblkdata_pt = ...` (twice), `//Debug.Assert(false, "Zero chunks ...")` and the `//bool v = raw.Decode(); ...` lines under the `NotImplementedException` of the raw refinement pass. Kept: `//uint cblkdata_pt; //<-- No need for both pointer and index` (a port note). |
| Collection expressions (30) | `MOD`. |
| `sealed` (14) | `T1CBLKDecodeProcessingJob`, `T1CBLKEncodeProcessingJob`. |
| Accessibility (9) | Nothing to narrow: every `internal` member is used by `TileCoder.cs`. |
| Static (40) | `Getwmsedec` made `static` by the repo owner (it was missed). The `static` step finds no other candidate in this file. |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `Internal/Tier2Coding.cs` (1 290 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented, trailing whitespace. CRLF and BOM kept. |
| `is null` / `is not null` | 14: `pi_ar`, `pi`, `enumerator`, `marker_info` (2), `dec.segs`, and the `Debug.Assert(... != null, ...)` calls on `cblk.segs[...]` (6), `band.precincts` and `pass`. |
| `using` | `System` removed (implicit); `System.Diagnostics` (`Debug`) is used. |
| Locals | `bit_number` (2) joined, `n_bytes_read` moved into the loop body; 2 unused locals deleted (`l_nb_bytes`, `n_pocs = tcp.numpocs + 1`). |
| File-scoped namespace | Done. |
| Missing `private` | The fields (`_image`, `_cp` ...) and the private methods (`ReadPacketData`, `SkipPacketData`, `ReadPacketHeader`, ...). |
| Redundant `else` (27) | 2 in `ReadPacketData` and `SkipPacketData`: `else { _cinfo.Warn(...); ... }` after a branch that ends in `return`. |
| Commented-out code (8) | The 3 `#if DEBUG` regions that held only `//n_itterations` / `//n_reads` probes (removed with their `#if`), `//cblk.data_current_size = 0;`, `//_cp.ppm_len = ...`. The `//Debug.Assert(dec.segs[index] == null);` line is folded into its explanation: `//C# The slot may already hold a segment: the org impl calls opj_tcd_reinit_segment, so overwriting is fine.` |
| Braces (12) | 28 bodies. |
| Static (40) | No candidate. |
| Nothing to change | Accessibility (the `internal` members are used by `TileCoder.cs`), `sealed` (already). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `Internal/TileCoder.cs` (2 768 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented, tab-indented parts, trailing whitespace, K&R braces. CRLF and BOM kept. |
| `is null` / `is not null` | 26 in code (`_used_component`, `layer`, `cblk.passes[...]`, `current_precinct.*`, `code_block.*`, `band.precincts`, `tilec.resolutions`, `_tcp.mct_*_matrix`, `val`, `cb.decoded_data`). The `data != null` inside a `<remarks>` sentence is prose and unchanged. |
| `using` | `System`, `System.Collections.Generic`, `System.Threading` removed (implicit); the `OPJ_UINT32 = System.UInt32` alias removed, 47 uses are `uint` (32). `System.Numerics`, `System.Runtime.CompilerServices`, `System.Runtime.InteropServices` and `System.Diagnostics` are used. |
| Locals | `size_comp`, `remaining`, `l_data_size`, `img_comps` joined. Locals captured by a lambda stay where they are. |
| File-scoped namespace | Done. |
| Missing `private` | Fields, the DC-shift helpers (`DcShiftRowInt`, `DcShiftRowFloat`, `DcShiftInt`, `DcShiftFloat`) and the rest. |
| Auto-properties (19) | `CurTotnumTp`, `CurPino`, `TpPos`, `TpNum`, `CurTpNum` (backing fields `_cur_totnum_tp`, `_cur_pino`, `_tp_pos`, `_tp_num`, `_cur_tp_num` removed, the code uses the properties; the constructor and the packet-encoding code were the only other users). |
| Expression-bodied (13) | `TcdImage`, `Image`. |
| Static (40) | `CodeBlockEncAllocate`, `CodeBlockEncAllocateData`, `CodeBlockDecAllocate`, `DumpCblk`, `GetTileDimensions` (the script). |
| Redundant `else` (27) | 1 in the layer-allocation loop (after `continue`). |
| Bare blocks (29) | Several removed; the ones whose names would clash with a local of the enclosing block stay. |
| Commented-out code (8) | `lrintf` (with its `<remarks>`), `//tilec.resolutions_size`, three `//l_..._size` pairs, `//p_code_block.data[0] = 0;`, the `//for (...) segs[c] = new TcdSeg()` loop, `//if (tileno == 17) //DumpTilcomp(...)`, and the commented `UpdateTileData` copy loop and size check. |
| Braces (12) | The one-line `if` / `for` bodies. |
| Nothing to change | Accessibility (the `internal` members are used by `J2K.cs`), `sealed` (already). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `Internal/TileCodingParams.cs` (220 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, doubled blank lines. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `using System;` removed (implicit; `ICloneable`, `MemberwiseClone`). |
| File-scoped namespace | Done. |
| Commented-out code (8) | `//internal bool first;` and `//internal int ppt_store;`, each with the `<summary>` that documented it. |
| Braces (12) | 3 `for` bodies. |
| `sealed` (14) | `TileCodingParams` (nothing derives from it). |
| Nothing to change | Locals, missing `private` (`bitvector1` already is), static, accessibility. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Internal/TileCompParams.cs` (100 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; tab-indented fields and trailing whitespace. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `using System;` removed (implicit; `ICloneable`). |
| File-scoped namespace | Done. |
| `sealed` (14) | `TileCompParams` (nothing derives from it). |
| Braces (12) | Already done by the repo owner (`Create`). |
| Nothing to change | Locals, missing `private` (the constructor already is), static, commented-out code. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Util/BitWriter.cs` (68 lines, used by `JPXImage.cs` only)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace. CRLF and BOM kept. |
| `is null` / `using` | No null comparisons; `using System.IO;` removed (implicit; `Stream`). |
| File-scoped namespace | Done. |
| Top-level modifier (36) / `sealed` (14) | `class BitWriter` to `internal sealed class BitWriter`. |
| Missing `private` | `_buf_pos`, `_unfinished_byte`, `_u_pos`, `_target`. |
| Accessibility (9) / `readonly` (10) / initialisers (11) | `_buf` `internal` to `private readonly` (no use outside the class); `_target` `readonly`; `= 0` removed from `_buf_pos` and `_unfinished_byte`. |
| Doc comments (16) | The `/** Empties the buffers ... */` comment of `Flush` is a `///` summary with `<returns>`. |
| Braces (12) | 1 `for` body. |
| Nothing to change | Locals, static, commented-out code. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Util/LinearInterpolator.cs` (76 lines, used by `JPXImage.cs`)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace. Plain ASCII, CRLF kept. |
| `is null` / `using` | None. |
| File-scoped namespace | Done. |
| Missing `private` | `MAPPING_U`, `_y_min`, `_x_min`. |
| `sealed` (14) | `LinearInterpolator`. |
| Doc comments (16) | The `/*** Used to transform X from window til viewport. */` comment on `MAPPING_U` is a `///` summary. |
| Commented-out code (8) | `//double Y_HEIGHT = ...;` and `//MAPPING_V = ...;` in the constructor. |
| Nothing to change | Locals, braces, static (the public `Interpolate` overload is already static). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Util/Scaler.cs` (106 lines, public type)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; the stray empty statement in `int lfract = 0, source_pos = pixels_pos; ;` removed. CRLF and BOM kept. |
| `is null` | None. |
| `using` | `using System;` removed (implicit; `Array.Copy`). |
| File-scoped namespace | Done. |
| Typos (17) | In comments: `Remebers`, `possition`, `itterate`, `Itterates`, `remeber`. |
| Nothing to change | Locals (already at first use), missing `private`, braces, static (already). |

The public API is unchanged, including the misspelt method name `Rezise`. Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `CIO.cs` (486 lines; `public class CIO` and `internal class BufferCIO`)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, trailing whitespace. CRLF and BOM kept. |
| `is null` | None. |
| `using` | `System` and `System.IO` removed (implicit). |
| Locals | `i` and `v` of the two `Read(int n)` / `Read(uint n)` overloads: `int i; uint v; v = 0; for (i = ...)` is now `uint v = 0; for (int i = ...)`. |
| File-scoped namespace | Done. |
| Missing `private` | `_bytes`, `_s`, `_m`, `_size`, `BufferCIO._pos`, `BufferCIO._cio`. |
| `readonly` (10) | `_bytes` and `_s` (set in the constructors only). |
| Accessibility (9) / `sealed` (14) | `BufferCIO` `internal sealed`; its `_buffer` `internal` to `private` (no use outside). |
| Expression-bodied (13) | `CanSeek`. |
| Doc comments (16) | The `/** Used for culling the size of the stream ... */` comment on `_size` is a `///` summary. |
| Braces (12) | 2 bodies. |
| Nothing to change | Static, commented-out code, the public surface of `CIO` (unchanged). |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `CodingParameters.cs` (1 012 lines; public `ExtraOption`, `CompressionParameters`, `PPX`, internal `CodingParameters` and structs)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented; the one-line `WriteSOP` / `WriteEPH` accessors (`set { if (value) ... else ... }`) formatted over several lines. CRLF and BOM kept. |
| `is null` / `is not null` | 11 (`value` x6 in setters, `POC`, `comment`, `cp.matrice`, `cp.comment`, `ppm_markers`). |
| `using` | `System` removed (implicit; `Array.Resize`); `OpenJpeg.Internal` is used. |
| File-scoped namespace | Done. |
| Missing `private` | `CodingParameters.bitvector1`. |
| Braces (12) | 8 bodies. |
| Commented-out code (8) | `//var ca = ...` after the `return` in the `comment` getter, the `//comment = new byte[...]` loop in its setter, and the `//Debug code` `matrice` loop with its 3-line explanation in `UpdateMatrice`. |
| Accessibility | `mct_data` `public` to `internal` (done with `IntOrFloat`, see `MinorStructs.cs`). The rest of the public surface is unchanged. |
| `sealed` (14) | `CodingParameters` (internal; nothing derives from it). The public types stay unsealed (API). |
| Nothing to change | Locals, static. |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `CompressionInfo.cs` (419 lines; public sealed `CompressionInfo`, internal `Compression` / `Decompression` structs with delegates)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, doubled blank line after the licence; the `if (file is null) throw new ArgumentNullException(); ;` stray `;` removed. CRLF and BOM kept. |
| `is null` / `is not null` | 20 (`_mgr` and its `_error` / `_info` / `_warning` delegates, `cio`, `parameters`, `image`, `file`, `extra`, `_j2k`). |
| `using` | `System` and `System.IO` removed (implicit). |
| File-scoped namespace | Done. |
| Missing `private` | `_is_decompressor`, `_codec_format`, `_j2k`, `_jp2`, `_mgr`. |
| Redundant `else` (27) | 2, each after a branch that ends in `return`. |
| Expression-bodied (13) | `Version`, `IsDecompressor`. |
| Braces (12) | 3 bodies. |
| Nothing to change | Locals, static, commented-out code, `sealed` (already). |

The public surface is unchanged. Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `Constants.cs` (61 lines, public static class)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented. CRLF and BOM kept. |
| Nothing to change | Everything else: no `using`, no locals, all members already carry an explicit `public` / `internal`. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `DecodeParms.cs` (129 lines; internal `DecodeParms` struct, public `DecompressionParameters`)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, tab-indented doc comments, trailing whitespace. CRLF and BOM kept. |
| `using` | `System` removed (implicit). The alias `OPJ_UINT32 = System.Int32` removed and its 5 declarations are `int` (rule 32: this alias is `Int32`, not `UInt32`). The two casts `(OPJ_UINT32)value` in `Reduce` / `MaxLayer` became redundant and are gone. |
| File-scoped namespace | Done. |
| Attribute (37) | `[Flags()]` to `[Flags]`. |
| Commented-out code (8) | `//public LimitDecoding LimitDecoding = ...;` at the end of `DecodeParms`, with the `<summary>` that documented it. |
| Typos (17) | The internal field `DAx1` is `DA_x1` (never used). |
| Nothing to change | Locals, missing `private`, static, braces; the public surface is unchanged. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `Enums.cs` (881 lines; 22 enums and 5 small public classes)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented. CRLF and BOM kept. |
| `using` | `System` removed (implicit; `Flags`, `ArgumentException`). |
| File-scoped namespace | Done. |
| Top-level modifiers (36) | `JP2_STATE`, `JP2_IMG_STATE`, `JP2_Marker`, `CCP_CBLKSTY`, `CCP_QNTSTY`, `T2_MODE`, `T1`, `T1_TYPE`, `T1_NUMCTXS`, `T1_CTXNO` had none: now `internal enum`. |
| Attribute (37) | The 5 `[Flags()]` are `[Flags]`. |
| Nothing to change | Locals, null comparisons, braces, static; the public enums and classes are unchanged (public API). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `EventMgr.cs` (70 lines; public `MsgCallback` delegate and `EventMgr`)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented. CRLF and BOM kept. |
| `using` | `System.Diagnostics` is used (`Stopwatch`). |
| File-scoped namespace | Done. |
| Typos (17) | `convinience`. |
| Nothing to change | Locals, null comparisons, missing `private`, braces, static; the public surface is unchanged. |

Check: build of all 6 targets, 0 errors. Parity and tests: see "Verification runs".

### `J2K.cs` (7 971 lines)

| Rule | Result |
|---|---|
| Formatting | Roslyn formatter over all `#if` variants, namespace dedented, tab-indented parts, trailing whitespace, K&R braces. CRLF and BOM kept. |
| `is null` / `is not null` | About 70 in code (`_cstr_index`, `image`, `tcp`, `tlm`, `parameters.mct_data`, `img_comp_dest.data`, `handler.Handler`, `_cp.ppm_markers[i].Data`, `tp_index`, ...). Two `data != null` / `== null` inside `<remarks>` prose are unchanged. |
| `using` | `System`, `System.IO` removed (implicit); `System.Diagnostics` and `OpenJpeg.Internal` are used. The alias `OPJ_UINT32 = System.UInt32` is gone, about 110 uses are `uint` (32). |
| Locals | Joined or moved: `i` (12), `n_comps`, `j`, `tmp`, `tcp`, `indix`, `comp_x1`, `comp_y1`, `N_ppm`, ... Locals with a real initialiser stay (see "Script coverage"). |
| File-scoped namespace | Done. |
| Missing `private` | The fields, helper methods and nested types (`StrideFunc`, `J2KEncOrDec`, `EncParams`, `TlmTilePartInfo`, `TlmInfo`, `DecParams`). |
| Static (40) | `SetupMCTencoding`, `GetIMF_MaxNL`, `Initialise4K_poc`, `SetIMF_Parameters` (found once the others were static), `BubbleSort`, `ReadEOC`, `AddMCT`, `opj_get_encoding_packet_count`, `GetTpStride`, `GetDefaultStride`; and by hand `EndDecompress`: its two users (`CompressionInfo.cs` delegate construction, `JP2.cs` call) now say `J2K.EndDecompress`. |
| Redundant `else` (27) | About 7, nested ones handled in several passes (the script now keeps the outer edit and repeats). |
| Bare blocks (29) | About 8 removed (3 kept: name clash). The shadow check found none to keep for that reason. |
| Expression-bodied (13) | `ImageLength`, `CP`, `NumcompsToDecode`. |
| Commented-out code (8) | `//int _sot_start, _sod_start;` with its `<summary>`, `//Debug.Assert(avalible_data ...)`, `//_bcio.BufferPos += 2;`, the commented move-data loop in `Decode`, the `//if (img_comp_dest.w != width_dest ...)` clear (its explanation is one sentence now), `//int Zplm`, `//_cp.ppm_markers[Z_ppm].DataSize`, `//_cp.ppm_data_size`. |
| Braces (12) | The one-line `if` / `for` bodies. |
| Nothing to change | Accessibility (the `internal` members are used by `CompressionInfo.cs`, `JP2.cs`, `TileCoder.cs`), `sealed` (already). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `JP2.cs` (1 810 lines)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, tab-indented parts, trailing whitespace. CRLF and BOM kept. |
| `is null` / `is not null` | 33 (`_color`, `_color.jp2_pclr`, `_color.channel_definitions`, `image`, `handler`, `info`, `cinfo`, `parameters`, ...). |
| `using` | `System.IO` removed (implicit); `System.Diagnostics` and `OpenJpeg.Internal` are used. |
| Locals | `i` (4), `nr_channels`, `entries`, `cmap`, `channel_size`, `channel_sign`, `uc`, `top_k`, `src` joined or moved; the unused `typ` deleted. |
| File-scoped namespace | Done. |
| Missing `private` | The fields (`_state`, `_img_state`, ...) and helper methods. |
| Static (40) | `WriteJP`. |
| Redundant `else` (27) / bare blocks (29) | A few of each. |
| Expression-bodied (13) | `_has_jp2h`. |
| Commented-out code (8) | `//List<ProcedureDlg> _validation_list / _procedure_list` (fields, constructor, and the two `l_is_valid &= (...)` lines of the validation with their "make sure a ... list is present" comments), `//numcl = _cl.Length`. |
| Typos (17) | The private delegate `Handeler` is `Handler`; the local `hadler_misplaced` is `handler_misplaced`. |
| Braces (12) | The one-line bodies. |
| Nothing to change | Accessibility, `sealed` (already). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### `JPXImage.cs` (1 586 lines, public class and the private `BWrite` writer)

| Rule | Result |
|---|---|
| Formatting | Namespace dedented, tab-indented parts, trailing whitespace. CRLF and BOM kept. |
| `is null` / `is not null` | 22 (`cinfo` x13, `channel_definitions` x6, `color_info` x2, `d`). |
| `using` | `System`, `System.IO` removed (implicit); `System.Diagnostics`, `System.Text` and `OpenJpeg.Internal` are used. |
| Locals | `r` (4), `i` (4), `upb`, `offset`, `maxw`, `maxh`, `max`, `d0` / `d1` / `d2`, `rc`, `offx`, `loopmaxw`, `g`, `b` ... joined or moved. |
| File-scoped namespace | Done. |
| Static (40) | `TGAWriteHeader`. |
| Expression-bodied (13) | `IsIndexed`, `Width`, `Height`, `NumberOfComponents`, `Components`. |
| Commented-out code (8) | The two `//if (i == 723)` / `//if (i == 41187)` `i = i;` debug probes, each with its 3-line "C# - i == Bitmap images differs at position" explanation. |
| Braces (12) | The one-line bodies. |
| Nothing to change | Accessibility; the public surface is unchanged (`JPXImage` is public API, see candidates). |

Check: build of all 6 targets, 0 errors; the script finds nothing more to change. Parity and tests: see "Verification runs".

### Rule 40 across the files done so far

`tools/tidy/tidy.cs static` run on every file of `OpenJPEG/` (Internal and root). The files tidied so far have no candidate left.
No candidate is left in the files listed so far; the ones found by the first scan (`TileCoder.cs`, `J2K.cs`, `JP2.cs`, `JPXImage.cs`) were made static with their files.


## Verification runs

Run on the working tree with `BIO.cs`, `WBIO.cs`, `DWT.cs`, `ht_dec.cs`, `ImageComp.cs`, `Invert.cs`, `J2KTables.cs`, `MCT.cs` and
`MinorStructs.cs` tidied (plus `CodingParameters.cs`: `mct_data` made `internal`). Corpora and commands: see
`.claude/skills/verifying-jpx-decoder-changes`.

| Check | Result |
|---|---|
| Build, 6 targets (library), tests and benchmarks | 0 errors |
| Parity, 315 conformance + non-regression files: default, `PARITY_THREADS=1`, `PARITY_AREA=1`, `PARITY_FORCE_V512=1`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` | 315/315 in each |
| Parity, 1 404 PDF streams: default, `PARITY_THREADS=1`, `DOTNET_EnableAVX2=0` | 1 404/1 404 in each |
| Tests net8.0 | 289/294 (the 5 known failures) |
| Tests net472 | 289/294 (the 5 known failures) |

Second run, with every file listed under "Files" tidied (24 files, plus `CodingParameters.cs`, `Simd.cs` and `TileCompParams.cs` touched by
the repo owner). It first failed: parity 295/315 in the default mode (20 `CS_FAIL`: `IndexOutOfRangeException` in
`Tier2Coding.DecodePackets` and `decode=false`). Bisected by putting the committed version of one file back at a time:
only `PacketIterator.cs` made the failure go away; see the caution under the checklist. After the fix:

| Check | Result |
|---|---|
| Build, 6 targets (library), tests and benchmarks | 0 errors |
| Parity, 315 files: default, `PARITY_THREADS=1`, `PARITY_AREA=1`, `PARITY_FORCE_V512=1`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` | 315/315 in each |
| Parity, 1 404 PDF streams: default, `PARITY_THREADS=1`, `DOTNET_EnableAVX2=0` | 1 404/1 404 in each |
| Tests net8.0 | 289/294 (the 5 known failures) |
| Tests net472 | 289/294 (the 5 known failures) |

Third run, with the root-level files tidied as well (`TileCompParams.cs`, `BitWriter.cs`, `LinearInterpolator.cs`, `Scaler.cs`, `CIO.cs`,
`CodingParameters.cs`, `CompressionInfo.cs`, `Constants.cs`, `DecodeParms.cs`, `Enums.cs`, `EventMgr.cs`, `J2K.cs`, `JP2.cs`, `JPXImage.cs`):

| Check | Result |
|---|---|
| Build, 6 targets (library), tests and benchmarks | 0 errors |
| Parity, 315 files: default, `PARITY_THREADS=1`, `PARITY_AREA=1`, `PARITY_FORCE_V512=1`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` | 315/315 in each |
| Parity, 1 404 PDF streams: default, `PARITY_THREADS=1`, `DOTNET_EnableAVX2=0` | 1 404/1 404 in each |
| Tests net8.0 | 289/294 (the 5 known failures) |
| Tests net472 | 289/294 (the 5 known failures) |

## Performance check (speed and memory)

Whole-decode `DecodeBenchmarks` (14 decodes: 7 files x single / multi thread, net8.0, `short` job), the commit before the tidy-up
(`35a5191`) against the tidied tree (`b23eda7`), each tree in a checkout under a short path (`C:\jb\...`: with the repo path,
BenchmarkDotNet's per-job build folder exceeds the 260-character path limit and the build fails).

- **Memory: unchanged.** Allocated bytes per decode are identical on all 14 (for example 179.1 MB on `MOZILLA-10719-0_3`, 35.7 MB on
  `MOZILLA-10026-0_0`, 4.8 MB on `MOZILLA-8179-0_1`): +0.0% everywhere, in every run.
- **Speed: no regression.** First comparison (3 iterations): 12 of 14 rows within +-4%, two flagged (`PDFBOX-1845` single thread +10%,
  `MOZILLA-10026` multi thread +6.9%). Baseline run twice with 12 iterations (A/A): up to +5.7% from noise alone on single-thread rows.
  Tidied tree with 12 iterations, later session: 13 of 14 within +-3% of both baselines, `MOZILLA-10026` multi thread +15% (session drift:
  that row is a 4 ms decode).
- **Interleaved A/B on the flagged file** (`MOZILLA-10026`, base and tidied alternating, 4 rounds of 20 iterations, same session):

| Threads | Round 1 | Round 2 | Round 3 | Round 4 | Mean |
|---|---|---|---|---|---|
| multi (0) | +1.5% | +5.4% | +0.3% | -0.1% | +1.8% (4.02 / 4.16 / 4.12 / 4.15 ms base) |
| single (1) | +1.0% | +0.2% | +0.9% | -8.6% | -1.6% (one outlier round of base at 14.5 ms) |

  The differences are within the round-to-round scatter of the baseline itself; there is no consistent gap.

## Candidate items (not applied, to decide)

Found in `Internal/DWT.cs`:

- 4 blank lines directly after an opening `{` (for example before `v8dwt_interleave_h` in `decode_tile_97`): remove.
- `dwt_local` and `v4dwt_local` have mutable `internal` fields in a private class and a `public` `Clone()`; a narrower
  accessibility is not possible (`DWT` reads and writes the fields), so nothing to change.
- 31 `return;` statements: those that end a method or a branch that ends the method are redundant (not checked one by
  one).
- Lowercase and snake_case method names (`decode_tile_97`, `idwt53_v`, `v8dwt_interleave_h`) are kept to match upstream.
- `/* ... */` C comments inside methods copied from upstream stay (they drive the upstream sync).
- `var` or the explicit type for joined declarations: the script keeps the explicit type; `var` where the type is
  identical would match the hand edits.

Found in `Internal/ht_dec.cs`:

- `System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)` is
  written out in full on its two helpers; a `using System.Runtime.CompilerServices;` would shorten it (`MCT.cs`,
  `MyMath.cs` and `T1Luts.cs` do the same, `DWT.cs`, `MQCoder.cs` and `Tier1Coding.cs` use the `using`).
- `ht_dec.only_cleanup_pass_is_decoded` is a mutable `internal static` flag written by `Tier1Coding.cs` (a global that
  records an error already shown); `Tier1Coding.cs` compares it with `== false`.
- snake_case type names (`ht_dec`, `dec_mel`, `rev_struct`) kept to match upstream.

Found in `Internal/ImageComp.cs`:

- `public class ImageComp` could be `sealed` (nothing in the repo derives from it) but it is public API: a breaking change
  for any external subclass. Decide separately.
- `IsAlpha` has a getter and a setter with bodies; `get => alpha != 0; set => alpha = (ushort)(value ? 1 : 0);` would match
  the other properties. `Clone()` could be `=> MemberwiseClone()`.
- `alpha` is a `ushort` used as a flag, `bpp` is documented "Deprecated. Use prec instead" and `ImageComp()` is an
  empty `internal` constructor: left as they are (they mirror upstream `opj_image_comp_t`).

Found in `Internal/Invert.cs`:

- `System.Diagnostics.Debug.Assert` in `lupSolve` is written in full; `using System.Diagnostics;` would shorten it.
- Parameters in PascalCase (`SrcMatrix`, `DestMatrix`) next to snake_case ones, and `lup*` / `l*` Hungarian-style
  local names: kept to match upstream.
- `MatrixInversion` is `public` in an `internal` class (no effect); `static` helpers could be `internal`/`private` as
  they are.

Found in `Internal/J2KTables.cs`:

- `delegate bool J2K_Action(uint header_size)` has no accessibility modifier (internal by default, as a top-level type);
  the file keeps three unrelated types (`J2KTables`, `DecMstabent`, `J2kMarker`) and a delegate: one type per file (15)
  would split it.
- The `(b) => true` handler of the unknown marker allocates nothing (cached lambda) but ignores its argument: `_ => true`.

Found in `Internal/MCT.cs`:

- `MCT.Getnorm(int)` and `MCT.Getnorm_real(int)` are not called anywhere in the repository (library, tests, benchmarks);
  `GetMctNorms()` / `GetMctNormsReal()` return the whole tables instead. Dead code: delete, or keep for the upstream
  correspondence (`opj_mct_getnorm`, `opj_mct_getnorm_real`).
- The file holds `MCT`, two enums (`MCT_ELEMENT_TYPE`, `MCT_ARRAY_TYPE`) and two data classes: one type per file (15) would
  split it.
- `MctData` and `SimpleMccDecorrelationData` have public fields and `public object Clone() { return MemberwiseClone(); }`
  in an `internal` class; the same shape as `ImageComp`.

Found in `Internal/MinorStructs.cs`:

- Seventeen unrelated small types in one file: one type per file (15) would split it; keep it if the file name
  "MinorStructs" is the intent.
- `ArPtr<T>.Deref { get { return Data[Pos]; } set { Data[Pos] = value; } }`, `ArPtr.Clone()`, `ArPtr.Add()` and the
  one-line ctor bodies could be expression-bodied.
- The nested `Copy*` / `SetBytes` / `ToArray` switches repeat the same four-case pattern; a refactor, not a tidy-up.

Found in `Internal/MQCoder.cs`:

- The empty `internal MQCoder() { }` constructor (with a comment about "creating the ctxs struct") is the default
  constructor of an `internal` class: removable.
- `MQCState.Table` is assigned in a static constructor that must run after the `mcqints` initialiser (the comment says so);
  turning it into `Table = BuildTable()` as a field initialiser would depend on textual order of the two fields.
  Left as is.
- `MQCoder`, `MqcRegs` and `MQCState` share the file: one type per file (15).

Found in `Internal/MyMath.cs`:

- `MyMath.int_max`, `int_min` and `int_abs` are not called anywhere in the repository (library, tests, benchmarks);
  `Math.Max` / `Math.Min` / `Math.Abs` do the same. Dead code: delete, or keep for the upstream correspondence
  (`opj_int_max` ...). `int_ceildiv`, `int_floordivpow2` ... have one or two callers each.
- Two helpers write `[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]`
  in full; a `using System.Runtime.CompilerServices;` would shorten them (see `ht_dec.cs`).
- The members are `public` in an `internal` class (no effect); `internal` would be the narrowest honest modifier.

Found in `Internal/PacketIterator.cs`:

- `PIComp` and `PIResolution` (end of the file) are `internal class` with `internal` fields and nothing derives from them:
  `internal sealed class`.
- 181 locals with a real initialiser (`var tcp = cp.tcps[tile_no];`, `uint rx0 = MyMath.uint_ceildivpow2(...)`) are used only in an  inner block of the block that declares them. The script leaves them: moving them changes when the initialiser runs.  Decide whether to move the ones whose initialiser is plain arithmetic on locals (no calls, no indexing).
- Four calls got very long single-line `out uint x, out uint y, ...` argument lists after the inlining (about 170 to
  230 columns); wrapping them one argument per line, or `out var`, would be easier to read.
- The file keeps `PacketIterator` and the two small data classes: one type per file (15).

Found in `Internal/Parallelism.cs`:

- `RunAll<T>(IEnumerable<T>, int, Action<T>, Func<bool> keepGoing)` takes a non-nullable `keepGoing` that `Run` stores as
  `Func<bool>?` (the `IReadOnlyList` overload passes `null`): the signature is right as it is, noted for the deferred
  nullability check.
- The private nested struct `Range` hides `System.Range` inside the class (intentional, no effect).

Found in `Internal/ProgOrdChang.cs`:

- `public class ProgOrdChang` could be `sealed` (nothing derives from it) but it is public API, like `ImageComp`.
- It mixes `public` and `internal` fields (`resno0` ... `prg1` public, `layno0`, `prg`, `tx0` ... internal): the public ones
  are what a caller of `CodingParameters.ProgressionOrderChange` can set. Public fields in a public type are API: left.

Found in `Internal/SparseArray.cs`:

- The fields (`width`, `height`, `block_width` ..., `data_blocks`) are `public readonly` in an `internal sealed` class: `internal`
  would be the honest modifier (no effect). Methods are snake_case (`is_region_valid`, `read_or_write`) to match upstream.
- `read_or_write` exists twice (one for `int[]`, one for `float[]` buffers) with the same body apart from the element
  copy; a refactor, not a tidy-up.

Found in `Internal/TcdImage.cs`:

- Eleven data classes and a struct in one file: one type per file (15) would split it.
- `public TcdResolution()` (public constructor on an `internal` class) and many mutable `internal` fields; the `throw new
  NotImplementedException("Data.length != data_size")` in the `data_size` setter of `TcdTilecomp` is listed in `tidy-up.md`.

Found in `Internal/Tier1Coding.cs`:

- Declarations with a real initialiser, or with a comment on their line, were left where they are (see "Script coverage"
  above); the T1 passes declare many temporaries at the top of each pass, so the script's skipped list is long.
- The `NotImplementedException()` of the raw refinement pass and the `DEBUG` helpers (`DumpData`, `c:/temp/t1_dump.txt`) are
  in `tidy-up.md`.

Found in `Util/BitWriter.cs`:

- `BitWriter` is `WBIO` (`Internal/WBIO.cs`) without the "skip bit after 0xFF" rule and without a length limit; `Flush` always returns
  `true` although its comment says it can return false. A shared implementation would remove the duplicate.

Found in `Util/Scaler.cs`:

- `public static int[] Rezise(...)` is public API with a typo in its name (`Resize`); renaming it breaks callers, so it stays unless you decide a rename.
- `Array.Copy(...) //<-- todo: use BufferCopy instead` (an `int[]` copy, `Buffer.BlockCopy` would need the byte count).

Found in `CIO.cs`:

- `public class CIO` could be `sealed` but it is public API (like `ImageComp`).

Found in `CodingParameters.cs`:

- `CompressionParameters` is public API with many public fields named like upstream (`tcp_numlayers`, `cp_fixed_alloc`, ...) next to  the `NumberOfLayers`-style properties; they stay.

Found in `DecodeParms.cs`:

- The compiler's unused-field warnings (CS0649) for `DA_x0` ... `n_tile_to_decode`, `tile_index` and `flags` of `DecompressionParameters`:
  fields that nothing sets or reads (`DUMP_FLAG` is documented as having no effect); candidates for removal.
- `DecodeParms` (the struct) is only a field of `CodingParameters.dec`; its `reduce` / `layer` are public in an internal struct.

Found in `Enums.cs`:

- The comment `//Should be placed in another source file` above `TileSize`: the five small public classes (`TileSize`, `PrecinctSize`,
  `SubsamplingFactor`, `ImageOffset`, `CodeblockSize`) are public API and could be `sealed` (breaking for external subclasses).
- One type per file (15): 22 enums in one file is the usual way to keep them.

Found in `EventMgr.cs`:

- `EventMgr.obj_clock()` (upstream `opj_clock`) is not called anywhere in the repository: dead code, delete or keep for the upstream
  correspondence. `EventMgr` is public and could be `sealed` (API).

Found in `JPXImage.cs`:

- `public class JPXImage` could be `sealed` (API, like `ImageComp`).
