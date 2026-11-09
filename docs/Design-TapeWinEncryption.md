## Design — Set Encryption in TapeWin (WPF)

**Status:** v2.0 · **as implemented** (Phase 7, Steps 1–7, help revamp).
**Companion of:** Design-Encryption.md (library, service, CLI). This document was its §11.1; **each section keeps its
former number in brackets** (`[→ Design-Encryption 11.1.x]`) so the references in the code stay valid.
**See also:** the TapeNET primer (TOC barrier, selection model, usage bar), Design-MultiVolume §5.5 (series TOC),
Design-TapeWinHelp (help pane).

## 0. Principles [→ Design-Encryption 11.1.0]

| # | Principle |
|---|---|
| W1 | **Quiet by default.** Loading a cartridge never asks for a password; locked sets render as locked. |
| W2 | **Ask only on an explicit gesture** — never on selection. |
| W3 | **Verify inside the window that asks** — between operations every password is checked against its envelope before the window closes (CPU only, no tape movement); a typo never costs a prepared Backup, Restore or Rename. |
| W4 | **Locked is normal, not a warning** — grey italic and a padlock; warning colours stay for real problems. |
| W5 | **Keys, never passwords, outlive a dialog** — retained for the session (§7). |
| W6 | **Each window speaks through its own warning pane**; only a password field's own message shows under that field. |
| W7 | **User-facing text never cites the design.** |
| W8 | **Wrong passwords slow down, session-wide** — the service's delay (Design-Encryption §10.8); every password field counts it down. |
| W9 | **One password opens every set it fits** — whichever window carried it (Design-Encryption E19). |

## 1. Main window [→ Design-Encryption 11.1.1]

### 1.1 Rendering

| Place | Locked | Unlocked encrypted |
|---|---|---|
| Tree node | `Set #3 \| 0: 🔒 [encrypted set]`, grey italic (`LockedFg`) | name, normal text |
| Set icon (tree and list) | overlays by state, §1.3 | |
| Backup sets list | Description 🔒 placeholder, grey italic; Files, Size, Created, Inc?, Block, Hash, Compression "—"; **on Tape** from the stub | all columns |
| Details pane | DescribeSet: only the stub rows, Encryption "— locked" | all rows |
| Files pane | placeholder *"This set is encrypted. Unlock it to see its files."* with **[Unlock…]** (`UnlockShownSetCommand`) | files |
| Usage bar | fed by DescribeUsage: placeholder name, "locked", size on tape | as plain |

### 1.2 Unlock gestures and menus (MainViewModel.Encryption.cs)

| Gesture | Effect |
|---|---|
| [Unlock…] in the files placeholder | unlock dialog for the shown set |
| Double-click / Enter on a locked row | unlock, then navigate in (`NavigateToSelectedBackupSetAsync`) |
| **Unlock…** — Media menu, tape and set context menus | `UnlockCommand` on the current selection (§2): *"Unlock set #3 \| 0…"*, *"Unlock 2 sets…"*, *"Unlock Encrypted Sets…"* (nothing selected) |
| Media ▸ **Forget Passwords** | `ForgetPasswordsCommand` → ForgetPasswordsAsync |

- **Media menu:** the two items (and their separator) show while the TOC holds an encrypted set or passwords are held
  (`ShowEncryptionMenu`), disabled when not applicable — a stable menu.
- **Context menus:** shown only when applicable (`HasUnlockTargets`, `HasHeldPasswords`, separator `HasEncryptionActions`).
- **UnlockWithDialogAsync(targets, all):** captures the target set objects while the TOC is quiet; the dialog's validator
  runs `UnlockWithPasswordAsync` in a busy scope (every locked chain, E19). Outcomes: all targets open — or, for "all",
  anything opened — closes the dialog (*"Unlocked 5 sets (2 key chains)."* in the pane); something opened but not the
  targets → amber *"This password opens other sets, not this one."* / *"1 set needs another password."* (Progress, not
  counted); nothing → red error. Afterwards the TOC barrier refreshes the views.

### 1.3 Set icon overlays (TapeIcons.GetBackupSetIcon)

`BackupSetIconState(Lock, Incremental, ContinuedFromPrevVolume, ContinuesOnNextVolume)` — composed once per combination
(≤ 24) and cached; each overlay a Segoe MDL2 glyph on a white disc:

| Corner | State | Glyph |
|---|---|---|
| bottom right | encrypted: locked / unlocked (`SetLockState`) | E72E / E785, grey |
| bottom left | incremental (hidden while locked) | E710 ⊕, green |
| top left | continued from the previous volume | E72B ←, blue, 0.45 |
| top right | continues on the next volume | E72A →, blue, 0.45 |

