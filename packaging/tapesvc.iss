; =====================================================================
;  tapesvc (TapeNET remote tape service) -- Inno Setup installer script
;  Built by packaging\build-packages.ps1, which passes the version via
;  /DAppVersion=<x.y.z.b>. Compile standalone with:
;      ISCC.exe /DAppVersion=2.0.0.0 packaging\tapesvc.iss
;
;  Standalone installer for servers. The TapeNET suite installer
;  (tapenet.iss) embeds this setup and runs it silently when the user
;  selects the tapesvc component, so the service has exactly ONE owner:
;  this package, with its own uninstall entry.
; =====================================================================

#ifndef AppVersion
  #define AppVersion "2.0.0.0"
#endif

#define AppName "TapeNET tapesvc"
#define AppPublisher "TapeNET"
#define AppExeName "tapesvc.exe"
#define AppUrl "https://github.com/avk1im/TapeNET"

; Windows service identity -- keep in sync with TapeServiceNET\README.md.
#define SvcName "TapeService"
#define SvcDisplayName "TapeNET Media Service"
#define FirewallRule "TapeNET tapesvc"
#define SvcPorts "50551,50552"

[Setup]
AppId={{5B2E7C91-4A3D-4F6E-8C1B-9D0A2F3E6B74}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\TapeNET\tapesvc
DefaultGroupName=TapeNET
DisableProgramGroupPage=yes
; Registers a Windows service and a firewall rule: admin only.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=dist\tapesvc\LICENSE.txt
OutputDir=installers
OutputBaseFilename=tapesvc-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; We stop and restart the service ourselves (PrepareToInstall / ssPostInstall).
CloseApplications=no
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "service"; Description: "Install tapesvc as a Windows service and start it (automatic start, LocalSystem)"
Name: "firewall"; Description: "Allow inbound TCP {#SvcPorts} through Windows Firewall (Domain and Private networks only)"

[Files]
; Everything the publish step staged: self-contained runtime, tapesvc, sanitized appsettings.json,
;  appsettings.Tls.json.example, docs, licenses. Local overrides belong in appsettings.Production.json,
;  which this setup never ships and therefore never overwrites.
Source: "dist\tapesvc\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\tapesvc console (Administrator)"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; \
	Comment: "Run tapesvc in a console window (stop the service first)"
Name: "{group}\tapesvc README"; Filename: "{win}\notepad.exe"; Parameters: """{app}\README-tapesvc.md"""
Name: "{group}\tapesvc folder"; Filename: "{app}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Code]
#include "shared-code.iss"

function ScExe(): String;
begin
  Result := ExpandConstant('{sys}\sc.exe');
end;

function NetExe(): String;
begin
  Result := ExpandConstant('{sys}\net.exe');
end;

function NetshExe(): String;
begin
  Result := ExpandConstant('{sys}\netsh.exe');
end;

// 'sc query' returns 1060 (ERROR_SERVICE_DOES_NOT_EXIST) for an unknown service.
function ServiceExists(): Boolean;
begin
  Result := RunHidden(ScExe(), 'query {#SvcName}') = 0;
end;

// 'net stop' blocks until the service reports STOPPED, so files are free afterwards.
procedure StopService();
begin
  if ServiceExists() then
    RunHidden(NetExe(), 'stop {#SvcName}');
end;

procedure DeleteService();
begin
  if ServiceExists() then
  begin
    StopService();
    RunHidden(ScExe(), 'delete {#SvcName}');
  end;
end;

procedure RegisterService();
var
  BinPath: String;
begin
  // sc.exe syntax: "option= value"; the inner \" quote the path for the SCM.
  BinPath := 'binPath= "\"' + ExpandConstant('{app}\{#AppExeName}') + '\""';
  if ServiceExists() then
    RunHidden(ScExe(), 'config {#SvcName} ' + BinPath + ' start= auto DisplayName= "{#SvcDisplayName}"')
  else
    RunHidden(ScExe(), 'create {#SvcName} ' + BinPath + ' start= auto DisplayName= "{#SvcDisplayName}"');

  RunHidden(ScExe(), 'description {#SvcName} "TapeNET remote tape service (gRPC): exposes this machine''s tape drives to TapeWin and tapecon."');
  // Restart after a crash: 5 s delay, failure counter resets daily.
  RunHidden(ScExe(), 'failure {#SvcName} reset= 86400 actions= restart/5000/restart/5000/restart/5000');

  if RunHidden(NetExe(), 'start {#SvcName}') <> 0 then
    MsgBox('The {#SvcDisplayName} was registered but did not start.' + #13#10 +
           'Check Event Viewer > Windows Logs > Application, or run the "tapesvc console" shortcut to see the error.',
           mbError, MB_OK);
end;

procedure RemoveFirewallRule();
begin
  RunHidden(NetshExe(), 'advfirewall firewall delete rule name="{#FirewallRule}"');
end;

procedure AddFirewallRule();
begin
  RemoveFirewallRule();
  RunHidden(NetshExe(), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow protocol=TCP' +
    ' localport={#SvcPorts} profile=domain,private program="' + ExpandConstant('{app}\{#AppExeName}') + '"');
end;

// Free the files before an upgrade overwrites them.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopService();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    exit;

  SetShortcutRunAsAdmin(ExpandConstant('{group}\tapesvc console (Administrator).lnk'));

  // The tasks describe the desired end state, also on upgrade: deselecting removes.
  if WizardIsTaskSelected('service') then
    RegisterService()
  else
    DeleteService();

  if WizardIsTaskSelected('firewall') then
    AddFirewallRule()
  else
    RemoveFirewallRule();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteService();
    RemoveFirewallRule();
  end;
end;
