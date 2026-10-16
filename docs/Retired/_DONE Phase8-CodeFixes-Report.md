# Phase 8 — Completion Report

Companion to `_TODO Phase8-CodeFixes.md`. The build is clean (0 warnings, 0 errors). The full test suite was **not** run — please run it.

| Rows | Status | Remarks |
|---|---|---|
| 1-5 | DONE | Mis-encoded section signs matched by regex; saved as UTF-8 |
| 6 | ALREADY DONE | `Δ` was already correct |
| 7-33 | DONE | Row 34 and row 61 were already applied. Row 61: the doc's `...Int` in `nameof` is a typo, the method is `...Core`; code left as is |
| 35-45, 47-60, 62-66, 68-78, 81, 83-90 | DONE | Row 66 already had no stale reference |
| 42 | DONE | `ClampUtf8` is expression-bodied via `TapePrimitives.ClampToUtf8Bytes`; unused `using` removed |
| 55 | DONE | `LegacyDeserializer` is now `internal`; builds |
| 56 | DONE | Remarks changed; commented-out block deleted |
| 67 | NOT CHANGED (by decision) | `ITapeRecordEmitter` stays public per the REVIEWED note |
| 71, 72 | DONE | Empty `Constants` region and dead commented block removed |
| 79 | DONE (REVIEW) | `read != TapeHeaderBlock.Size`; latent, no behaviour change expected |
| 80 | REPORT ONLY | `End*` methods still `return true;` even if `WentOK` is false — decision pending |
| 82 | DONE | `ProduceNavigator_OLD` removed; no callers |
| 91, 92 | ALREADY DONE | `Design-FormatV21.md` exists; superseded docs already in `docs\Retired\` |
| 93 | ALREADY DONE | Primer already has the "On-tape format V2.1" section |
| 94-100 | DONE | Primer edits |
| 101-103 | DONE | `Design-Compression.md` §4, §5, §8, §10, §14 (paths mapped to `Compression/`, `Toc/`, `Agents/`) |
| 104 | PARTLY | No `Design-Format-v2` left in those docs. `Design-RepairMedia` (resolved note) and `Design-Encryption-pre-v2` (§7 superseded note; no `Design-Encryption.md` exists) done. `TapeLibNET\Getting-Started.md` layer diagram updated. **Not found:** the "known wart" text in `Design-TapeHeader.md` — do by hand |
| C.1 | DONE | `Design-FormatV21.md`: F32, TocSet row 14, §7.2 `TapeFrameWriter`, §7.5, new §9.7, §11 row, §12.1 golden, §14 row dropped. Also `Design-ScanMedia` §9.1, `Design-SetHeader-VerifiedWrite` §13, `Design-TapeFilePacker` §7.1 |
| 105 | DONE | Test comments mapped (§11.x to §12.x, §B.x to §7.3/§7.4, Appendix A to §5.6); mappings approximate — please skim |
| 106-108 | DONE | Renamed everywhere except `docs\Retired\`, the TODO file, and the "Supersedes" line in `Design-FormatV21.md` (intentional) |
| 109, 110 | DONE | Leftover `(R#)` / `(F1..F7)` mapped; `G8` in `TapeRecord.cs` changed to F14 |

**Pre-existing, untouched:** mojibake / U+FFFD in many test-project comments, unrelated to this TODO.
