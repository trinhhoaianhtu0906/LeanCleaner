; Inno Setup script — builds LeanCleaner-Setup.exe (per-user install, no admin needed).
; Build: run installer\build.ps1 (publishes the self-contained exe, then compiles this script).

#define AppName "LeanCleaner"
#define AppVersion "1.0.0"
#define AppExe "LeanCleaner.exe"

[Setup]
AppId={{7B1E6D2A-4C3F-4E8B-9A51-2F6C8D0E1A47}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Trinh Hoai Anh Tu
AppCopyright=Copyright (c) 2026 Trinh Hoai Anh Tu
LicenseFile=..\LICENSE
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\LeanCleaner\LeanCleaner.ico
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài Desktop"; GroupDescription: "Biểu tượng:"

[Files]
Source: "..\publish-sc\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Gỡ cài đặt {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Mở {#AppName} ngay"; Flags: nowait postinstall skipifsilent
