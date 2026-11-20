// =====================================================================
//  Shared [Code] helpers for tapenet.iss and tapesvc.iss.
//  Included from inside the [Code] section of both scripts:
//      #include "shared-code.iss"
//  The text lands inside [Code], so Pascal Script rules apply: comments
//  are // or { }, never the ';' of the setup sections.
// =====================================================================

// Marks a shortcut (.lnk) as "Run as administrator" -- the same checkbox as
//  Properties > Shortcut > Advanced. Inno Setup has no [Icons] flag for this, so we
//  set SLDF_RUNAS_USER (0x2000) in the ShellLinkHeader LinkFlags (offset 0x14,
//  little-endian): bit 0x20 of the byte at offset 0x15 (21).
//  Patches that single byte in place via TFileStream. Pascal Script cannot cast an
//  integer to AnsiChar, so the byte travels through a 1-char string buffer: ReadBuffer
//  fills its low byte, Chr/Ord stay in the native Char type, WriteBuffer writes it back.
procedure SetShortcutRunAsAdmin(const LnkPath: String);
var
  Buffer: String;
  Stream: TStream;
begin
  if not FileExists(LnkPath) then
  begin
    Log('SetShortcutRunAsAdmin: not found ' + LnkPath);
    exit;
  end;
  try
    Stream := TFileStream.Create(LnkPath, fmOpenReadWrite);
    try
      Stream.Seek(21, soFromBeginning);
      SetLength(Buffer, 1);
      Stream.ReadBuffer(Buffer, 1);
      Buffer[1] := Chr(Ord(Buffer[1]) or $20);
      Stream.Seek(-1, soFromCurrent);
      Stream.WriteBuffer(Buffer, 1);
      Log('SetShortcutRunAsAdmin: set on ' + LnkPath);
    finally
      Stream.Free;
    end;
  except
    Log('SetShortcutRunAsAdmin: failed on ' + LnkPath + ': ' + GetExceptionMessage);
  end;
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
