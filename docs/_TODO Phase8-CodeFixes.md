# Phase 8 — Code and Document Fixes (machine-processable)

**For the agent applying this file:**

1. Process the rows **in order**, top to bottom. Part D (the global rename) must run after parts A–C.
2. `find` and `replace_with` are **literal** text. The surrounding backticks are not part of them; `(empty)` means
   "delete the found text". `⏎` marks a line break inside a value.
3. Each `find` must match **exactly once** in its file, unless the instructions say "all occurrences". If it does not
   match, apply `additional_instructions` by hand and report the row number.
4. Rows marked **REVIEW** change behaviour: apply them only after reading the instructions, and run the full test suite
   afterwards.
5. After all rows: `dotnet build`, then the full test suite. No test should change outcome.

---

## Part A — Section and rule references (mapping)

### A.1 Old section → new section (`Design-Format-v2` → `Design-FormatV21`)

| Old | New | Old | New |
|---|---|---|---|
| §1–§2 | §1–§2 | §6.1–§6.6 | §9.1–§9.6 |
| §3 (principles) | §3.1 (F1–F4) | §7.1 | §8.1 |
| §4.1–§4.6 | §4.1–§4.6 (unchanged) | §7.2 | §8.2 |
| §4.7 | §15 | §7.3 | §8.4 |
| §5.1–§5.5 | §5.1–§5.5 (unchanged) | §7.4 | §8.5 |
| §5.6 / Appendix A | §5.6 | §8.1 | §7.2 |
| §5.7 | §7.3, §7.4 | §8.2, §8.4 | §15 |
| §5.8 | §8.3 | §8.5 | §10.1 |
| §5.9 | §6 | §8.6 | §9.2 |
| §9 | §10 | §10 | §13 |
| §11 | §12 | §12, §13 | dropped (plan, file map) |
| §14 | §15 | §15 | §14 |
| App. B §B.1 | §7.1 | App. B §B.2 | §3.2 (G-rules) |
| App. B §B.3.x | §7.2, §7.4 | App. B §B.8.x | §5.1, §7.3 |

### A.2 Old rule → new rule

| Old | New | Old | New |
|---|---|---|---|
| R1 (legacy time conversion) | F25 | Review F1 (abandon on throw) | F13 |
| R3 (critical bit) | F7 | Review F2 (truncated CRC) | F14 |
| R5 (bounds) | F10 | Review F3 (ascending numbers) | G9 |
| R6 (TocHeader clamp) | F17 | Review F4 (absent group → null) | F31 |
| R7 (minor informational) | F6 | Review F5 (error context) | G8 |
| R9 (legacy TOC spill) | F24 | Review F6 (LimitExceeded) | F10 |
| G1–G7, G9–G11 (App. B) | G1–G10, renumbered: G8 (old G8 lost its text) → G8 = error context; old G9 → G9; old G10 (no read-ahead) → F11; old G11 → G10 | Review F7 (domain exceptions) | F14 |

**Code comments citing `G11`** → `G10`. **Citing `G10`** (no read-ahead) → `F11`. **Citing `G8`** → keep `G8` if the
context is error context, `F14` if it is exception mapping.

---

## Part B — Per-file fixes (product code)

