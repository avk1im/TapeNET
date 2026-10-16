> **RETIRED:** This migration has been implemented. The current state is described by [docs/Design-ReleaseTest-TestHooks.md](../Design-ReleaseTest.md). The text below is kept as a historical record only.

# Migration: separate TEST_HOOKS from DEBUG (ReleaseTest configuration)

**Status: STEP 1 â€” inventory only. No source or build file has been changed.**

Target configurations: `Debug` (DEBUG + TEST_HOOKS), `ReleaseTest` (TEST_HOOKS only, optimized, `DebugType=portable`), `Release` (neither; test projects not built).

Classification: **TEST_HOOK** = fault injection / simulation / test-only exposure â†’ `#if TEST_HOOKS`.
**DEBUG** = genuinely debugger/diagnostic (rule 3) â†’ unchanged. **AMBIGUOUS** = left unchanged (rule 4).

## 1. Scan scope and result

Scanned all `*.cs` (excluding `bin`/`obj`) in every directory: TapeLibNET, TapeLibNET.Tests, TapeServiceNET, TapeConNET(.Tests), TapeWinNET(.Tests), FclNET(.Tests), FclAiNET(.Test), AiNET(.Tests), HelpNET(.Tests), tools, packaging.
Hits exist only in **TapeLibNET, TapeLibNET.Tests, TapeConNET, TapeWinNET**. No hits in TapeServiceNET, Fcl*, AiNET*, HelpNET*, other test projects.
No `#elif DEBUG`, no compound conditions (`DEBUG && ...`) were found. Patterns found: `#if DEBUG`, `#if !DEBUG`, `[Conditional("DEBUG")]`.

Build files: `Directory.Build.targets` (root) and `tools/CheckTestFiles.targets` exist; **no `Directory.Build.props`** exists yet (to be created in step 2, rule 5). `TapeNET.sln` has Debug/Release only. Projects with per-project Debug/Release PropertyGroups: TapeLibNET.Tests, TapeServiceNET, TapeWinNET (others not enumerated; to review in step 2).

## 2. Production code (TapeLibNET)

