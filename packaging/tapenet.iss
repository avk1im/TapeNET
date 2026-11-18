; =====================================================================
;  TapeNET suite -- Inno Setup installer script
;  Components: tapecon (CLI), TapeWin (WPF GUI), tapesvc (remote service, optional).
;  Built by packaging\build-packages.ps1, which passes the versions via
;  /DAppVersion=<x.y.z.b> /DSvcVersion=<x.y.z.b>. Compile standalone with:
;      ISCC.exe /DAppVersion=2.0.0.0 /DSvcVersion=2.0.0.0 packaging\tapenet.iss
;  Requires installers\tapesvc-<SvcVersion>-setup.exe (compile tapesvc.iss first).
;
;  The tapesvc component embeds the standalone tapesvc installer and runs it
;  silently, so the service keeps a single owner and its own uninstall entry.
; =====================================================================

#ifndef AppVersion
  #define AppVersion "2.0.0.0"
#endif
#ifndef SvcVersion
  #define SvcVersion AppVersion
#endif

#define AppName "TapeNET"
#define AppPublisher "TapeNET"
#define AppUrl "https://github.com/avk1im/TapeNET"
#define SvcSetup "tapesvc-" + SvcVersion + "-setup.exe"

#define EnvKey "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"

[Setup]
AppId={{8F1D2A6B-3C4E-4D5F-9A7B-2E6C1D0F4A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\TapeNET
DefaultGroupName=TapeNET
DisableProgramGroupPage=yes
; System-wide PATH, all-users shortcuts, optional service: admin only.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=dist\common\LICENSE.txt
OutputDir=installers
OutputBaseFilename=TapeNET-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Broadcast WM_SETTINGCHANGE so new shells see the PATH change without a reboot.
ChangesEnvironment=yes
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\TapeWin\TapeWin.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "client"; Description: "Client: tapecon and TapeWin"
Name: "full"; Description: "Client and server: tapecon, TapeWin, and the tapesvc remote service"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "tapecon"; Description: "tapecon -- command-line backup utility"; Types: client full
Name: "tapewin"; Description: "TapeWin -- graphical backup manager"; Types: client full
Name: "tapesvc"; Description: "tapesvc -- remote tape service (installs as a separate package)"; Types: full

[Tasks]
Name: "addpath"; Description: "Add tapecon to the system PATH"; GroupDescription: "tapecon:"; Components: tapecon
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "TapeWin:"; Components: tapewin; Flags: unchecked
Name: "svcservice"; Description: "Run tapesvc as a Windows service (automatic start)"; GroupDescription: "tapesvc:"; Components: tapesvc
Name: "svcfirewall"; Description: "Allow inbound TCP 50551,50552 (Domain and Private networks only)"; GroupDescription: "tapesvc:"; Components: tapesvc

[Files]
; Shared docs: README, LICENSE, THIRD-PARTY-NOTICES, docs\FCL-Specification.md.
Source: "dist\common\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Self-contained app folders (runtime, app, native DLLs, licenses\).
Source: "dist\tapecon\*"; DestDir: "{app}\tapecon"; Components: tapecon; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "dist\tapewin\*"; DestDir: "{app}\TapeWin"; Components: tapewin; Flags: ignoreversion recursesubdirs createallsubdirs
; The standalone tapesvc installer, run silently from [Run] and deleted afterwards.
Source: "installers\{#SvcSetup}"; DestDir: "{tmp}"; Components: tapesvc; Flags: deleteafterinstall

[Icons]
; Physical tape access needs elevation; virtual drives and remote hosts do not.
Name: "{group}\TapeWin"; Filename: "{app}\TapeWin\TapeWin.exe"; WorkingDir: "{app}\TapeWin"; Components: tapewin
Name: "{group}\TapeWin (Administrator)"; Filename: "{app}\TapeWin\TapeWin.exe"; WorkingDir: "{app}\TapeWin"; \
	Comment: "TapeWin with access to physical tape drives"; Components: tapewin
Name: "{autodesktop}\TapeWin"; Filename: "{app}\TapeWin\TapeWin.exe"; WorkingDir: "{app}\TapeWin"; Components: tapewin; Tasks: desktopicon
Name: "{group}\tapecon prompt"; Filename: "{cmd}"; Parameters: "/K title tapecon"; WorkingDir: "{app}\tapecon"; \
	Comment: "Command prompt in the tapecon folder"; Components: tapecon
Name: "{group}\tapecon prompt (Administrator)"; Filename: "{cmd}"; Parameters: "/K title tapecon (Administrator)"; WorkingDir: "{app}\tapecon"; \
	Comment: "Elevated command prompt for physical tape drives"; Components: tapecon
Name: "{group}\tapecon docs"; Filename: "{cmd}"; Parameters: "/K tapecon docs"; WorkingDir: "{app}\tapecon"; \
	Comment: "tapecon's built-in documentation topics"; Components: tapecon
; .md files often lack a file association; Notepad always works.
Name: "{group}\TapeNET README"; Filename: "{win}\notepad.exe"; Parameters: """{app}\README.md"""
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Registry]
; System PATH lives under HKLM\{#EnvKey} -- NOT HKLM\Environment.
Root: HKLM; Subkey: "{#EnvKey}"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\tapecon"; \
	Tasks: addpath; Components: tapecon; Check: NeedsAddPath(ExpandConstant('{app}\tapecon'))

[Run]
Filename: "{tmp}\{#SvcSetup}"; Parameters: "{code:SvcSetupParams}"; StatusMsg: "Installing the tapesvc remote service..."; \
	Components: tapesvc; Flags: waituntilterminated
Filename: "{app}\TapeWin\TapeWin.exe"; Description: "{cm:LaunchProgram,TapeWin}"; Components: tapewin; \
	Flags: nowait postinstall skipifsilent runasoriginaluser unchecked

[Code]
#include "shared-code.iss"

// True when Dir is not yet an entry of the system PATH (case-insensitive, trailing '\' ignored).
function PathContains(const PathValue, Dir: String): Boolean;
var
  Rest, Item: String;
  P: Integer;
begin
  Result := False;
  Rest := PathValue;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Delete(Rest, 1, P);
    end;
    if (Item <> '') and (CompareText(RemoveBackslashUnlessRoot(Item), RemoveBackslashUnlessRoot(Dir)) = 0) then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function NeedsAddPath(Dir: String): Boolean;
var
  OrigPath: String;
begin
  if not RegQueryStringValue(HKLM, '{#EnvKey}', 'Path', OrigPath) then
    Result := True
  else
    Result := not PathContains(OrigPath, Dir);
end;

// Rebuilds PATH without Dir; keeps every other entry and its order.
function RemoveFromPath(const PathValue, Dir: String): String;
var
  Rest, Item: String;
  P: Integer;
begin
  Result := '';
  Rest := PathValue;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Delete(Rest, 1, P);
    end;
    if (Item <> '') and (CompareText(RemoveBackslashUnlessRoot(Item), RemoveBackslashUnlessRoot(Dir)) <> 0) then
    begin
      if Result <> '' then
        Result := Result + ';';
      Result := Result + Item;
    end;
  end;
end;

// Command line for the embedded tapesvc setup: silent, same root folder, tasks mirrored.
function SvcSetupParams(Param: String): String;
var
  Tasks: String;
begin
  Tasks := '';
  if WizardIsTaskSelected('svcservice') then
    Tasks := 'service';
  if WizardIsTaskSelected('svcfirewall') then
  begin
    if Tasks <> '' then
      Tasks := Tasks + ',';
    Tasks := Tasks + 'firewall';
  end;
  Result := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' +
            ' /DIR="' + ExpandConstant('{app}\tapesvc') + '"' +
            ' /TASKS="' + Tasks + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    exit;
  if WizardIsComponentSelected('tapewin') then
    SetShortcutRunAsAdmin(ExpandConstant('{group}\TapeWin (Administrator).lnk'));
  if WizardIsComponentSelected('tapecon') then
    SetShortcutRunAsAdmin(ExpandConstant('{group}\tapecon prompt (Administrator).lnk'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath: String;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  if RegQueryStringValue(HKLM, '{#EnvKey}', 'Path', OrigPath) then
    if PathContains(OrigPath, ExpandConstant('{app}\tapecon')) then
      RegWriteExpandStringValue(HKLM, '{#EnvKey}', 'Path', RemoveFromPath(OrigPath, ExpandConstant('{app}\tapecon')));
end;
