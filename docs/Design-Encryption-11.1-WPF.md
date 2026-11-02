# Design-Encryption §11.1 — TapeWin (WPF), revision 3

Replaces §11.1 of Design-Encryption v1.3. Phase 7 of §20. Sources checked: `MainWindow.xaml` (+ `.cs`),
`MainViewModel*.cs`, `BackupWindow.xaml` + VM, `RestoreWindow.xaml` + VM, `DeleteBackupSetsViewModel`, `TOCView`,
`BackupSetListItem`, `TapeTreeItemViewModel`, `AppendAfterOption`, `DeleteFromSetOption`, the three usage-bar presenters,
`ScanResultViewModel`, `AskDialog`, `TapeIcons`, `AppSettings`; for step 1 the service, key-ring and encryption sources.

## 11.1.0 Principles

| # | Principle |
|---|---|
| W1 | **Quiet by default.** Loading a cartridge never asks for a password. Locked sets render as locked; the user asks to look inside. |
| W2 | **Ask only on an explicit gesture** — never on selection: arrow keys through the tree must not pop dialogs. |
| W3 | **Verify inside the dialog that asks.** Every password is checked against its envelope *before* the dialog closes — CPU only, ~0.25 s, no tape movement (the envelope is in the loaded TOC). A typo never costs the user a prepared Backup or Restore. The service's own `OnAskPassword` stays as the fallback only. |
| W4 | **Locked is normal, not a warning.** Grey italic plus a padlock; the warning colours stay reserved for real problems (TOC from file, recovered TOC). |
| W5 | **Keys, never passwords, outlive a dialog** — for the session, re-applied to every newly loaded TOC (§11.1.7). |
| W6 | **Each window speaks through its own warning pane** — advice, notes and conflicts. Only the error of a password *field* (mismatch, wrong password, empty) shows in red directly under that field. Never through the main window's log, which may be hidden behind the dialog. |
| W7 | **User-facing text never cites the design** — no "(E8)", no "§". |
| W8 | **Wrong passwords slow down, session-wide** (§11.1.3). |

## 11.1.1 Main window

### Rendering a locked set

| Place | Locked | Unlocked encrypted |
|---|---|---|
| Tree node | `Set #3 \| 0: 🔒 encrypted set` (`DisplayName`), grey italic, folder icon with a padlock overlay (`IconComposer`, `OverlayCorner.BottomRight`, glyph `E72E`) | normal text, open-padlock overlay (`E785`) |
| Backup sets list | Description `🔒 encrypted set`, grey italic; Files, Size, Created, Inc?, Block, Hash, Compression `—`; **on Tape** shown (plain stub) | all columns |
| Details pane | `DescribeSet` already returns only the visible rows (§10.6) | all rows |
| Files pane | one centred placeholder: *"This set is encrypted. Unlock it to see its files."* with **[Unlock…]** | files |
| Usage bar segment | label `#3\|0`, tooltip `Set #3\|0: 🔒 encrypted set`, size on tape | as now |

### Unlock gestures

| Gesture | Effect |
|---|---|
| [Unlock…] in the files placeholder | unlock dialog for this set |
| Double-click / Enter on a locked set (tree or list) | unlock dialog; on success navigates in |
| **Unlock…** (set context menu; Media menu) | unlock dialog for the **current selection** (§11.1.2); its text follows the selection like Restore's |
| Media ▸ **Forget Passwords** | `ForgetPasswordsAsync`; every encrypted set locks again |

Unlock is enabled only when its target holds a locked set. Unlocking always tries the password against every locked chain
(behind the busy overlay) and reports: *"Unlocked 5 sets (2 key chains)."* No toolbar button.

## 11.1.2 Multi-set selection (fixes an existing gap)

**Today:** the backup sets list allows Ctrl/Shift multi-selection (`ListView` default `Extended`), but the view model binds
only `SelectedItem` — so Restore, Validate and Verify act on one highlighted set however many are highlighted.

**Selection model.** `BackupSetsListView.SelectionChanged` feeds `MainViewModel.SelectedBackupSets` (the highlighted
rows). The target of a set command, in order:

1. checked files / checked sets (the existing restore marking) — unchanged;
2. **the highlighted rows in the backup sets list** — new: one or many;
3. the set selected in the tree;
4. all sets (Restore only).

