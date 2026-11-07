---
name: sync-openjpeg-upstream
description: Use when porting changes from upstream C OpenJPEG (uclouvain/openjpeg, src/lib/openjp2) into the C# port in UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG, when a new OpenJPEG release or security fix is out, or when checking whether the port is behind upstream.
---

# Sync the C# port with upstream OpenJPEG

## Overview
The port mirrors `openjp2` function by function. Each C# function carries a tag like `2.5.3 - opj_j2k_read_sot`.
`OpenJPEG/UPSTREAM.md` records the last synced upstream commit and the C -> C# file map.
A change is ported only when the decoder still matches C bit for bit (REQUIRED: verifying-jpx-decoder-changes).

## Steps
1. Read the synced commit `<old>` from `UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG/UPSTREAM.md`.
2. List upstream changes (clone kept in `.reference/openjpeg` by `tools/reference/build.ps1`):
   `git -C .reference/openjpeg fetch && git -C .reference/openjpeg log --no-merges --stat <old>..origin/master -- src/lib/openjp2`
3. Classify each commit:
   - **Port**: decoder logic, bounds or overflow checks, marker parsing, colour handling, encoder fixes.
   - **Skip**: CMake/build, typos, C-only undefined-behaviour fixes (pointer arithmetic on NULL, memcpy of 0 bytes)
     with no observable behaviour, `opj_thread.c`, codec apps.
   - **SIMD** (`__SSE2__`, `__AVX2__`, `__ARM_NEON` blocks in `dwt.c`, `mct.c`): port as `System.Runtime.Intrinsics`
     code under `#if NET8_0_OR_GREATER`, with the scalar path kept for older targets.
4. For each commit to port: `git show <sha>`, find the C function in C# (search the C name), apply the same change,
   and update the function's version tag. Check that the C# code has the *pre*-change form first: the port is sometimes
   ahead or behind in unexpected places.
5. Rebuild the reference at the new commit and run parity: `./tools/reference/build.ps1 -Commit <new>`, then
   the steps in verifying-jpx-decoder-changes.
6. If decoder output changed on purpose, regenerate the test reference table (see `tools/README.md`).
7. Update `UPSTREAM.md`: new synced commit, and add each ported commit to the "ported" list.

## Pitfalls seen in this port
| Pitfall | What to do |
|---|---|
| C sets a field directly, C# calls a helper with side effects (`DecoderSetStrictMode` also disabled the TPsot fix) | Port the exact statement, not an equivalent-looking call |
| C `realloc` growth guarded by `if (count <= index)`; C# dropped the guard and shrank the array | Keep every guard around allocations |
| Early `return` in C seeks back the stream; C# returned without restoring `Pos` | Check every exit path restores state |
| `(OPJ_UINT32)` vs `int` casts | C# `uint` arithmetic wraps like C; keep the same types |
| C asserts | Become `if (...) return false` with an error message |