`BackupSetIconState.Of(toc, setIndex)` reads the TOC between operations; `BackupSetListItem` takes
`continuesOnNextVolume` from its creator.

## 2. Selection [→ Design-Encryption 11.1.2]

One selection, computed once per change (`ComputeRestoreSelection`, only while the TOC is quiet), feeds Restore /
Validate / Verify, their enabled state, the status bar — and Unlock (`UpdateUnlockTargets`):

| Type | Source | Restore text | Unlock target |
|---|---|---|---|
| 1 Checked | file and set checkboxes, incl. sets checked as a whole | *Restore 195 files selected from 2 sets; 1 set locked…* | its locked sets |
| 2a Tree set | the set selected in the tree | *Restore set #3 \| 0…* | that set, if locked |
| 2b Highlighted | rows highlighted in the sets list | *Restore 2 sets…* | the locked ones |
| 3 All sets | nothing selected | *Restore all sets…* | every locked set |

`SelectionText.Describe` words it all (*"3 locked sets"*, *"locked set #3 | 0"*). Locked sets stay selectable for
Restore (§5). **TOCView `_checkedWhole`**: a locked set checked as a whole is remembered by set object; once unlocked and
first visited its view starts with all files checked; a delete or new backup never hands the check to another set.

## 3. Password controls and the password dialog [→ Design-Encryption 11.1.3]

### 3.1 IPasswordSource, PasswordInput, NewPasswordInput (Controls/)

A PasswordBox does not bind; each control hands itself to its view model through `PasswordSource`
(OneWayToSource) as **IPasswordSource**: `HasPassword`, `IsValid`, `TakePassword()` (fresh array, field cleared),
`CopyPassword()` (field kept — for in-window checks), `Clear()`, `PasswordChanged` (text only).

| Control | Content | IsValid |
|---|---|---|
| **PasswordInput** | label, box with a green ✓ (`IsVerified`), **one message line**, grey hint | typed and not held back by the delay |
| **NewPasswordInput** | two PasswordInputs (composition): *"The passwords don't match"* under Confirm, ✓ on match; hint *"Recommended: at least 12 characters"* | both typed and equal |

**The message line** — `Message` (plain text), `MessageLevel` (default Error), optional `MessageIcon`; glyph and colour
from the level (`WarningLevelToIconConverter`, `WarningFg.*`). Typing clears `Message`, `MessageIcon`, `IsVerified`
(`SetCurrentValue` — host bindings survive).
**The countdown (W8)** lives here: `RetryDelaySource = () => service.PasswordRetryDelay` (lock-free), polled every
250 ms with Caps Lock. While it runs the line shows *"⏳ Too many wrong passwords — try again in 12 s"* (Warning), the
caller's message waits underneath, `IsRetryBlocked` is set and commands re-query — Start / OK follow without a timer in
any view model. A new password is never checked, so `NewPasswordInput` has no countdown.

### 3.2 PasswordDialog (TapeWinNET root)