| # | full_file_name | details | find | replace_with | additional_instructions |
|---|---|---|---|---|---|
| 1 | TapeLibNET\Format\TapeFormatException.cs | FormatErrorKind summary | `(Design-Format-v2 ยง8.1)` | `(Design-FormatV21 §7.2)` | The `§` is mis-encoded in this file (`ยง`). If the find fails, search `Design-Format-v2` and fix the garbled sign by hand. Save the file as UTF-8. |
| 2 | TapeLibNET\Format\TapePeekStream.cs | class summary | `(Design-Format-v2 ยง5.7)` | `(Design-FormatV21 §7.3)` | Same encoding defect as row 1. |
| 3 | TapeLibNET\Format\TapeRecord.cs | class summary | `(Design-Format-v2 ยง4.2)` | `(Design-FormatV21 §4.2)` | Same encoding defect as row 1. |
| 4 | TapeLibNET\Format\TapeRecordKind.cs | enum summary | `Kind registry (Design-Format-v2 ยง4.3)` | `Kind registry (Design-FormatV21 §4.3)` | Same encoding defect as row 1. |
| 5 | TapeLibNET\Format\TapeSampleCoder.cs | class summary, line 1 | `(Design-Format-v2 ┬º5.5)` | `(Design-FormatV21 §5.5)` | Mis-encoded `§` (`┬º`). Save as UTF-8. |
| 6 | TapeLibNET\Format\TapeSampleCoder.cs | class summary, line 2 | `<c>varuint ╬öActualWritten</c> (monotone) and <c>varint ╬öReportedRemaining</c>` | `<c>varuint ΔActualWritten</c> (monotone) and <c>varint ΔReportedRemaining</c>` | Mis-encoded `Δ` (`╬ö`). |
| 7 | TapeLibNET\Format\TapeFrame.cs | class summary | `(Design-Format-v2 §4.5, §8.1)` | `(Design-FormatV21 §4.5, F12)` | |
| 8 | TapeLibNET\Format\TapeRecordReader.cs | class summary | `(Design-Format-v2 §4.2, §8.1)` | `(Design-FormatV21 §4.2, F11)` | |
| 9 | TapeLibNET\Format\TapeRecordWriter.cs | class summary | `(Design-Format-v2 §4.2, §8.1)` | `(Design-FormatV21 §4.2, §7.2)` | |
| 10 | TapeLibNET\Format\ITapeRecord.cs | ITapeRecord summary | `(Design-Format-v2 §8.1)` | `(Design-FormatV21 §7.2)` | |
| 11 | TapeLibNET\Format\TapeCrc64Envelope.cs | class summary | `(Design-Format-v2 §4.5, §5.7)` | `(Design-FormatV21 §4.5, §7.3)` | |
| 12 | TapeLibNET\Format\TapeSchema.cs | FieldFlags summary | `(Design-Format-v2 §4.4, Appendix B §B.3.1)` | `(Design-FormatV21 §4.4, §7.4)` | |
| 13 | TapeLibNET\Format\TapeSchema.cs | TapeSchema summary | `(Design-Format-v2 §5.7, Appendix B §B.3.3)` | `(Design-FormatV21 §7.4)` | |
| 14 | TapeLibNET\Format\TapeSchema.cs | Read() | `// A known number: the critical bit is irrelevant (R3)` | `// A known number: the critical bit is irrelevant (F7)` | |
| 15 | TapeLibNET\Format\TapeSchema.cs | ValueField / GroupField ApplyDefault | `// absent optional group: null, not the previous value` | `// absent optional group: null, not the previous value (F31)` | |
| 16 | TapeLibNET\Format\TapeFieldReader.cs | class remarks | `ignore the critical bit when matching a known field (R3)` | `ignore the critical bit when matching a known field (F7)` | |
| 17 | TapeLibNET\Format\TapeFieldWriter.cs | class remarks | `emit it consistently per field number (R3)` | `emit it consistently per field number (F7)` | Also in the same remarks: the ascending check is G9 — append ` (G9)` after `an InvalidOperationException enforces it.` |
| 18 | TapeLibNET\Toc\TapeTOC.Format.cs | file header comment | `(Design-Format-v2 §5.1, Appendix B §B.8)` | `(Design-FormatV21 §5.1, §7.3)` | |
| 19 | TapeLibNET\Toc\TapeTOC.Format.cs | TocHeaderWire remarks | `identifies a TOC copy from that block alone (§5.9)` | `identifies a TOC copy from that block alone (§6, F17)` | |
| 20 | TapeLibNET\Toc\TapeTOC.Format.cs | TocFileEntryWire remarks | `ONE instance serves a whole TOC on write and on read (§B.8.4)` | `ONE instance serves a whole TOC on write and on read (F31)` | |
| 21 | TapeLibNET\Toc\TapeTOC.Format.cs | TocFileEntryWire.From | `CreationTime = d.CreationTime;          // UTC in memory (§8.5)` | `CreationTime = d.CreationTime;          // UTC in memory (F20)` | |
| 22 | TapeLibNET\Toc\TapeTOC.Format.cs | EstimateSerializedSize summary | `Conservative 2.1 TOC footprint of this entry (§9)` | `Conservative 2.1 TOC footprint of this entry (§10.3)` | |
| 23 | TapeLibNET\Toc\TapeTOC.Format.cs | c_maxPreallocFiles | `never drives allocation beyond this (R5)` | `never drives allocation beyond this (F10)` | |
| 24 | TapeLibNET\Toc\TapeTOC.Format.cs | Wire summary | `Wire form of the set record (§5.1, §B.8.2).` | `Wire form of the set record (§5.1).` | |
| 25 | TapeLibNET\Toc\TapeTOC.Format.cs | Wire.Validate comment | `// Cross-field rules - run on write (programming error) and on read (CrossCheck), G11` | `// Cross-field rules - run on write (programming error) and on read (CrossCheck), G10` | |
| 26 | TapeLibNET\Toc\TapeTOC.Format.cs | WriteBatches catch | `writer.AbandonRecord();     // keep the writer usable (same rule as F1)` | `writer.AbandonRecord();     // keep the writer usable (F13)` | |
| 27 | TapeLibNET\Toc\TapeTOC.Format.cs | ReadBatch catch | `// Domain conversion refused the data (G8): report it as a format error with context` | `// Domain conversion refused the data (F14): report it as a format error with context` | |
| 28 | TapeLibNET\Toc\TapeTOC.Format.cs | c_maxPreallocSets | `/// <summary>Pre-allocation cap for the set list on load (R5).</summary>` | `/// <summary>Pre-allocation cap for the set list on load (F10).</summary>` | |
| 29 | TapeLibNET\Toc\TapeTOC.Format.cs | TryPeek remarks — stale phase | `not recognized here (Phase 5 reports it as` | `not recognized here (Scan Media reports it as` | |
| 30 | TapeLibNET\Toc\TapeTOC.cs | TapeFileDescriptor summary | `(Design-Format-v2 §8.5)` | `(Design-FormatV21 §10.1, F20)` | |
| 31 | TapeLibNET\Toc\TapeTOC.cs | TapeTOC.CopyFrom — truncated comment | `        // Preserve⏎` | `        // Preserve the series identity: every TOC load copies a⏎` | The comment block reads `// Preserve` / `// freshly deserialized TOC into the live one via CopyFrom, so dropping this line would` / `// silently strip the id from every loaded TOC.` — only the first line changes. |
| 32 | TapeLibNET\Toc\TapeTOC.cs | CreateHeader remarks — stale | `once at format / on a fresh volume and never rewritten. Only ever produced for single-headerPartition` | `once at format / on a fresh volume and never rewritten. Placement and partition are supplied by the caller,` | Then replace the following line `///  (TOC-in-set) media, hence <see cref="TapeTocPlacement.InSet"/>.` with `///  which knows the layout — the TOC does not.` |
| 33 | TapeLibNET\Headers\TapeFileHeader.cs | first line | `// Save as: TapeLibNET/TapeFileHeader.cs⏎` | (empty) | Stale download-path line. |
| 34 | TapeLibNET\Headers\TapeFileHeader.cs | From() | `CreationTime = d.CreationTime,          // UTC in memory (§8.5)` | `CreationTime = d.CreationTime,          // UTC in memory (F20)` | |
| 35 | TapeLibNET\Headers\TapeFileHeader.cs | class remarks | `the same rule as the TOC file entry.` | `the same rule as the TOC file entry (F21).` | |
| 36 | TapeLibNET\Headers\TapeFileHeader.cs | Validate comment | `// Cross-field rules - run on write (programming error) and on read (CrossCheck), G11` | `// Cross-field rules - run on write (programming error) and on read (CrossCheck), G10` | |
| 37 | TapeLibNET\Headers\TapeMediaHeader.cs | first line | `// Save as: TapeLibNET/TapeMediaHeader.cs⏎` | (empty) | |
| 38 | TapeLibNET\Headers\TapeMediaHeader.cs | class remarks | `a legacy media header stays legacy (§3.3)` | `a legacy media header stays legacy (F3)` | |
| 39 | TapeLibNET\Headers\TapeMediaHeader.cs | region title — stale | `#region *** Legacy read — removed in Phase 6 ***` | `#region *** Legacy read ***` | |
| 40 | TapeLibNET\Headers\TapeSetHeader.cs | region title — stale | `#region *** Legacy read — removed in Phase 6 ***` | `#region *** Legacy read ***` | |
| 41 | TapeLibNET\Headers\TapeHeader.cs | CreatedUtc summary | `When this header was created, UTC (§8.5).` | `When this header was created, UTC (F20).` | |
| 42 | TapeLibNET\Headers\TapeHeader.cs | ClampUtf8 — surrogate-unsafe, O(n²) | `protected static string? ClampUtf8(string? name, int maxBytes)` | `protected static string? ClampUtf8(string? name, int maxBytes)` | Replace the method BODY with `=> string.IsNullOrEmpty(name) ? null : TapePrimitives.ClampToUtf8Bytes(name, maxBytes);` (expression-bodied). The current body trims char by char and can split a surrogate pair; `ClampToUtf8Bytes` cannot. Remove `using System.Text;` if now unused. Existing tests must stay green (clamped names are byte-identical for BMP text). |
| 43 | TapeLibNET\Headers\TapeHeaderBlock.cs | Frame() summary — stale | `magic first; the calibration header still legacy until Phase 6), remainder zero padding, ignored on read.` | `magic first), remainder zero padding, ignored on read.` | |
| 44 | TapeLibNET\Headers\TapeHeaderBlock.Identify.cs | IdentifiedBlock / IdentifyBlock docs | `(Design-Format-v2 §5.9)` | `(Design-FormatV21 §6)` | **All occurrences** (2). |
| 45 | TapeLibNET\Headers\TapeHeaderBlock.Identify.cs | IdentifyBlock remarks | `A record damaged BEHIND its magic is ours, damaged — never a phantom TOC.` | `A record damaged BEHIND its magic is ours, damaged — never a phantom TOC (F23).` | |
| 46 | TapeLibNET\Headers\TapeFramer.cs | class remarks — stale plan | `///  The move-only folder commit folds this class into <see cref="TapeFrame"/> and <see cref="TapeFrameStatus"/> into` | `///  Kept as the header-specific façade over <see cref="TapeFrame"/>; <see cref="TapeFrameStatus"/> lives in` | The next line `///  <c>Format/</c>.` stays and completes the sentence. |
| 47 | TapeLibNET\Legacy\LegacyTime.cs | first line | `// Save as: TapeLibNET/Legacy/LegacyTime.cs  (replaces the previous version: adds the zone-explicit overload)⏎` | (empty) | |
| 48 | TapeLibNET\Legacy\LegacyTime.cs | class summary | `///  §7.1, §8.5).` | `///  §8.1, F25).` | The preceding line ends with `(Design-Format-v2` — handled by Part D. |
| 49 | TapeLibNET\Legacy\LegacyTocReader.cs | class remarks | `(Design-Format-v2 §7.2)` | `(Design-FormatV21 §8.2)` | |
| 50 | TapeLibNET\Legacy\LegacyTocReader.cs | MaxInMemoryBytes | `bigger ones spill to a temp file (R9).` | `bigger ones spill to a temp file (F24).` | |
| 51 | TapeLibNET\Legacy\LegacyTocReader.cs | Buffer() comment | `beyond that in a delete-on-close temp file (R9)` | `beyond that in a delete-on-close temp file (F24)` | |
| 52 | TapeLibNET\Legacy\LegacyVirtualMediaState.cs | class summary | `version ≤ 0x0100; Design-Format-v2-AppendixA §A.5).` | `version ≤ 0x0100; Design-FormatV21 §5.6).` | |
| 53 | TapeLibNET\Legacy\LegacyCheckpointReader.cs | class summary | `(Design-Format-v2 §6.5)` | `(Design-FormatV21 §9.5)` | |
| 54 | TapeLibNET\Legacy\LegacyIdentify.cs | IdentifyBlock summary — mis-encoded dash | `a damaged legacy record �` | `a damaged legacy record —` | The garbled character is one replacement glyph followed by two spaces; if the find fails, fix the line `Identifies one LEGACY block (no 2.1 magic): ... a damaged legacy record ? or foreign` by hand. Save as UTF-8. |
| 55 | TapeLibNET\Legacy\LegacyDeserializer.cs | visibility — doc says frozen/internal | `public class LegacyDeserializer(Stream rstream)` | `internal class LegacyDeserializer(Stream rstream)` | Callers are internal (`LegacyHeaderReader`, `ConstructBody` methods) and the test project has `InternalsVisibleTo`. If a PUBLIC member still exposes the type, the build says which — report it instead of reverting. |
| 56 | TapeLibNET\Legacy\LegacyDeserializer.cs | class remarks — stale | `/// <remarks><c>ITapeSerializable</c> not used, not offered past V2.1 Phase 6.</remarks>` | `/// <remarks>Read-only; the write half lives in the test project (F1).</remarks>` | Also delete the commented-out `Deserialize<TClass>` / `Deserialize<TList, TValue>` block at the end of the class (from `/*` after `ValidateSignature(out ushort version)` to the matching `*/`). |
| 57 | TapeLibNET\Agents\TapeFileBackupAgent.cs | EnsureSetFormat summary | `one set, one format (Design-Format-v2 §6.1)` | `one set, one format (F4)` | |
| 58 | TapeLibNET\Agents\TapeFileRestoreAgent.cs | RestoreFilesFromCurrentSet summary — typo | `-- thepacker seeks` | `-- the packer seeks` | |
| 59 | TapeLibNET\Agents\TapeFileRestoreAgent.cs | ResumeRestoreFromAnotherVolume — typo | `// update the volme` | `// update the volume` | |
| 60 | TapeLibNET\Agents\TapeFileRestoreAgent.cs | ResumeRestoreFromAnotherVolume — end comment | `} // ResumeRestoreOnAnotherVolume()` | `} // ResumeRestoreFromAnotherVolume()` | |
| 61 | TapeLibNET\Agents\TapeFileRestoreAgent.cs | RestoreAllFilesFromCurrentSetCore — **DONE**: missing NotifySetEnd | `            m_logger.LogWarning("Failed to begin reading content in {Method}",⏎                nameof(RestoreAllFilesFromCurrentSet));⏎            return false;` | `            NotifySetEnd(fileNotify);⏎            m_logger.LogWarning("Failed to begin reading content in {Method}",⏎                nameof(RestoreAllFilesFromCurrentSetInt));⏎            return false;` | Behaviour fix: every `NotifySetStart` needs its `NotifySetEnd` (the list variant `RestoreFilesFromCurrentSet(List<…>)` already does this). Without it a host's per-set progress scope stays open after a failed positioning. Run the restore and notification tests (`TapeSetNotificationTests`, `StatisticsTests`). |
| 62 | TapeLibNET\Agents\TapeAgentBase.cs | class summary — stale | `///  set deletion, and the <see cref="ITapeFileNotifiable"/> notification wrappers.` | `///  media / set header I/O, set verification, and the <see cref="ITapeFileNotifiable"/> notification wrappers.` | |
| 63 | TapeLibNET\Agents\TapeAgentBase.cs | class summary — subclasses | `/// <para>Subclasses: <see cref="TapeFileBackupAgent"/> (backup), <see cref="TapeFileRestoreBaseAgent"/>` | `/// <para>Subclasses: <see cref="TapeFileBackupAgent"/> (backup), <see cref="TapeSetAgent"/> (set deletion), <see cref="TapeFileRestoreBaseAgent"/>` | |
| 64 | TapeLibNET\Agents\TapeAgentBase.cs | c_fixedTOCBlockSize — duplicated constant | `private const uint c_fixedTOCBlockSize = 16 * 1024; // 16 KiB` | `private const uint c_fixedTOCBlockSize = TapeHeader.FixedHeaderBlockSize; // 16 KiB — TOC and headers share one block size (§5.7)` | `TapeLibNET.Headers` is already imported. |
| 65 | TapeLibNET\Agents\TapeAgentBase.cs | BackupTOC tail comment | `(the service logs it, §8.6).` | `(the service logs it, §9.2, F28).` | |
| 66 | TapeLibNET\Agents\TapeRecordEmitter.cs | interface summary | `(Design-Format-v2 §3, §7.3)` | `(Design-FormatV21 F1, §8.4)` | |
| 67 | TapeLibNET\Agents\TapeRecordEmitter.cs | **REVIEW**: visibility contradicts its own doc ("Internal: only TapeLibNET and its test project…") | `public interface ITapeRecordEmitter` | `internal interface ITapeRecordEmitter` | The agent property `RecordEmitter` is already internal; the test project sees internals. If a public member in TapeLibNET exposes the interface, the build says which — make that member internal too, or revert this row and instead fix the doc comment to "Public, but only replaceable through the internal `TapeAgentBase.RecordEmitter`". |
| 68 | TapeLibNET\Compression\TapeCompressionStream.cs | CompressionFilterStream comment — wrong | `// ZstdNet.CompressionStream owns its write semantics; create lazily so the` | `// ZstdNet.CompressionStream owns its write semantics; created together with this stream.` | Delete the following line `//  instance is only allocated when the first byte is written.` |
| 69 | TapeLibNET\Calibration\TapeCalibrator.cs | ReadRecord summary | `(Design-Format-v2 §6.5: a trail may mix legacy and 2.1 records)` | `(Design-FormatV21 §9.5: a trail may mix legacy and 2.1 records)` | |
| 70 | TapeLibNET\Virtual\VirtualMediaStateRecord.cs | class summary | `(Design-Format-v2-AppendixA §A.4)` | `(Design-FormatV21 §5.6, F27)` | |
| 71 | TapeLibNET\Virtual\VirtualTapeMedia.cs | unused constant | `    private const ushort StateVersion = 0x0100;⏎` | (empty) | If the `Constants` region is then empty, delete the region too. |
| 72 | TapeLibNET\Virtual\VirtualTapeMedia.cs | VirtualTapeBlock — dead code | `    /// <summary>Serializes this virtual block.</summary>` | (empty) | Delete the whole commented-out block `/* … SerializeTo … ConstructFrom … */` at the end of `VirtualTapeBlock` (from the `/*` line to its `*/` line). |
| 73 | TapeLibNET\Virtual\VirtualTapeDriveBackend.cs | LoadMedia — rule reference | `// Never create or truncate over metadata we cannot read` | `// Never create or truncate over metadata we cannot read (F26)` | |
| 74 | TapeLibNET\Services\TapeServiceBase.Toc.cs | region title | `#region TOC persistence (Design-Format-v2 §6.2, §8.6)` | `#region TOC persistence (Design-FormatV21 §9.2, F28)` | |
| 75 | TapeLibNET\Services\TapeServiceBase.Toc.cs | SaveTocCore remarks | `upgrades to format 2.1 on its first write (Design-Format-v2 §6.2)` | `upgrades to format 2.1 on its first write (Design-FormatV21 §9.2, F3)` | |
| 76 | TapeLibNET\Services\TapeServiceBase.Scan.cs | RecoverTocCore — **bug**: stray `$` prints literally | `LogInfo($"Recovering the TOC ${where}...");` | `LogInfo($"Recovering the TOC {where}...");` | Inside an interpolated string `${where}` renders as `$at block 5…`. |
| 77 | TapeLibNET\Services\TapeServiceBase.Scan.cs | RecoverTocCore — same bug | `LogErr($"Couldn't recover the TOC ${where}: {read.ErrorMessage}");` | `LogErr($"Couldn't recover the TOC {where}: {read.ErrorMessage}");` | |
| 78 | TapeLibNET\Services\TapeServiceBase.Scan.cs | RecoverTocCore — same bug | `$"The TOC ${where} belongs to a different series than the " +` | `$"The TOC {where} belongs to a different series than the " +` | |
| 79 | TapeLibNET\Media\TapeStreamManager.cs | ReadBomHeaderBlock — **REVIEW**: inconsistent length check | `        if (read != buffer.Length)⏎        {⏎            SyncErrorFrom(Drive);⏎            Navigator.ResetContentSet();          // half-read header — unknown position⏎            return -1;` | `        if (read != TapeHeaderBlock.Size)⏎        {⏎            SyncErrorFrom(Drive);⏎            Navigator.ResetContentSet();          // half-read header — unknown position⏎            return -1;` | `ReadSetHeaderBlock` compares against the block size, and its comment explains why: an over-sized buffer is legitimate. Today every caller passes exactly `TapeHeaderBlock.Size`, so this is latent. |
| 80 | TapeLibNET\Media\TapeStreamManager.cs | EndWriteTOC / EndReadTOC / EndWriteContent / EndReadContent — **DONE** | (none) | (none) | All four end with `return true;` even when `WentOK` is false, so `EndReadWrite()` callers (`if (!Manager.EndReadWrite())`) never see a teardown failure. Decide whether to `return WentOK;` — it changes behaviour on every teardown path. Report only. |
| 81 | TapeLibNET\Media\TapeNavigator.cs | DefaultTOCCapacity — duplicated summary line | `    /// <summary>Default TOC capacity used when no override is specified.</summary>⏎    /// <summary>Default TOC capacity used when no override is specified.</summary>` | `    /// <summary>Default TOC capacity used when no override is specified.</summary>` | |
| 82 | TapeLibNET\Media\TapeNavigator.cs | dead code | `    public static TapeNavigator? ProduceNavigator_OLD(TapeDrive drive, bool useTOCMark = true)` | (empty) | Delete the whole method `ProduceNavigator_OLD` including its doc comment. `ProduceNavigator` builds on `TapeMediaLayout.Predict` now. Verify with `git grep ProduceNavigator_OLD` that nothing calls it. |
| 83 | TapeLibNET\Media\TapeNavigator.cs | TapeNavigatorTOCInPartition.MoveToBomHeader — log text | `LogErrorAsDebug($"MoveToMediaHeader(forWrite={forWrite}) invalid` | `LogErrorAsDebug($"MoveToBomHeader(forWrite={forWrite}) invalid` | |
| 84 | TapeLibNET\Scan\MediaScanMap.cs | first line | `// Save as: TapeLibNET/Scan/MediaScanMap.cs⏎` | (empty) | |
| 85 | TapeLibNET\Scan\MediaScanMap.cs | ToJson summary | `Harvested TOCs are saved separately (§4.3).` | `Harvested TOCs are saved separately (Design-ScanMedia §8.3).` | |
| 86 | TapeLibNET\Scan\ScanMediaOptions.cs | HarvestTocCopies summary | `Deserialize TOC copies, not merely detect them. See §4.3.` | `Deserialize TOC copies, not merely detect them. See Design-ScanMedia §8.3.` | |
| 87 | TapeLibNET\Scan\ScanMediaOptions.cs | InspectCalibrationTrail summary | `checkpoint-derived run state (§4.4).` | `checkpoint-derived run state (Design-ScanMedia §4.5).` | |
| 88 | TapeLibNET\Scan\TapeScanner.Identify.cs | IdentifyBomFragment remarks | `From the second fragment on the scanner reads raw (§4.2)` | `From the second fragment on the scanner reads raw (Design-ScanMedia §3.1)` | |
| 89 | TapeLibNET\Scan\TapeScanner.Identify.cs | ReadIdentificationBlock remarks | `<b>§4.2, and not optional.</b>` | `<b>Design-ScanMedia §4.6, and not optional.</b>` | |
| 90 | TapeLibNET\Scan\TapeScanner.Identify.cs | InspectCalibrationTrail summary | `calibrator rather than duplicating its walk (§4.4).` | `calibrator rather than duplicating its walk (Design-ScanMedia §4.5).` | |

