; =====================================================================
;  Shared [Code] helpers for tapenet.iss and tapesvc.iss.
;  Included from inside the [Code] section of both scripts:
;      #include "shared-code.iss"
; =====================================================================

// Marks a shortcut (.lnk) as "Run as administrator" -- the same checkbox as
//  Properties > Shortcut > Advanced. Inno Setup has no [Icons] flag for this, so we
//  set SLDF_RUNAS_USER (0x2000) in the ShellLinkHeader LinkFlags (offset 0x14,
//  little-endian): bit 0x20 of the byte at offset 0x15, i.e. 1-based index 22.
procedure SetShortcutRunAsAdmin(const LnkPath: String);
var
  Data: AnsiString;
begin
  if not LoadStringFromFile(LnkPath, Data) then
  begin
    Log('SetShortcutRunAsAdmin: cannot read ' + LnkPath);
    exit;
  end;
  if Length(Data) < 22 then
  begin
    Log('SetShortcutRunAsAdmin: unexpected size of ' + LnkPath);
    exit;
  end;
  Data[22] := AnsiChar(Ord(Data[22]) or $20);
  if not SaveStringToFile(LnkPath, Data, False) then
    Log('SetShortcutRunAsAdmin: cannot write ' + LnkPath);
end;

// Runs a system tool hidden and returns its exit code (-1 when it cannot start).
function RunHidden(const Exe, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
  Log(Format('RunHidden: %s %s -> %d', [Exe, Params, Result]));
end;