| File | Member / block | Current | New | Class | Reason |
|---|---|---|---|---|---|
| Agents/TapeAgentBase.cs:119 | `SimulateFileFailures` (FailureSimulator property) | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Simulated file failures |
| Agents/TapeAgentBase.cs:~133-139 (same `#if` block from 119) | `SimulateTOCFailureMask`, `_tocCopyCounter` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | TOC failure simulation state |
| Agents/TapeAgentBase.cs:348 | BackupTOC: throw "Simulated TOC backup failure" | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Fault injection call site |
| Agents/TapeAgentBase.cs:398 | `_tocCopyCounter = 0;` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Reset of simulation counter |
| Agents/TapeAgentBase.cs:527 | LoadTOC: throw "Simulated TOC restore failure" | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Fault injection call site |
| Agents/TapeAgentBase.cs:582 | `_tocCopyCounter = 0;` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Counter reset |
| Agents/TapeAgentBase.cs:696 | `_tocCopyCounter = 0;` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Counter reset |
| Agents/TapeFileBackupAgent.cs:574 | `SimulateFileFailures.ShouldFailNow()` â†’ TapeIOException | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Agents/TapeFileRestoreAgent.cs:197 | `SimulateFileFailures.ShouldFailNow()` â†’ TapeIOException | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Drive/TapeDriveWin32Backend.cs:86 | `#region Failure Simulation`: `SimulateIOFailures`, `SimulateTimeoutFailures` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Failure simulators |
| Drive/TapeDriveWin32Backend.cs:508 | Read: simulated I/O failure | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Drive/TapeDriveWin32Backend.cs:574 | Write: simulated I/O failure | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Drive/TapeDriveWin32Backend.cs:943 | Wait: simulated timeout failure | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Drive/TapeDriveWin32Backend.lto-direct.cs:364 | SPTD write: simulated failure | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Media/TapeNavigator.cs:625 | `SimulateNavigationFailures` check (+ `SimulateNavigationFailureError`) | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Media/TapeNavigator.cs:860 | `physical += TakeSimulatedMiscount()` and zero-move skip | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Miscount call site |
| Media/TapeNavigator.cs:939 | `SimulateSetMiscount`, `SimulateSetMiscountSkipN`, `SimulateSetMiscountPersistent`, `TakeSimulatedMiscount()` (also `SimulateNavigationFailures/Error` declarations â€” verify when editing) | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Miscount simulation state. Doc comment line 952 mentions `#if DEBUG` / "never reach Release (Â§15.4)" â€” comment text to update |
| Media/TapeNavigator.cs:1254 | Navigation failure check | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Media/TapeNavigator.cs:1284 | `filemarks += TakeSimulatedMiscount()` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Miscount call site |
| Media/TapeNavigator.cs:1389 | Navigation failure check | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Media/TapeStreamManager.cs:1458 | `WritePacker_FORTESTINGONLY` | none (always compiled, `internal`) | `#if TEST_HOOKS` | TEST_HOOK | Test-only state exposure, unconditional today. Wrapping removes it from Release; **confirm no non-test use** |
| Media/TapeStreamManager.cs:1611 | `ReadPacker_FORTESTINGONLY` | none | `#if TEST_HOOKS` | TEST_HOOK | Same as above |
| Services/ServiceOperationProgressHandler.cs:111 | Commented-out `Agent.SimulateFileFailures.Enabled = true;` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Refers to a test hook (code is a comment only) |
| FailureSimulator.cs:1 | whole file, class `FailureSimulator` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Reusable fault injector |
| Virtual/VirtualMediaFaultInjector.cs:6 | `VirtualFaultMode`, `VirtualMediaFaultInjector` (whole file). Doc line 64 says "`#if DEBUG`-only" â€” comment to update | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Block-level fault injector |
| Virtual/VirtualTapeDriveBackend.cs:607 | `ApplyFaultInjectorsToMedia()` call in LoadMedia | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Virtual/VirtualTapeDriveBackend.cs:963 | `ApplyFaultInjectorsToMedia()` call (initiator medium) | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Virtual/VirtualTapeDriveBackend.Diagnostics.cs:8 | `ContentWriteFaults` etc., `ApplyFaultInjectorsToMedia` (whole partial) | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Injector ownership/wiring. File is named "Diagnostics" â€” contents beyond injectors to be checked in step 2 |
| Virtual/VirtualTapeMedia.cs:462 | Write path: `WriteFaults` â†’ `ApplyInjectedWriteFault` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Virtual/VirtualTapeMedia.cs:629 | Read path: `ReadFaults` â†’ `ApplyInjectedReadFault` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Call site |
| Virtual/VirtualTapeMedia.Diagnostics.cs:394 | `WriteFaults`/`ReadFaults` properties, `ApplyInjected*Fault` | `#if DEBUG` | `#if TEST_HOOKS` | TEST_HOOK | Injector members |
| Virtual/VirtualTapeMedia.cs:358 | `AssertByteTotalConsistent` | `[Conditional("DEBUG")]` | unchanged | DEBUG | Wraps `Debug.Assert` |
| Virtual/VirtualTapeMedia.cs:368 | `AssertPositionConsistent` | `[Conditional("DEBUG")]` | unchanged | DEBUG | Wraps `Debug.Assert` |
| Virtual/VirtualTapeMedia.cs:1303 | `AssertStreamLengthMatchesSummation` | `[Conditional("DEBUG")]` | unchanged | DEBUG | Wraps `Debug.Assert` |

## 3. Apps

| File | Member / block | Current | New | Class | Reason |
|---|---|---|---|---|---|
| TapeConNET/Logging/LoggerFactoryBuilder.cs:3 | usings `System.Diagnostics`, `NullLoggerFactory` | `#if !DEBUG` | unchanged | DEBUG | Build-flavour logger selection (`AddDebug`, `Debugger.IsAttached`); logging must not be wrapped in new conditions per instructions â€” left as is |
| TapeConNET/Logging/LoggerFactoryBuilder.cs:44 | `LoggerFactory.Create` with Debug provider / `#else` branch | `#if DEBUG` | unchanged | DEBUG | Debug-output logger provider |
| TapeConNET/Program.cs:49 | `Console.Error.WriteLine(ex)` in fatal handler | `#if DEBUG` | unchanged | `#if DEBUG` | Outputs addition Debug diagnostic; not a test hook. Listed per rule 4 |
| TapeWinNET/App.xaml.cs:6 | usings | `#if !DEBUG` | unchanged | DEBUG | As LoggerFactoryBuilder |
| TapeWinNET/App.xaml.cs:52 | `BuildLoggerFactory()` Debug vs Debugger.IsAttached variants | `#if DEBUG` | unchanged | DEBUG | Debug logger selection |

## 4. Test project (TapeLibNET.Tests) â€” all `#if DEBUG` â†’ `#if TEST_HOOKS`

