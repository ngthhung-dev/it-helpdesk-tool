; Bộ cài IT Helpdesk Tool — Inno Setup 6.5+
; Bước 1: dotnet publish src\ITHelpdeskTool -c Release -o publish
;         dotnet build src\ITHelpdeskTool.Lite -c Release -o lite  +  tools\get-wimlib.ps1 -Dest lite
; Một bộ cài cho mọi máy: Windows 10 2004+ 64-bit cài app đầy đủ (.NET 10, tự chứa runtime);
; Windows 7 SP1 / 8 / 8.1 (và Windows 10 cũ / 32-bit) cài bản nhẹ ITHelpdeskToolLite.exe (.NET Framework 4.8, chỉ có Cài lại sạch).
; Bước 2: mở file này bằng Inno Setup Compiler rồi Build (Ctrl+F9)

#ifndef AppVersion
  #define AppVersion "1.0.0"
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
VersionInfoDescription={#AppName} by {#AppAuthor}
VersionInfoCopyright=Copyright © 2026 {#AppAuthor}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ITHelpdeskTool-Setup-{#AppVersion}
SetupIconFile=..\src\ITHelpdeskTool\Assets\app.ico
UninstallDisplayIcon={app}\{code:ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x86compatible x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=6.1sp1
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
; Giao diện bộ cài tiếng Việt (bản dịch tiếng Việt của Inno Setup, Files/Languages/Unofficial/Vietnamese.isl)
Name: "vi"; MessagesFile: "Languages\Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài Desktop"; GroupDescription: "Tuỳ chọn:"

[Files]
; Bản publish self-contained single-file: không cần cài .NET riêng
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseFull
; Bản nhẹ (Windows 7 / 8 / 8.1): app .NET Framework 4.8 + wimlib-imagex (GPLv3 / LGPLv3, kèm giấy phép và nguồn trong thư mục wimlib)
Source: "..\lite\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseLite

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{code:ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{code:ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{code:ExeName}"; Description: "Mở {#AppName}"; Flags: nowait postinstall skipifsilent shellexec
; Cập nhật từ trong app (UpdateService chạy bộ cài /SILENT ... /relaunch=1): cài xong tự mở lại app
Filename: "{app}\{code:ExeName}"; Flags: nowait skipifnotsilent shellexec; Check: Relaunch

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// App đầy đủ cần Windows 10 bản 2004 (19041) trở lên, 64-bit; máy khác dùng bản nhẹ
function UseFull: Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := IsX64Compatible and ((V.Major > 10) or ((V.Major = 10) and (V.Build >= 19041)));
end;

function UseLite: Boolean;
begin
  Result := not UseFull;
end;

function ExeName(Param: String): String;
begin
  if UseFull then Result := '{#AppExe}' else Result := '{#LiteExe}';
end;

// .NET Framework 4.8 (Release >= 528040) — Windows 7 SP1 chưa có sẵn
function HasDotNet48: Boolean;
var
  R: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', R) and (R >= 528040);
end;

function InitializeSetup: Boolean;
var
  Code: Integer;
begin
  Result := True;
  if UseLite and not HasDotNet48 then
  begin
    if MsgBox('Máy này (Windows 7 / 8 / 8.1) sẽ cài bản nhẹ của {#AppName}, cần .NET Framework 4.8 của Microsoft nhưng máy chưa có.' + #13#10#13#10 +
              'Bấm Yes để mở trang tải .NET Framework 4.8 (cài xong khởi động lại máy rồi chạy lại bộ cài này).',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, Code);
    Result := False;
  end;
end;