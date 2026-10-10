; Driver Forge installer (Inno Setup 6). Built by build/build-installer.sh,
; which passes AppVersion and the path of the published app (PublishDir).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\app\publish"
#endif

[Setup]
AppId={{B04F2A6D-91C3-4E58-A7D2-3F86C1E95B20}
AppName=Driver Forge
AppVersion={#AppVersion}
AppVerName=Driver Forge {#AppVersion}
AppPublisher=Hillyard Tech
AppCopyright=Copyright (c) 2026 Hillyard Tech (MIT License)
DefaultDirName={autopf}\Driver Forge
DefaultGroupName=Driver Forge
DisableProgramGroupPage=yes
LicenseFile=LICENSE.txt
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=output
OutputBaseFilename=DriverForge-Setup-{#AppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\DriverForge.exe
UninstallDisplayName=Driver Forge
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Tells the app it's installed: data then goes to ProgramData, not Program Files.
Source: "installed.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\Driver Forge"

[Icons]
Name: "{autoprograms}\Driver Forge"; Filename: "{app}\DriverForge.exe"
Name: "{autodesktop}\Driver Forge"; Filename: "{app}\DriverForge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DriverForge.exe"; Description: "Start Driver Forge"; Flags: nowait postinstall skipifsilent shellexec

[Code]
// Backups are the only way to roll a driver back; ask before deleting them (keep them when silent).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\Driver Forge');
    if DirExists(Data) and not UninstallSilent then
      if MsgBox('Also delete driver backups, install history, downloads and settings?' + #13#10#13#10 + Data,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