---

## Part C — Documents

| # | full_file_name | details | find | replace_with | additional_instructions |
|---|---|---|---|---|---|
| 91 | docs\Design-FormatV21.md | new file | (none) | (none) | Add the delivered `Design-FormatV21.md`. |
| 92 | docs\Design-Format-v2.md, docs\Design-Format-v2-AppendixA-VirtualMedia.md, docs\Design-Format-v2-AppendixB-CoreTypes-Revised.md, docs\Design-Format-v2-AppendixB-Review-R1-R5.md | superseded | (none) | (none) | `git mv` each into `docs\archive\` (or `git rm` — history keeps them). Delete any older Appendix B variants (`-CoreTypes.md`, `-Revised.md`, `-Rulings.md`) the same way. |
| 93 | docs\TapeNET-Context-Primer.md | new format section | `##### Scan Media — TOC-less media survey` | `#### On-tape format 2.1⏎…⏎⏎##### Scan Media — TOC-less media survey` | Insert the delivered `Primer-FormatV21-Section.md` **before** the Scan Media heading (i.e. after the media-header section). The `…` stands for the section's full text. |
| 94 | docs\TapeNET-Context-Primer.md | media header — stale "watch-item" | `**Watch-item:** the header carries no trailing filemark, so beginning-of-content is a mid-data write.` | `**Trailing filemark:** the media header is followed by one filemark, so beginning-of-content is a post-mark write position on every drive.` | Delete the rest of that paragraph (the sentences about AIT-2, DLT-V4, the strict-write mode and "should a strict drive ever appear") — the fix it describes has shipped (`TapeHeaderBlock.WritesTrailingMark`). |
| 95 | docs\TapeNET-Context-Primer.md | media header — stale API name | `TapeFramer.Unpack<TapeHeader>` | `TapeFramer.TryUnpackHeader` | |
| 96 | docs\TapeNET-Context-Primer.md | class hierarchy — stale name | `TapeFileAgent (base)              — TOC backup/restore, Notify* wrappers, TapeFileStatistics` | `TapeAgentBase (base)              — TOC backup/restore, header I/O, set verification, Notify* wrappers` | Also add a line `  TapeSetAgent                    — set deletion (verified)` under it, and delete the NOTICE paragraph above the hierarchy (the rename is history now). |
| 97 | docs\TapeNET-Context-Primer.md | tests table — deleted class | `BufferedTapeStreamTests` | (empty) | Delete the whole table row (`BufferedTapeStreamTests` / "Buffered stream read/write round-trips"). |
| 98 | docs\TapeNET-Context-Primer.md | compression — stale serialization bullet | `**Forward-compatible serialization** — the codec byte is appended after existing` | `**Codec on tape** — 2.1 file bodies start with a one-byte codec prefix that must match the TOC's codec; legacy sets carry the codec in the TOC only (Design-FormatV21 §5.3).` | Delete the rest of the old bullet (`TapeFileInfo fields; older tapes without it are read back as Stored by default.`). |
| 99 | docs\TapeNET-Context-Primer.md | test counts | `TapeLibNET.Tests (xUnit, 2,470+ tests` | `TapeLibNET.Tests (xUnit, ~5,000 tests` | Also in the TapeLibNET.Tests section: `xUnit test project with **2,470+ tests**` → `**~5,000 tests**`, and the profiles line: three profiles → four (`Setmarks, Partitions, SeqFilemarks, FilemarksOnly`). |
| 100 | docs\TapeNET-Context-Primer.md | "What's Been Implemented" | `- ✅ Backup set headers — verified set navigation and verified destructive writes:` | `- ✅ On-tape format 2.1 — self-describing tagged records with CRC-64, legacy read-only forever, mixed media, Scan Media integration, 2.1 goldens. Full design: docs/Design-FormatV21.md.⏎- ✅ Backup set headers — verified set navigation and verified destructive writes:` | |
| 101 | docs\Design-Compression.md | §4 — stale serialization | `Both are serialized as consecutive int32 fields in SerializeTo / ConstructFrom, appended` | `Both are TocSet fields 9 and 10 in format 2.1 (Design-FormatV21 §5.1); legacy TOCs carry them as two int32 values in layout B,` | Replace the rest of that sentence with `and layout-A TOCs (written before compression) read back as None / Default.` |
| 102 | docs\Design-Compression.md | §5 — stale serialization | `TapeFileInfo.SerializeTo appends (byte)Codec after SizeOnTape.` | `Codec is TOC file-entry field 7 (critical) in format 2.1, and is also written as the first byte of every 2.1 file body (Design-FormatV21 §5.3).` | Delete the following sentences about `ConstructFrom` and `EstimateSerializedSize adds 1 byte`. |
| 103 | docs\Design-Compression.md | §8 / §10 — stale header layout | `TapeFileInfo.DeserializeAndCheckHeaderFrom` | `the 2.1 file header frame` | In §10 replace the layout block with `[2.1 file header frame][codec u8][file body — Stored or ZSTD]` and note that `SizeOnTape` covers all three. File paths in §14 moved into `Compression/`, `Toc/`, `Agents/` folders — update them. |
| 104 | docs\Design-TapeHeader.md, docs\Design-TapeFilePacker.md, docs\Getting-Started.md, docs\Design-Encryption.md, docs\Design-RepairMedia.md | pointers | `Design-Format-v2` | `Design-FormatV21` | All occurrences. Additionally: Design-TapeHeader — mark the "known wart" (signature behind the length) as resolved by F5; Design-TapeFilePacker — the per-file header is the 2.1 frame + codec byte; Getting-Started — the serializer layer is `TapeLibNET/Format/` (schemas), `TapeSerializer` lives in the tests; Design-Encryption — its §7 is superseded by Design-FormatV21 §13; Design-RepairMedia — the strict TapeSetTOC signature problem is resolved. These files were not reviewed here; change only these points. |

