; -----------------------------------------------------------------------------
; PulseRAM — Inno Setup Script
; Intelligent Kernel Memory Optimizer (WinUI 3 + NT Kernel Native)
; -----------------------------------------------------------------------------

#define MyAppName "PulseRAM"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "JoyBoy"
#define MyAppURL "https://github.com/JoyBoy/PulseRAM"
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
OutputBaseFilename=PulseRAM-Setup-v1.0.0
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
Source: "publish_selfcontained\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,startup.log,pulseram_config.json"
Source: "publish_selfcontained\pulseram_config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "PulseRAM.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\PulseRAM.ico"; Comment: "Intelligent Kernel Memory Optimizer"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\PulseRAM.ico"; Comment: "Intelligent Kernel Memory Optimizer"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PulseRAM"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: runonstartup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Gracefully kill running PulseRAM instance before installing or uninstalling
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im PulseRAM.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('taskkill.exe', '/f /im PulseRAM.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
