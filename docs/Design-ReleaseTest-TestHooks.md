# Design: TEST_HOOKS and the ReleaseTest configuration

## 0. Problem and approach

Test hooks (fault injection, failure simulation, miscount simulation, virtual-media injectors, test-only state accessors) were compiled under `#if DEBUG`. Consequences:

- Tests could run only against unoptimized Debug builds; optimization-sensitive behavior was never tested.
- Release builds could not be tested with the hook-based tests.
- A few test-only members were compiled unconditionally and shipped in Release.

Approach: introduce a dedicated `TEST_HOOKS` conditional-compilation symbol, independent of `DEBUG`, and a new `ReleaseTest` build configuration that defines `TEST_HOOKS` but is otherwise optimized like Release. Shipping `Release` contains no hooks and does not build test projects.

| Configuration | DEBUG | TEST_HOOKS | Optimize | Debug symbols | Test projects built | Purpose |
|---|---|---|---|---|---|---|
| Debug | yes | yes | no | full (SDK default) | yes | Development |
| ReleaseTest | no | yes | yes | portable PDB | yes | Run the full test suite against optimized code |
| Release | no | no | yes | SDK default | no | Shipping; hook-free |

`TRACE` and other SDK-provided constants are untouched (`TEST_HOOKS` is appended, never replaces).

## 1. Configuration and build-file changes

