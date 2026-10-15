; Bộ cài IT Helpdesk Tool — Inno Setup 6.5+
; Bước 1: dotnet publish src\ITHelpdeskTool -c Release -o publish
; Bước 2: mở file này bằng Inno Setup Compiler rồi Build (Ctrl+F9)

#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif
#define AppName "IT Helpdesk Tool"
#define AppExe "ITHelpdeskTool.exe"
#define AppAuthor "ngthhung"
#define AppEmail "ngthhung.dev@gmail.com"
#define AppPhone "0968 799 365"

[Setup]
AppId={{6C1E3E0A-5B8C-4F2A-9A7D-DE5C0A11F001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppAuthor}
AppCopyright=Copyright © 2026 {#AppAuthor}
AppContact={#AppEmail}
AppSupportPhone={#AppPhone}
AppPublisherURL=mailto:{#AppEmail}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppAuthor}
VersionInfoDescription={#AppName} by {#AppAuthor} – Setup
VersionInfoCopyright=Copyright © 2026 {#AppAuthor}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ITHelpdeskTool-Setup-{#AppVersion}
SetupIconFile=..\src\ITHelpdeskTool\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.19041
WizardStyle=modern

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
