; =====================================================================
;  tapecon (TapeNET CLI) — Inno Setup installer script
;  Built by packaging\build-packages.ps1, which passes the version via
;  /DAppVersion=<x.y.z.b>. Compile standalone with:
;      ISCC.exe /DAppVersion=2.0.0.0 packaging\tapecon.iss
; =====================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#define AppName "tapecon"
#define AppPublisher "TapeNET"
#define AppExeName "tapecon.exe"
#define AppUrl "https://github.com/avk1im/TapeNET"

[Setup]
AppId={{B3D2A8F1-7C4E-4A2D-9F61-9A0C1D2E3F40}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\TapeNET\tapecon
DefaultGroupName=TapeNET
DisableProgramGroupPage=yes
; CLI tool: a minimal, non-intrusive install. Allow per-user install without admin.
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=dist\tapecon\LICENSE.txt
OutputDir=installers
OutputBaseFilename=tapecon-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "Add tapecon to the PATH (run 'tapecon' from any shell)"; GroupDescription: "Command-line integration:"

[Files]
; Everything the publish step staged (exe, dlls, docs, README, LICENSE).
Source: "dist\tapecon\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\tapecon (command prompt)"; Filename: "{cmd}"; Parameters: "/K cd /d ""{app}"""; Comment: "Open a command prompt in the tapecon folder"
Name: "{group}\tapecon docs"; Filename: "{app}\README.md"

[Registry]
; Append the install dir to the (per-user or system) PATH when the task is selected.
; HKA resolves to HKCU for per-user installs and HKLM for admin installs.
Root: HKA; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
	ValueData: "{olddata};{app}"; \
	Check: NeedsAddPath('{app}'); Tasks: addtopath

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

// Runs 'dotnet --list-runtimes' and returns True if a runtime whose line
// contains RuntimeId (e.g. 'Microsoft.NETCore.App 8.') is installed.
function HasDotNetRuntime(RuntimeId: string): Boolean;
var
  TempFile: string;
  ExecOk: Boolean;
  ResultCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  TempFile := ExpandConstant('{tmp}\dotnet-runtimes.txt');
  // Use cmd /C so we can redirect the output of dotnet to a file.
  ExecOk := Exec(ExpandConstant('{cmd}'),
                 '/C dotnet --list-runtimes > "' + TempFile + '" 2>&1',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (not ExecOk) or (not FileExists(TempFile)) then
    exit; // dotnet not on PATH at all -> treat as missing.

  if LoadStringsFromFile(TempFile, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos(RuntimeId, Lines[I]) > 0 then
      begin
        Result := True;
        exit;
      end;
    end;
  end;
end;

// Verifies the prerequisite runtime before install; offers to open the
// download page and aborts when the user declines.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if HasDotNetRuntime('Microsoft.NETCore.App 8.') then
    exit;

  if MsgBox('tapecon requires the .NET 8 Runtime, which was not found on this computer.' + #13#10 + #13#10 +
            'Open the official download page now?' + #13#10 +
            '(Install the ".NET Desktop Runtime 8.0 (x64)" and then re-run this setup.)',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
  Result := False; // Abort: prerequisite missing.
end;

// Returns True only when {app} is not already present in the relevant PATH,
// so we never duplicate the entry on re-install.
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
  RootKey: Integer;
begin
  if IsAdminInstallMode then
	RootKey := HKEY_LOCAL_MACHINE
  else
	RootKey := HKEY_CURRENT_USER;

  if not RegQueryStringValue(RootKey, 'Environment', 'Path', OrigPath) then
  begin
	Result := True;
	exit;
  end;
  // Wrap in ';' on both sides for a robust, case-insensitive substring test.
  Result := Pos(';' + Lowercase(ExpandConstant(Param)) + ';',
				';' + Lowercase(OrigPath) + ';') = 0;
end;
