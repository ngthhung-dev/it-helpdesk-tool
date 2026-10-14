; Bộ cài DeskMate — Inno Setup 6.5+
; Bước 1: dotnet publish src\DeskMate -c Release -o publish
; Bước 2: mở file này bằng Inno Setup Compiler rồi Build (Ctrl+F9)

#define AppName "DeskMate"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppExe "DeskMate.exe"

[Setup]
AppId={{6C1E3E0A-5B8C-4F2A-9A7D-DE5C0A11F001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DeskMate
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DeskMate-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.19041
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài Desktop"; GroupDescription: "Tuỳ chọn:"

[Files]
; Bản publish self-contained single-file: không cần cài .NET riêng
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Mở {#AppName}"; Flags: nowait postinstall skipifsilent shellexec