---

### Part C.1 — Document changes related to introduction of `ClosedByEom` set trait

Document |	Edit
|---|---|
Design-FormatV21.md | §3.1	F32: On TOC-in-partition media the last set of a volume may end at end-of-media without its closing setmark; the TOC records it (ClosedByEom), and end-relative navigation on that volume counts one setmark fewer. Never written, and never honoured, on TOC-in-set media.
Design-FormatV21.md | §5.1 TocSet table	row 14 · ClosedByEom · bool · false — F32; optional, not critical
Design-FormatV21.md | new §9.7 Full volumes on partitioned media	why the mark may be missing, why it's recorded rather than recovered, the −1 shift in the agent, the full-volume guard on append, restore tolerating end of data, the scan judgement
Design-FormatV21.md | §11	row: A full partitioned volume shifts every end-relative index by one; the agent translates, the navigator never knows
Design-FormatV21.md | §12.1	the new golden toc-closed-by-eom.v21.bin
Design-ScanMedia.md | §9.1	an unclosed last set that a recovered TOC records as closed by end-of-media (partitioned media) reads "runs to the end of the medium — the volume is full", with no review advice. The live line on partitioned media is neutral (Info) for any unclosed set.
Design-SetHeader-VerifiedWrite.md | §13	On a full TOC-in-partition volume the last set has no closing setmark (F32); the agent's end-relative translation accounts for it, so no drift arises and no recovery runs.
Design-FormatV21.md | §14	drop the row "TapeFrame.Pack allocates per call" — resolved
Design-FormatV21.md | §7.2 table	add TapeFrameWriter — reusable Record ‖ CRC-64 builder; the static TapeFrame methods run through a temporary one
Design-FormatV21.md | §7.5	"RecordEmitter defaults to the agent's own TapeRecordEmitter21, created and disposed with the agent; it owns a TapeFrameWriter and one file-header wire object, so writing a file header allocates nothing. Setting null returns to it; a borrowed emitter is never disposed."
Design-TapeFilePacker.md | §7.1	One file: "the per-file header through the agent's own emitter — no allocation per file"