All are TEST_HOOK: each block wraps tests (or whole test files) that use the hooks listed above and therefore must compile whenever those hooks exist.
Exact end of each block (`#endif`) must be matched in step 2; the doc-comment lines noted below mention "DEBUG".

| File | `#if` line(s) | Content | Hook used |
|---|---|---|---|
| Agents/ErrorHandlingTests.cs | 153, 489, 695, 904, 973, 1042, 1280, 1610, 1698, 1762, 1794 | Skip/all-fail/restore-fail/TOC-copy failure tests; abort-after-failure setups; media write/read fault tests (1042â€“1390). Header comment line 18 says "`#if DEBUG`-only" â€” update text | `SimulateFileFailures`, `SimulateTOCFailureMask`, virtual media fault injectors |
| Agents/StatisticsTests.cs | 262 | Every-Nth-file failure statistics test | `SimulateFileFailures` |
| Agents/TapeSetDeleteVerificationTests.cs | 127, 298, 386 | Delete-verification miscount tests | `SimulateSetMiscount*`, fault injectors |
| Agents/TapeSetHeaderWriteVerificationTests.cs | 311, 402 | Write-verification tests (unreadable header; read-fault injector unconsumed) | miscount / `ReadFaults` |
| Agents/TapeSetNotificationTests.cs | 100, 201, 449 | Anomaly notification tests | `SimulateSetMiscount*` |
| Headers/TapeSetHeaderBlockIoTests.cs | 360, 711 | Torn-write and block-size-restore tests. Line 677 uses `WritePacker_FORTESTINGONLY` â€” outside a located `#if`; verify it is not in a non-DEBUG-compiled region | `WriteFaults`, `*_FORTESTINGONLY` |
| Media/TapeSetNavigationRecoveryTests.cs | 116, 162, 269, 450, 634, 690 | Tail-damage / recovery tests | `SimulateSetMiscount*` |
| Physical/PhysicalErrorTests.cs | 1 (whole file) | Physical-error tests | `SimulateIOFailures`, `SimulateTimeoutFailures` |
| Services/ServiceReportTests.cs | 157, 296, 394 | Service report tests with anomalies | miscount hooks |
| Services/ServiceSetAnomalyTests.cs | 184, 284, 464, 587 | Service-level anomaly tests | miscount hooks |
| Virtual/VirtualMediaFaultInjectionTests.cs | 10 (whole file) | Fault injector tests | `VirtualMediaFaultInjector` |

## 5. Test-only members not conditionally compiled

| File | Member | Note |
|---|---|---|
| Media/TapeStreamManager.cs:1458 | `WritePacker_FORTESTINGONLY` | TEST_HOOK, see section 2 |
| Media/TapeStreamManager.cs:1611 | `ReadPacker_FORTESTINGONLY` | TEST_HOOK, see section 2 |

No other `*_FORTESTINGONLY`-style names found. The remaining `FOR_TESTING` text match (`Scenarios/FileEdgeCaseTests.cs:227`) is a file-name string literal â€” not a member.

RESOLUTION: Confirmed use for testing only, no production use. Hence place under TEST_HOOKS.

## 6. Ambiguous cases: RESOLUTION

1. `TapeConNET/Program.cs:49` â€” `Console.Error.WriteLine(ex)` â€” RESOLUTION: under DEBUG (diagnostic, not a hook).
2. Mixed-content partial files `Virtual/*.Diagnostics.cs` â€” RESOLUTION: Proved entirely injector-related, used in tests only, NOT in production. Hence all members go under TEST_HOOKS, including those currently outside of #if DEBUG.
3. `*_FORTESTINGONLY` accessors â€” RESOLUTION: Place under TEST_HOOKS; no production use confirmed.

## 7. Build-file plan (for later steps, not applied)

- Create `Directory.Build.props` (none exists): `ReleaseTest` config; `DefineConstants` **append** `TEST_HOOKS` for `Debug` and `ReleaseTest`; `ReleaseTest`: `Optimize=true`, `DebugType=portable`, no DEBUG; TRACE untouched.
- Extend `Directory.Build.targets` (root) to skip building test projects in `Release`; reuse `IsTestProject`.
- Add `ReleaseTest` to `TapeNET.sln` and map project configurations; per-project `Debug|AnyCPU`/`Release|AnyCPU` groups (TapeLibNET.Tests, TapeServiceNET, TapeWinNET) need review.
- `tools/CheckTestFiles.targets`: extend only if it needs awareness of configuration.