| Target | Restore / Validate / Verify | Unlock |
|---|---|---|
| checked files | `Restore 53 files selected…` | `Unlock 2 sets…` — the locked sets owning them |
| 2 highlighted rows | `Restore 2 sets…` | `Unlock 2 sets…` (locked ones only); `Unlock set #3 \| 0…` when one |
| tree set | `Restore set #3 \| 0…` | `Unlock set #3 \| 0…` |
| nothing | `Restore all sets…` | `Unlock Encrypted Sets…` (all locked) |

`SelectedSetIndexes` uses the same order, so `StartRestore` processes every highlighted set. `SelectedBackupSet` (single)
stays for navigation and the usage-bar highlight.

## 11.1.3 The password dialog (`PasswordDialog`, new)

A separate window, not an `AskDialog` flag: `PasswordBox` does not bind, and the layout differs.

```
┌ Unlock backup set ───────────────────────────────┐
│ Set #3 | 0: 🔒 encrypted set                      │
│ Enter the password of this backup set.            │
│ Password: [●●●●●●●●●●        ]                    │
│   ✖ Wrong password                     (red, field)│
│ ┌ warning pane ─────────────────────────────────┐ │
│ │ ⚠ Caps Lock is on.                             │ │
│ │ ⓘ Attempt 2 of 3.                              │ │
│ └────────────────────────────────────────────────┘ │
│                                   [ OK ] [Cancel] │
└───────────────────────────────────────────────────┘
```

| Mode | Fields | Pane (W6) | Used by |
|---|---|---|---|
| **Enter** | one `PasswordBox` | Caps Lock (Warning), attempt counter / wait (Info → Warning), series note (Info) | Unlock, `OnAskPassword` fallback |
| **New** | password + confirm | the advice *"A lost password cannot be recovered. Keep an exported TOC."* (Warning), Caps Lock | `OnAskPassword(NewSet)` fallback |

**Verification inside (W3).** The dialog takes a validator `Func<char[], Task<TapeResult>>`. OK runs it; on failure the
dialog stays open, clears the field, shows the error under it. For a set of a multi-volume series, the pane adds (Info):
*"If this set's password was changed, this volume's own TOC may still use the old one."* Cancel returns null.

**Wrong passwords slow down (W8).** The service keeps one failure counter per session, shared by every caller —
`PasswordDialog`, BackupWindow, RenameBackupSetWindow, the `OnAskPassword` loops:

| Consecutive failures | Next attempt allowed after |
|---|---|
| 1–3 | at once |
| 4 | 5 s |
| 5, 6, … | doubling, capped at 60 s |

A success resets it. While waiting, the interactive verbs refuse without trying (`ERROR_ACCOUNT_LOCKED_OUT`, message
carrying the remaining time); the prompt loops wait it out (cancellable). The dialog disables OK and the pane counts down
(*"Too many wrong passwords — try again in 12 s."*). Closing, reopening or switching dialogs does not reset it. Not
persisted across a restart: the real protection is the PBKDF2 work factor and the cartridge's custody — an attacker works
offline on a copy — so the delay targets casual guessing at the console. Three attempts per dialog before it closes stays.

## 11.1.4 BackupWindow

### Options group

An **Encryption** combobox under Compression: `None` (default) · `Software`; `Drive` later, mirroring compression.

| Situation | Fields | Warning pane |
|---|---|---|
| New set, Software | **Password** + **Confirm**; red under them only: *"Passwords don't match"* / *"Password is empty"* | Warning: *"A lost password cannot be recovered. Keep an exported TOC."* |
| Incremental on an **encrypted** base | combobox read-only *Software — continues set #3*; **one** field **Password of set #3**; red under it: *"Wrong password"*; green tick when verified | Info, until verified: *"Enter the password of set #3 to continue the encrypted chain."* |
| Incremental on a **plain** base, Software selected | — | Warning: *"An incremental backup of an unencrypted set cannot be encrypted."* Start disabled |
| Software selected while Hardware compression was | Hardware leaves the compression list; compression → Software | Info: *"Hardware compression is not available with software encryption — software compression selected."* Gone once Encryption is None again or the user picks a compression |

Each pane line exists only while its condition holds; they follow the existing append/overwrite lines.

**The base-set password field** (decision: in-dialog). The service needs it anyway — it wraps the chain key for the new
set (§8.2) — so verifying it in the window adds one service call: on focus loss and again on Start,
`VerifyPasswordAsync(base, password, unlock: true)`. Wrong → red under the field, every selection kept. Right → green
tick, the chain unlocks for the session. No confirmation: an existing password is checked against its envelope; only a
new one must be typed twice.

### Media group and sources