---

## Part D — Global rename (run last)

| # | full_file_name | details | find | replace_with | additional_instructions |
|---|---|---|---|---|---|
| 105 | all `*.cs` in TapeLibNET, TapeLibNET.Tests, TapeConNET, TapeWinNET, TapeServiceNET | test comments citing removed sections | `Design-Format-v2 §1` | (by mapping) | Before the plain rename: `git grep -n "Design-Format-v2 §1[1-5]"` and `"§B\."` / `"§A\."`. Map each hit with Part A (e.g. `§11.5` → `§12.2`, `§12 Phase N` → delete the reference). |
| 106 | all `*.cs` and `*.md` in the repository | document name | `Design-Format-v2-AppendixA` | `Design-FormatV21` | All occurrences; the section after it is mapped by Part A.1 (`§A.x` → `§5.6`). |
| 107 | all `*.cs` and `*.md` in the repository | document name | `Design-Format-v2-AppendixB` | `Design-FormatV21` | All occurrences; `§B.x` → Part A.1. |
| 108 | all `*.cs` and `*.md` in the repository | document name | `Design-Format-v2` | `Design-FormatV21` | All occurrences, **after** rows 1–107. Then `git grep -n "Design-Format-v2"` must return nothing outside `docs\archive\`. |
| 109 | all `*.cs` in TapeLibNET | leftover rule names | `(R3)`, `(R5)`, `(R6)`, `(R7)`, `(R9)` | `(F7)`, `(F10)`, `(F17)`, `(F6)`, `(F24)` | `git grep -n -E "\(R[0-9]+\)" -- TapeLibNET` — map each by Part A.2. Do NOT touch `RM-n` (RepairMedia), `SH-n`, `SM-n`, `INV-n`. |
| 110 | all `*.cs` in TapeLibNET | leftover review-fix names | `(F1)`…`(F7)` in comments written before this change | by Part A.2 | `git grep -n -E "same rule as F[0-9]\|\(F[1-7]\)"` — only hits that predate this file (they refer to the R1–R5 review): F1→F13, F2→F14, F3→G9, F4→F31, F5→G8, F6→F10, F7→F14. Rows 26–27 already handled two. |