| File | Change |
|---|---|
| `Directory.Build.props` (new) | Declares `Configurations=Debug;Release;ReleaseTest` for all projects except CLI tools (detected via `\tools\` in the project path; those keep Debug/Release only). Appends `TEST_HOOKS` to `DefineConstants` for Debug (and empty Configuration, i.e. SDK default) and ReleaseTest. ReleaseTest sets `Optimize=true`, `DebugType=portable`, `DebugSymbols=true`. |
| `Directory.Build.targets` | Existing `CheckTestFiles.targets` import kept. Added: for `IsTestProject=true` and `Configuration=Release`, `BuildDependsOn` is replaced by `_SkipTestProjectInRelease` (prints a message). This also covers `dotnet build <test project> -c Release`. |
| `TapeNET.sln` | Added `ReleaseTest` solution configuration for Any CPU, Win32, x64, x86. Hook-dependent projects and test projects map ReleaseTest to ReleaseTest. Test projects have Release mappings without `Build.0` (not built in Release). `TapeServiceNET` maps to ReleaseTest (no hooks yet, provisioned for future ones). `FclAiNET.Test`, `HelpIndexBuilder` and `TypeInventory` map ReleaseTest to Release (no ReleaseTest project configuration, no hooks needed), which avoids MSB4121 warnings. |
| `TapeLibNET`, `TapeServiceNET`, `TapeWinNET`, `TapeConNET`, `TapeConNET.Tests`, `TapeLibNET.Tests` `.csproj` | Existing `Release|AnyCPU` PropertyGroups extended with an `Or` condition for `ReleaseTest|AnyCPU`, so ReleaseTest inherits the project-specific Release settings. |

## 2. Blocks addressed (now `TEST_HOOKS`)

All were previously `#if DEBUG` unless stated otherwise.

### Production code (TapeLibNET)

| File | Member / block | Old | New | Class | Reason |
|---|---|---|---|---|---|
| Agents/TapeAgentBase.cs | `SimulateFileFailures`, `SimulateTOCFailureMask`, `_tocCopyCounter` | DEBUG | TEST_HOOKS | TEST_HOOK | Simulation state |
| Agents/TapeAgentBase.cs | BackupTOC / LoadTOC simulated-failure throws; three `_tocCopyCounter = 0` resets | DEBUG | TEST_HOOKS | TEST_HOOK | Fault-injection call sites |
| Agents/TapeFileBackupAgent.cs | `SimulateFileFailures.ShouldFailNow()` check | DEBUG | TEST_HOOKS | TEST_HOOK | Call site |
| Agents/TapeFileRestoreAgent.cs | `SimulateFileFailures.ShouldFailNow()` check | DEBUG | TEST_HOOKS | TEST_HOOK | Call site |
| Drive/TapeDriveWin32Backend.cs | Region Failure Simulation: `SimulateIOFailures`, `SimulateTimeoutFailures` | DEBUG | TEST_HOOKS | TEST_HOOK | Failure simulators |
| Drive/TapeDriveWin32Backend.cs | Read, Write, Wait call sites | DEBUG | TEST_HOOKS | TEST_HOOK | Call sites |
| Drive/TapeDriveWin32Backend.lto-direct.cs | SPTD write simulated failure | DEBUG | TEST_HOOKS | TEST_HOOK | Call site |
| Media/TapeNavigator.cs | `SimulateNavigationFailures/Error` (+ 3 call sites) | DEBUG | TEST_HOOKS | TEST_HOOK | Navigation failure simulation |
| Media/TapeNavigator.cs | `SimulateSetMiscount`, `...SkipN`, `...Persistent`, `TakeSimulatedMiscount()` (+ 2 call sites); doc and inline comments updated | DEBUG | TEST_HOOKS | TEST_HOOK | Miscount simulation |
| Media/TapeStreamManager.cs | `WritePacker_FORTESTINGONLY`, `ReadPacker_FORTESTINGONLY` | none (always compiled) | TEST_HOOKS | TEST_HOOK | Test-only state exposure; confirmed no production use |
| Services/TapeServiceBase.cs | `VirtualBackend` | none (always compiled) | TEST_HOOKS | TEST_HOOK | Test-only; moved by the maintainer |
| Services/ServiceOperationProgressHandler.cs | Commented-out `Agent.SimulateFileFailures.Enabled = true;` | DEBUG | TEST_HOOKS | TEST_HOOK | Refers to a hook |
| FailureSimulator.cs | Whole file | DEBUG | TEST_HOOKS | TEST_HOOK | Reusable fault injector |
| Virtual/VirtualMediaFaultInjector.cs | Whole file (`VirtualFaultMode`, `VirtualMediaFaultInjector`); doc comment updated | DEBUG | TEST_HOOKS | TEST_HOOK | Block-level fault injector |
| Virtual/VirtualTapeDriveBackend.cs | Two `ApplyFaultInjectorsToMedia()` call sites | DEBUG | TEST_HOOKS | TEST_HOOK | Call sites |
| Virtual/VirtualTapeDriveBackend.Diagnostics.cs | Injector properties, `ApplyFaultInjectorsToMedia` | DEBUG | TEST_HOOKS | TEST_HOOK | Injector wiring |
| Virtual/VirtualTapeMedia.cs | Write/Read fault call sites | DEBUG | TEST_HOOKS | TEST_HOOK | Call sites |
| Virtual/VirtualTapeMedia.Diagnostics.cs | Whole file: block layout/comparison helpers, `WriteFaults`/`ReadFaults`, `ApplyInjected*Fault` | partly none, partly DEBUG | TEST_HOOKS (outer guard) | TEST_HOOK | Proven test-only; inner guard also TEST_HOOKS |

### Test project (TapeLibNET.Tests): all `#if DEBUG` became `#if TEST_HOOKS`

| File | Content |
|---|---|
| Agents/ErrorHandlingTests.cs | Skip / all-fail / restore-fail / TOC-copy failure, abort-after-failure, media fault tests; header comment updated |
| Agents/StatisticsTests.cs | Every-Nth-file failure statistics |
| Agents/TapeSetDeleteVerificationTests.cs | Delete-verification miscount tests |
| Agents/TapeSetHeaderCorrectionTests.cs | Comment updated |
| Agents/TapeSetHeaderWriteVerificationTests.cs | Write-verification tests |
| Agents/TapeSetNotificationTests.cs | Anomaly notification tests |
| Headers/TapeSetHeaderBlockIoTests.cs | Torn-write and block-size-restore tests (`*_FORTESTINGONLY` users) |
| Media/TapeSetNavigationRecoveryTests.cs | Tail-damage / recovery tests |
| Physical/PhysicalErrorTests.cs | Whole file |
| Services/ServiceReportTests.cs, Services/ServiceSetAnomalyTests.cs | Service-level anomaly tests |
| Virtual/VirtualMediaFaultInjectionTests.cs | Whole file |

## 3. Remaining DEBUG blocks (unchanged, with justification)

| File | Member | Justification |
|---|---|---|
| Virtual/VirtualTapeMedia.cs | `AssertByteTotalConsistent`, `AssertPositionConsistent`, `AssertStreamLengthMatchesSummation` (`[Conditional("DEBUG")]`) | Wrap `Debug.Assert`; genuine debug diagnostics, not hooks |
| TapeConNET/Logging/LoggerFactoryBuilder.cs | `#if !DEBUG` usings; `#if DEBUG` logger creation | Build-flavour logger selection (Debug provider / `Debugger.IsAttached`); logging is deliberately not re-conditioned |
| TapeConNET/Program.cs | `Console.Error.WriteLine(ex)` in fatal handler | Debug-only diagnostic output, not a hook (maintainer decision) |
| TapeWinNET/App.xaml.cs | usings and `BuildLoggerFactory()` variants | Same as LoggerFactoryBuilder |

Remaining textual DEBUG mentions in comments describe these genuine Debug items only.

## 4. Open items

No ambiguous blocks remain; all earlier ambiguities were resolved (Program.cs stays DEBUG; `*.Diagnostics.cs` and `*_FORTESTINGONLY` go under TEST_HOOKS). Remaining notes:

- `TapeServiceNET` has a ReleaseTest configuration but no hooks yet.
- `FclAiNET.Test`, `HelpIndexBuilder`, `TypeInventory` and projects under `tools\` are Debug/Release only; in the ReleaseTest solution configuration they build as Release.
- Scripted edits may have altered BOMs/line endings in some source files; check `git diff` if byte-level cleanliness matters.

## 5. Verification results

Clean builds (`dotnet build TapeNET.sln -c <cfg> --no-incremental`):

| Config | Warnings | Errors |
|---|---|---|
| Debug | 0 | 0 |
| ReleaseTest | 0 | 0 |
| Release | 0 | 0 |

Tests (`dotnet test TapeNET.sln -c <cfg> --no-build`), identical in Debug and ReleaseTest:

| Project | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| FclNET.Tests | 469 | 0 | 0 | 469 |
| HelpNET.Tests | 231 | 0 | 7 | 238 |
| TapeWinNET.Tests | 26 | 0 | 0 | 26 |
| TapeConNET.Tests | 54 | 0 | 0 | 54 |
| AiNET.Tests | 78 | 0 | 15 | 93 |
| TapeLibNET.Tests | 4328 | 0 | 258 | 4586 |

No test passes in Debug but fails in ReleaseTest.

Release hook absence: searching `tapelib.dll` for `SimulateFileFailures`, `FailureSimulator`, `FORTESTINGONLY`, `VirtualMediaFaultInjector`, `GetBlockLayout`, `SimulateSetMiscount`, `TakeSimulatedMiscount`, `SimulateNavigationFailures`, `VirtualBackend`: all present in Debug and ReleaseTest, none in Release.

No test project is built in Release: the `bin\Release` and `obj\Release` folders of all test projects contain no DLL or PDB files; `dotnet build <test project> -c Release` prints the skip message and succeeds.

Visual Studio 2026: after reopening the solution, ReleaseTest appears in the configuration toolbar; builds and test runs work as expected.