| Control | Locked set |
|---|---|
| Append after set | listed `#3 \| 0: 🔒 encrypted set`, **fully selectable** — appending or overwriting needs no key; the set header verifies the position |
| Add sources from set (decision: no in-dialog unlock) | listed the same; **[+] disabled** for a locked set, tooltip *"Unlock this set in the main window to add its files."* |
| Load from last backup | disabled while the last set is locked (it copies hidden settings) |

## 11.1.5 Restore, Validate, Verify

**No password field in RestoreWindow.** `StartRestore` collects the target sets (§11.1.2); if any is locked, the unlock
dialog (Enter mode, validated, all chains) runs **before** RestoreWindow opens — the window shows file counts and sizes,
which need the key. Declined, or some still locked: those sets are dropped; RestoreWindow's warning pane says (Info)
*"1 encrypted set left out — it is locked."*; nothing left → no window. The service's pre-flight and `OnAskPassword` stay
the safety net (e.g. a volume whose TOC predates a password change).

## 11.1.6 RenameBackupSetWindow (new)

| Field | Shown | Rule |
|---|---|---|
| Name | always | prefilled with `Description` (once readable) |
| **Current password** | encrypted set | **always required**, by convention; verified in the window, also when a held key already opened the set. A locked set is unlocked by it, then Name fills |
| Change password (expander) | encrypted set | New + Confirm; red under them only for mismatch / empty |

Warning pane: the advice *"A lost password cannot be recovered. Keep an exported TOC."* (Warning) while the expander is
open; for a series, *"Volumes #1–#2 keep the old password in their own TOC copies; this volume's TOC, which a restore
uses, gets the new one."* (Info). Calls `RenameBackupSetAsync(set, name, current, new)`; replaces the `AskDialog` path for
every set (plain sets show Name only).

## 11.1.7 Keys across media changes

`TapeServiceBase.RetainKeysAcrossMedia` (default false; TapeWin sets true). When true, the E17 clear sites still lock
every set of the old TOC but keep the keys; every TOC load re-applies them (key trial, no KDF). The same cartridge
reinserted, or another volume of the series, opens silently; an unrelated cartridge matches nothing. Media ▸ Forget
Passwords and closing TapeWin drop them. E17 gains: *"…unless `RetainKeysAcrossMedia`: keys are retained, only the sets
lock."*

## 11.1.8 Delete, Scan

- **DeleteBackupSetsWindow:** no password. Locked sets listed with the padlock; file count `—`; the summary reads
  *"~1.2 GB, 340 files + 1 locked set"*; deleted by position, verified by header.
- **ScanMediaWindow:** no option. **ScanResultWindow:** no unlock — fragments carry no key. A TOC row reads
  *"recovered — 3 set(s), 1 encrypted"*; unlocking happens in the main window after **Use this TOC**.

## 11.1.9 Code that throws on a locked set today

| File | Member | Fix |
|---|---|---|
| `BackupSetListItem` | `Description`, `FileCount`, `FileCountFormatted`, `CreatedOn*`, `IsIncremental`, `BlockSize`, `HashAlgorithm`, `Compression`, `TotalSize` (`Sum`), `HasPartialSelection` | `IsLocked`; locked → `DisplayName` / `—` / 0 |
| `TapeTreeItemViewModel.CreateBackupSetItem` | `Description` | `DisplayName`; `IsLocked`, `IsEncrypted`; icon by state |
| `AppendAfterOption`, `DeleteFromSetOption` | `Description`, `Count` | `DisplayName`; count `—`; `IsLocked` |
| `MediaUsageBarPresenter.AddContentSegments` | tooltip `Description`, `Count` | `DisplayName`, `DisplayCount` |
| `DeleteBackupSetsViewModel.DeleteFileCount` | `toc[i].Count` | skip locked; count them separately |
| `BackupViewModel` | `GetSourceFilesFromSet` (`SelectFiles`), `LoadFromLastBackup` | guarded by the disabled [+] / disabled command |
| `MainViewModel` | `CreateAdHocSetItems` (`Count`), `LoadBackupSetInfo` (`GetOrCreate`), `ApplyBackupSetCheckedState`, `AreAllBackupSetsChecked`, `RenameBackupSetAsync` (`Description`) | locked → placeholder pane; skip in "check all"; rename via §11.1.6 |
| `TOCView` | `GetOrCreate`, `BuildBackupSetItemList` (pending global filter) | `TryGetOrCreate` returns null for a locked set; skipped by the filter pass |
| `ScanResultViewModel` | `setNames` (`Description` of harvested sets) | `DisplayName` (a recovered TOC loads its encrypted sets locked) |

