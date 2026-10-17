; -----------------------------------------------------------------------------
; PulseRAM — Inno Setup Script
; Intelligent Kernel Memory Optimizer (WinUI 3 + NT Kernel Native)
; -----------------------------------------------------------------------------

#define MyAppName "PulseRAM"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Hiirocchi18"
#define MyAppURL "https://github.com/Hiirocchi18/PulseRAM"
#define MyAppExeName "PulseRAM.exe"

[Setup]
AppId={{D3E8C41A-714F-4822-B129-C59FA6E1E49F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=dist
OutputBaseFilename=PulseRAM-Setup-v{#MyAppVersion}
SetupIconFile=PulseRAM.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=105
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequiredOverridesAllowed=dialog commandline
PrivilegesRequired=lowest
CloseApplications=yes
CloseApplicationsFilter=PulseRAM.exe
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (WinUI 3 Memory Optimizer)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "runonstartup"; Description: "Launch PulseRAM minimized in System Tray when Windows starts"; GroupDescription: "Windows Integration:"

[Files]
; User settings are stored in %LocalAppData%\PulseRAM, so never ship a pulseram_config.json from a dev run.
Source: "publish_selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,startup.log,pulseram_config.json,pulseram_defaults.json"
; Defaults are always refreshed on upgrade; a v1.0 pulseram_config.json already in {app} is imported once by the app.
Source: "pulseram_defaults.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "PulseRAM.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\PulseRAM.ico"; Comment: "Intelligent Kernel Memory Optimizer"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\PulseRAM.ico"; Comment: "Intelligent Kernel Memory Optimizer"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PulseRAM"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: runonstartup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the optional elevated logon task created by "Windows Startup" when PulseRAM ran as administrator.
; Fails harmlessly if it does not exist or the uninstaller is not elevated.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""PulseRAM Elevated Startup"" /F"; Flags: runhidden; RunOnceId: "DeleteElevatedStartupTask"

[UninstallDelete]
Type: files; Name: "{localappdata}\PulseRAM\frozen_processes.json"

[Code]
// PulseRAM 1.1+ understands --thaw-frozen. Older builds would open their full UI instead
// (and block this installer), so check the file version first.
function InstalledExeSupportsThaw(const ExePath: String): Boolean;
var
  VersionMS, VersionLS: Cardinal;
begin
  Result := FileExists(ExePath) and GetVersionNumbers(ExePath, VersionMS, VersionLS)
    and (VersionMS >= $00010001);
end;

function RegisterWindowMessage(lpString: String): Cardinal;
  external 'RegisterWindowMessageW@user32.dll stdcall';

// Resume any apps PulseRAM has Cryo-Frozen, then stop it. Force-killing first would leave those
// apps suspended with nothing able to resume them.
procedure ThawFrozenAppsAndStopPulseRAM();
var
  ResultCode: Integer;
  ExePath: String;
  ExitMsg: Cardinal;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  if InstalledExeSupportsThaw(ExePath) then
  begin
    // 1) Ask a running PulseRAM (even an elevated one, which taskkill from this non-elevated
    //    installer cannot stop) to thaw everything and exit cleanly.
    ExitMsg := RegisterWindowMessage('PulseRAM.ExitForInstaller.v1');
    if ExitMsg <> 0 then
    begin
      PostBroadcastMessage(ExitMsg, 0, 0);
      Sleep(2500);
    end;
    // 2) Resume anything still journaled from a crashed session.
    Exec(ExePath, '--thaw-frozen', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  // 3) Last resort for older versions / hung instances.
  Exec('taskkill.exe', '/f /im PulseRAM.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ThawFrozenAppsAndStopPulseRAM();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    ThawFrozenAppsAndStopPulseRAM();
end;
