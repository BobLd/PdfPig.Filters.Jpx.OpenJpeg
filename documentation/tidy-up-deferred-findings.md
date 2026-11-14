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