## 11.1.10 Library additions (step 1)

| Member | Behaviour |
|---|---|
| `Task<TapeResult> VerifyPasswordAsync(int setIndex, char[] password, bool unlock)` | Unwraps the set's envelope in memory; `unlock` adds the key and unlocks its chain. Works on locked and unlocked sets (Rename's convention). No tape I/O. Clears the array. Counts toward W8. |
| `Task<UnlockOutcome> UnlockWithPasswordAsync(char[] password)` | Tries the password on every locked chain: each still-locked set's envelope in turn until one opens; its key then opens its whole chain (E13) without further KDF. Returns sets and chains opened. A password that opens nothing counts once toward W8. Cost: one KDF per locked set of a foreign chain (~0.25 s each), behind the busy overlay. |
| `TimeSpan PasswordRetryDelay { get; }` | the remaining W8 wait; zero when free |
| `bool RetainKeysAcrossMedia { get; set; }` | §11.1.7 |
| `ForgetPasswordsAsync` | always drops the keys, retention or not |
| `RenameBackupSetAsync(…, password, newPassword)` | a password **change** verifies the current password against the envelope, even when a held key already opened the set |
| `OnAskPassword` loops | obey W8: wait out the delay (cancellable) before re-asking |

## 11.1.11 The CLI (TapeConNET)

| Item | Effect |
|---|---|
| W8 delay | applies — it lives in the service. One `tapecon` run is one session, so it only slows repeated wrong answers at the masked prompt within one command |
| `RetainKeysAcrossMedia` | stays off (default): a CLI run is one process |
| `VerifyPasswordAsync`, `UnlockWithPasswordAsync` | not used — the CLI unlocks through each operation's password options |
| `rename-set --change-password` | now always verifies the current password against the envelope |
| Multi-set selection, `PasswordDialog`, panes | WPF only |

No CLI code changes in Phase 7; `EncryptionCliTests` must stay green.

## 11.1.12 Steps and files

Step 2 can be tested before step 5: `tapecon backup --encrypt --password-file …` writes encrypted sets to a virtual
cartridge file that TapeWin opens.

| Step | Content | Exit | Files |
|---|---|---|---|
| 1 | Library §11.1.10 + tests | all green | `Services/TapeServiceBase.Encryption.cs`, `TapeServiceBase.cs`, `TapeServiceBase.Scan.cs`, `Encryption/TapeKeyRing.cs`, `Services/PasswordPrompt.cs`; tests `ServiceUnlockTests.cs` (new) |
| 2 | Crash-proofing §11.1.9, locked rendering §11.1.1 — no prompts | a tapecon-encrypted cartridge browses without exception | `BackupSetListItem`, `TapeTreeItemViewModel`, `AppendAfterOption`, `DeleteFromSetOption`, `MediaUsageBarPresenter`, `DeleteBackupSetsViewModel`, `BackupViewModel`, `TOCView`, `ScanResultViewModel`, `TapeIcons`, `MainViewModel.cs`, `MainViewModel.Restore.cs`, `MainWindow.xaml`, `App.xaml` |
| 3 | Multi-set selection §11.1.2 (independent of encryption) | `Restore 2 sets…` processes both | `MainViewModel.cs`, `MainViewModel.Restore.cs`, `MainWindow.xaml` + `.cs` |
| 4 | `PasswordDialog` §11.1.3, unlock gestures, Media menu, `WpfServiceHost.OnAskPassword`, retention on | unlock / wrong ×4 wait / forget / reload by hand | `AskDialog.xaml` + `.cs` (template), `WpfServiceHost.cs`, `TapeService.cs`, `MainViewModel.cs`, `MainWindow.xaml` + `.cs`, `App.xaml` |
| 5 | BackupWindow §11.1.4 | new encrypted set; incremental with wrong then right base password keeps selections | `BackupWindow.xaml` + `.cs`, `BackupViewModel.cs`, `MainViewModel.Backup.cs` |
| 6 | Restore pre-unlock §11.1.5, `RenameBackupSetWindow` §11.1.6 | mixed selection restores; rename + password change | `RestoreViewModel.cs`, `RestoreWindow.xaml`, `MainViewModel.Restore.cs`, `MainViewModel.cs` |
| 7 | Delete and Scan §11.1.8 | — | `DeleteBackupSetsViewModel.cs`, `DeleteBackupSetsWindow.xaml`, `ScanResultViewModel.cs`, `ScanResultWindow.xaml` |

WPF help topics: deferred to the help revamp at the end of the encryption branch.