One field, the warning pane above the buttons (Caps Lock, *"Attempt 2 of 3."*, the series note *"If this set's password
was changed, this volume's own table of contents may still use the old one."*, the lost-password advice for a new
password, the validator's info), **[Help ▶]** and F1 (`dialog.password`, Adjacent pane).

| Mode | Opened by | OK |
|---|---|---|
| **Validator** | Unlock gestures, Restore pre-unlock — between operations | runs the validator (may call the service, W3); a wrong password keeps the dialog open; `PasswordVerdict.Progress` → amber message, attempt not counted; after 3 failures it closes |
| **Service** | `WpfServiceHost.OnAskPassword` — on the worker thread **holding the service lock** | returns `Password`; **no validator** (it would deadlock); the service checks and asks again with error and attempt |

`PasswordDialogOptions.ForRequest` words each PasswordPurpose. The dialog never closes during a running check. With the
help pane open, the content-sized window freezes its height (DialogHelpPaneController).

## 4. BackupWindow [→ Design-Encryption 11.1.4]

`BackupViewModel.Encryption.cs`.

### 4.1 Scheme and password

| Situation | UI |
|---|---|
| **Encryption** combobox | None · Software (AES-256-GCM); Drive once SupportsHardwareEncryption |
| New chain, no encrypted set kept | NewPasswordInput |
| New chain, encrypted sets kept by the backup | **Password** selector: *New password* (default), *Password of set #N \| k* for every encrypted, unlockable set up to the append-after set — locked and unlocked alike, so the list reveals nothing |
| Reuse chosen | one PasswordInput *"Password of set #N \| k:"* — checked on focus loss and on Start with `VerifyPasswordAsync(…, unlock: false)`; the service then starts a **new chain** under that password (and opens the sets it fits, E19) |
| Incremental on an encrypted base | combobox replaced by *"Software (AES-256-GCM) — continues set #3 \| 0"*; one field *"Password of set #3 \| 0:"*, checked the same way — nothing unlocks while the window is open; the service unlocks at Start |
| Incremental on a plain base, Software chosen | warning, Start disabled |

Wrong password → *"Wrong password"* under the field, every selection kept; the delay → the field's countdown. Start
needs `IsPasswordReady` (none needed / new pair valid / existing typed, not blocked, not known wrong) and checks an
unverified existing password first.

### 4.2 Warning pane lines (W6, the most severe level wins)

| Line | Level |
|---|---|
| *An incremental backup of an unencrypted set cannot be encrypted.* | Warning |
| *Enter the password of set #3 \| 0 to continue the encrypted chain.* / *— the new set will use the same password.* | Info, until verified |
| *A lost password cannot be recovered. Keep an exported TOC.* | Warning, whenever encrypting |
| *Hardware compression is not available with software encryption — software compression selected.* | Info, until the user picks a compression or encryption is off |

Compression is stored by value: Hardware leaves the list under software encryption and the choice survives.

### 4.3 Media and sources

| Control | Locked set |
|---|---|
| Append after set | listed with 🔒, selectable — no key needed |
| Add sources from set | [+] disabled, tooltip *"Unlock this set in the main window to add its files."* |
| Load defaults | Encryption None |
| Load from last backup | disabled while the last set is locked; else copies its scheme and selects *Password of set #last* (never a password) |

## 5. Restore, Validate, Verify [→ Design-Encryption 11.1.5]

`OpenRestoreWindowUnlockingAsync`: the selection is captured once; checked, locked, unlockable sets are unlocked
**before** RestoreWindow opens (`UnlockWithDialogAsync`, one dialog, every chain) — the window shows file counts and sizes,
which need the keys. Sets still locked are left out; RestoreWindow's pane says *"1 encrypted set left out — it is
locked."*; nothing left → no window, a status line. Covers Restore, Validate, Verify, Restore All Sets and
drag-to-Explorer. In RestoreWindow, *Include incremental chain* is enabled for an incremental or locked set; Start needs a
target folder when one is chosen. The service's pre-flight and `OnAskPassword` remain the safety net.

## 6. RenameBackupSetWindow [→ Design-Encryption 11.1.6]

| Field | Rule |
|---|---|
| **Current password** (encrypted sets) | always required; checked on focus loss and on OK — `VerifyPasswordAsync(unlock: true)` for a locked set (its name then fills in), `unlock: false` for an unlocked one (a held key proves nothing) |
| Name | disabled while locked |
| Change password (expander) | NewPasswordInput |

Warning pane: *"Enter the current password…"* (Info, until verified); with the expander open: the lost-password advice
(Warning), *"The new password applies to the whole chain of N sets — the incremental sets and volume continuations this
set belongs to."* (Info, N from the sets' flags), *"After the change the set locks; the new password opens it."* (Info),
and for a series the note that other volumes keep the old password (Info). OK passes the current password only for a
change (the service proves it again). Not opened on a series TOC (a message asks to reread first). Replaces the name
prompt for every set; **[Help ▶]** / F1 → `dialog.rename-backup-set`.

## 7. Keys across media changes [→ Design-Encryption 11.1.7]

`TapeService` sets `RetainKeysAcrossMedia = true`: the old TOC's sets lock, the keys stay; every newly loaded TOC
reopens what they fit — the same cartridge reinserted, another volume of the series, a reread after a series TOC.
Forget Passwords and closing TapeWin drop them.

## 8. Delete, Scan [→ Design-Encryption 11.1.8]

- **DeleteBackupSetsWindow:** no password. Locked sets listed with 🔒, file count "—"; the size line adds *"+ N locked
  set(s)"*; the pane adds *"N of them are encrypted — deleting needs no password."* (Info).
- **ScanMediaWindow:** no option. **ScanResultWindow:** no unlock — fragments carry no key; encrypted set headers read
  *"🔒 encrypted (Software (AES-256-GCM))"*; a TOC row *"recovered — 3 set(s), 1 encrypted"*; after **Use this TOC** the
  sets show locked (or reopen with retained keys).

## 9. Locked-set safety in the views — as fixed [→ Design-Encryption 11.1.9]

| Code | Rule |
|---|---|
| BackupSetListItem | every property asks IsLocked first: placeholder / "—" / 0; on Tape from the stub |
| TapeTreeItemViewModel | `RefreshFromSet(toc, index)`; `SetTOC` kept to find the node again after a rebuild |
| AppendAfterOption, DeleteFromSetOption | DisplayName, no count, IsLocked |
| MediaUsageBarPresenter | DescribeUsage — never the TOC |
| DeleteBackupSetsViewModel | counts skip locked sets, counted separately |
| BackupViewModel | sources-from-set and load-from-last guarded |
| MainViewModel | placeholder pane; "check all" via `SetCheckedWhole`; rename via §6 |
| TOCView | `TryGetOrCreate` → null for a locked set; views keep a **copy** of the file list (Lock() empties the live one) |
| ScanResultViewModel | DisplayName for harvested sets |
| All | **TOC barrier**: no TOC read while a service operation runs (`IsTocQuiet`, `AssertTocQuiet`) |

## 10. Library additions used by TapeWin [→ Design-Encryption 11.1.10]

Now part of Design-Encryption §10: VerifyPasswordAsync, UnlockWithPasswordAsync, PasswordRetryDelay, HasPasswords,
RetainKeysAcrossMedia, ForgetPasswordsAsync, the password-change proof, MaxPasswordAttempts, PasswordRequest.PartOfSeries,
the prompt loops' "ask first, then wait", E19.

## 11. The CLI [→ Design-Encryption 11.1.11]

Moved to Design-Encryption §11.2. TapeWin and tapecon share the service: the W8 delay and E19 apply to both;
retention is TapeWin's.

## 12. Implementation history [→ Design-Encryption 11.1.12]

| Step | Content |
|---|---|
| 1 | library additions (§10) + ServiceUnlockTests |
| 2 | locked sets render without exceptions (§9) |
| 3 | multi-set selection (§2) — grew into the TOC barrier, the service-fed usage bar, the series TOC, the sets-list context menu, RestoreWindow fixes |
| 4 | PasswordInput, PasswordDialog, unlock gestures, menus, OnAskPassword, retention |
| 5 | BackupWindow (§4) incl. password reuse; FilesUpToDate (incremental "unchanged" files) |
| 6 | Restore pre-unlock (§5), RenameBackupSetWindow (§6); the countdown moved into PasswordInput; IPasswordSource unified |
| 7 | Delete / Scan (§8); set icon overlays (§1.3); the single message line; help pane in both new dialogs; help topics |

## 13. Constraints from the TOC barrier and the service lock [→ Design-Encryption 11.1.13]

- **OnAskPassword runs inside an operation** — its dialog gets no validator (deadlock); the service checks. Every window
  opened between operations checks in place (W3).
- **Unlock and Forget run as their own busy scope**; the service raises TocChanged; the TOC barrier refreshes tree,
  panes and selection when the scope ends — the view stays on the same set, checks carry over.
- **Keys survive a series TOC**; rereading the loaded volume re-applies them.
- **Help in the service-mode dialog is safe:** the pane reads embedded topics only; action links go to main-window
  commands, disabled while busy.

## 14. Help [new]

| Topic | Content |
|---|---|
| concepts/encryption.md | locked vs unlocked, one password every set, retention, chains, password change, limits, icons |
| concepts/toc-space.md | why the TOC needs room; the advice messages |
| dialogs/password.md, dialogs/rename-backup-set.md | the two dialogs; `## Controls` for Reveal |
| dialogs/backup.md, restore.md, delete-backup-sets.md, scan-result.md; concepts, glossary, menus, tree view | revised |

## 15. File map [new]

| File | Role |
|---|---|
| Controls/PasswordInput.xaml + .cs, NewPasswordInput.xaml + .cs | §3.1 |
| PasswordDialog.xaml + .cs | §3.2 |
| RenameBackupSetWindow.xaml + .cs, ViewModels/RenameBackupSetViewModel.cs | §6 |
| ViewModels/BackupViewModel.Encryption.cs (+ BackupViewModel.cs, BackupWindow.xaml) | §4 |
| ViewModels/MainViewModel.Encryption.cs, .Restore.cs, .TocBarrier.cs | §1.2, §2, §5, §13 |
| ViewModels/RestoreViewModel.cs | §5 |
| Models/TOCView.cs, SelectionText.cs, BackupSetListItem.cs; ViewModels/TapeTreeItemViewModel.cs | §2, §9 |
| TapeIcons.cs | §1.3 |
| Services/WpfServiceHost.cs (OnAskPassword), Services/TapeService.cs (retention) | §3.2, §7 |
| Resources/Help/… | §14 |
