; =====================================================================
;  TapeWin (TapeNET WPF GUI) — Inno Setup installer script
;  Built by packaging\build-packages.ps1, which passes the version via
;  /DAppVersion=<x.y.z.b>. Compile standalone with:
;      ISCC.exe /DAppVersion=1.2.0.0 packaging\tapewin.iss
; =====================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif

#define AppName "TapeWin"
#define AppPublisher "TapeNET"
#define AppExeName "TapeWin.exe"
#define AppUrl "https://github.com/avk1im/TapeNET"

[Setup]
AppId={{C4E3B9A2-8D5F-4B3E-A072-1B2C3D4E5F60}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\TapeNET\TapeWin
DefaultGroupName=TapeNET
DisableProgramGroupPage=yes
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=dist\tapewin\LICENSE.txt
OutputDir=installers
OutputBaseFilename=TapeWin-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything the publish step staged (exe, dlls, docs, README, LICENSE).
Source: "dist\tapewin\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

// Runs 'dotnet --list-runtimes' and returns True if a runtime whose line
// contains RuntimeId (e.g. 'Microsoft.WindowsDesktop.App 8.') is installed.
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

// Verifies the .NET 8 Desktop Runtime (required by WPF) before install;
// offers to open the download page and aborts when the user declines.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if HasDotNetRuntime('Microsoft.WindowsDesktop.App 8.') then
    exit;

  if MsgBox('TapeWin requires the .NET 8 Desktop Runtime, which was not found on this computer.' + #13#10 + #13#10 +
            'Open the official download page now?' + #13#10 +
            '(Install the ".NET Desktop Runtime 8.0 (x64)" and then re-run this setup.)',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
  Result := False; // Abort: prerequisite missing.
end;
