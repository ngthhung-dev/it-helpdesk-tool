using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Printing;

/// <summary>
/// Một thao tác sửa lỗi máy in. <see cref="Script"/> là PowerShell (chạy với quyền admin của app);
/// <see cref="Open"/> là lệnh mở công cụ của Windows thay cho script.
/// <see cref="Warn"/> khác rỗng: thao tác hạ mức bảo mật / ảnh hưởng rộng — phải hỏi lại trước khi chạy.
/// </summary>
public sealed record PrinterFix(string Id, string Group, string Title, string Description, string Script = "",
    string Warn = "", string Open = "", bool ShowOutput = false, string Glyph = "")
{
    public bool IsWarn => Warn.Length > 0;
    public bool IsCode => Title.StartsWith("0x") || Group == PrinterFixCatalog.Codes;
}

public sealed class PrinterFixGroup(string name, string note, IEnumerable<PrinterFix> items)
{
    public string Name { get; } = name;
    public string Note { get; } = note;
    public List<PrinterFix> Items { get; } = items.ToList();
}

/// <summary>Bộ sửa lỗi máy in: theo mã lỗi, dịch vụ, tính năng Windows, tường lửa, spooler/driver, RPC, chẩn đoán, theo hãng.</summary>
public static class PrinterFixCatalog
{
    public const string Codes = "Sửa theo mã lỗi";

    /// <summary>Hàm dùng chung cho mọi script.</summary>
    public const string Prelude = """
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        $spool = Join-Path $env:windir 'System32\spool'
        function Svc([string]$n, [string]$start = 'Automatic') {
          $s = Get-Service -Name $n -ErrorAction SilentlyContinue
          if (-not $s) { "• ${n}: máy này không có dịch vụ này"; return }
          if ($start -ne 'Keep') { Set-Service -Name $n -StartupType $start -ErrorAction SilentlyContinue }
          if ($start -eq 'Keep' -and $s.Status -eq 'Running') { "• $($s.DisplayName) ($n): đang chạy"; return }
          if ($s.Status -eq 'Running') { Restart-Service -Name $n -Force -ErrorAction SilentlyContinue } else { Start-Service -Name $n -ErrorAction SilentlyContinue }
          $s.Refresh(); "• $($s.DisplayName) ($n): $($s.Status)"
        }
        function RegSet([string]$p, [string]$n, $v, [string]$t = 'DWord') {
          if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
          New-ItemProperty -Path $p -Name $n -Value $v -PropertyType $t -Force | Out-Null
          "• $($p -replace '^HKLM:\\','HKLM\' -replace '^HKCU:\\','HKCU\')\$n = $v"
        }
        function RegDel([string]$p, [string]$n) {
          if (Get-ItemProperty -Path $p -Name $n -ErrorAction SilentlyContinue) { Remove-ItemProperty -Path $p -Name $n -Force; "• Đã xoá $n (về mặc định)" }
          else { "• ${n}: đang là mặc định" }
        }
        function Feat([string]$n, [bool]$on = $true) {
          $f = Get-WindowsOptionalFeature -Online -FeatureName $n -ErrorAction SilentlyContinue
          if (-not $f) { "• ${n}: Windows bản này không có tính năng này"; return }
          if ($on -and $f.State -eq 'Enabled') { "• $($f.DisplayName): đã bật sẵn"; return }
          if (-not $on -and $f.State -ne 'Enabled') { "• $($f.DisplayName): đang tắt"; return }
          try {
            $r = if ($on) { Enable-WindowsOptionalFeature -Online -FeatureName $n -All -NoRestart -ErrorAction Stop }
                 else { Disable-WindowsOptionalFeature -Online -FeatureName $n -NoRestart -ErrorAction Stop }
            "• $($f.DisplayName): đã " + $(if ($on) { 'bật' } else { 'tắt' }) + $(if ($r.RestartNeeded) { ' — cần khởi động lại máy' } else { '' })
          } catch { "• ${n}: lỗi — $($_.Exception.Message)" }
        }
        function FwGroup([string]$g, [string]$label) {
          $rules = @(Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue | Where-Object { $_.Profile -ne 'Public' })
          if ($rules.Count -eq 0) { "• ${label}: không tìm thấy nhóm luật"; return }
          $rules | Enable-NetFirewallRule
          "• Tường lửa: đã bật $($rules.Count) luật “$label” cho mạng Private/Domain"
        }
        function FwPort([int]$port, [string]$label) {
          $name = "IT Helpdesk Tool - $label (TCP $port)"
          Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
          foreach ($dir in 'Inbound', 'Outbound') {
            New-NetFirewallRule -DisplayName $name -Direction $dir -Protocol TCP -LocalPort $(if ($dir -eq 'Inbound') { $port } else { 'Any' }) `
              -RemotePort $(if ($dir -eq 'Outbound') { $port } else { 'Any' }) -Action Allow -Profile Private, Domain | Out-Null
          }
          "• Tường lửa: đã mở cổng TCP $port ($label) cho mạng Private/Domain"
        }
        function ClearQueue {
          Stop-Service Spooler -Force -ErrorAction SilentlyContinue
          $files = @(Get-ChildItem (Join-Path $spool 'PRINTERS') -File -ErrorAction SilentlyContinue)
          $files | Remove-Item -Force -ErrorAction SilentlyContinue
          Start-Service Spooler -ErrorAction SilentlyContinue
          "• Đã xoá $($files.Count) file lệnh in kẹt trong spool\PRINTERS và bật lại Print Spooler"
        }
        function PrivateNet {
          $p = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Public' })
          foreach ($x in $p) { Set-NetConnectionProfile -InterfaceIndex $x.InterfaceIndex -NetworkCategory Private -ErrorAction SilentlyContinue; "• Mạng “$($x.Name)”: Public → Private" }
          if ($p.Count -eq 0) { '• Mạng đang dùng: đã là Private/Domain' }
        }
        $rpcPol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\RPC'
        $printKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print'
        $pnp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint'

        """;

    // ---------- Các khối dùng lại ----------
    private const string ClientRpc = """
        RegSet $rpcPol 'RpcUseNamedPipeProtocol' 1
        RegSet $rpcPol 'RpcProtocols' 7
        RegSet $rpcPol 'ForceKerberosForRpc' 0
        """;
    private const string ShareFirewall = """
        PrivateNet
        FwGroup '@FirewallAPI.dll,-28502' 'File and Printer Sharing'
        FwGroup '@FirewallAPI.dll,-32752' 'Network Discovery'
        """;
    private const string DiscoverySvcs = """
        Svc 'fdPHost'; Svc 'FDResPub'; Svc 'SSDPSRV' 'Manual'; Svc 'upnphost' 'Manual'
        Svc 'LanmanServer'; Svc 'LanmanWorkstation'
        """;
    private const string Spooler = "Svc 'Spooler'";
    private const string Rpc11b = "RegSet $printKey 'RpcAuthnLevelPrivacyEnabled' 0";
    private const string WarnRpc11b =
        "Hạ mức xác thực RPC của dịch vụ in (bản vá CVE-2021-1678) trên MÁY ĐANG CHIA SẺ máy in để máy khác kết nối được.\n\n" +
        "Microsoft khuyến nghị cập nhật Windows đầy đủ cho cả hai máy thay vì cách này. Chỉ dùng trong mạng nội bộ tin cậy. Gỡ bằng “Khôi phục RPC mặc định”.";
    private const string WarnPnp =
        "Cho phép người dùng thường tự cài driver từ máy chia sẻ mà không hỏi quyền admin. Đây chính là điểm yếu PrintNightmare (CVE-2021-34527): " +
        "máy chia sẻ bị chiếm quyền có thể cài mã độc vào máy này.\n\nChỉ bật khi cần kết nối máy in, kết nối xong bấm “Khoá lại Point and Print”.";
    private const string WarnGuest =
        "Cho phép truy cập thư mục/máy in chia sẻ bằng tài khoản Guest không mật khẩu (NAS, máy photocopy cũ). Kẻ gian trong cùng mạng có thể giả máy chủ để lừa máy này. " +
        "Chỉ dùng trong mạng nội bộ tin cậy.";
    private const string WarnSmb1 =
        "SMB 1.0 là giao thức cũ đã bị Microsoft bỏ vì lỗ hổng (mã độc WannaCry lây qua SMB 1.0). Chỉ bật khi máy photocopy/NAS đời cũ không scan được vào máy tính, " +
        "và tắt lại khi không cần. Cần khởi động lại máy.";

    public static readonly IReadOnlyList<PrinterFix> All =
    [
        // ===== Sửa theo mã lỗi =====
        new("code.comm", Codes, "Communication Error", "Lỗi giao tiếp USB / máy in không phản hồi — xoá lệnh in kẹt, khởi động lại Spooler và PrintNotify",
            "ClearQueue\nSvc 'PrintNotify' 'Manual'\nSvc 'DeviceInstall' 'Manual'"),
        new("code.7c", Codes, "0x0000007c", "Không kết nối được máy in chia sẻ (thao tác không hợp lệ) — sửa RPC phía máy kết nối + khởi động lại Spooler",
            ClientRpc + "\n" + Spooler),
        new("code.11b", Codes, "0x0000011b", "Máy khác không kết nối được máy in chia sẻ — chạy trên MÁY ĐANG CHIA SẺ",
            Rpc11b + "\n" + Spooler, WarnRpc11b),
        new("code.709", Codes, "0x00000709", "Không đặt được máy in mặc định — tắt “Windows tự quản lý máy in mặc định”, xoá thiết lập mặc định cũ bị hỏng",
            """
            RegSet 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' 'LegacyDefaultPrinterMode' 1
            RegDel 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' 'Device'
            Svc 'Spooler'
            '• Giờ đặt lại máy in mặc định ở danh sách “Máy in trên máy”'
            """),
        new("code.40", Codes, "0x00000040", "Tên mạng không còn — bật File and Printer Sharing, Network Discovery, các dịch vụ chia sẻ",
            ShareFirewall + "\n" + DiscoverySvcs),
        new("code.bc4", Codes, "0x00000bc4", "Không tìm thấy máy in (Windows 11 22H2+) — chuyển RPC sang Named Pipes như bản Windows cũ",
            ClientRpc + "\n" + Spooler),
        new("code.6d9", Codes, "0x000006d9", "Lỗi tường lửa khi chia sẻ máy in — bật dịch vụ Windows Defender Firewall (MpsSvc)",
            "Svc 'BFE'\nSvc 'MpsSvc'\n" + Spooler),
        new("code.cannot", Codes, "Cannot Connect", "Không kết nối được máy in trong mạng — sửa đầy đủ: mạng Private, tường lửa, dịch vụ, RPC",
            ShareFirewall + "\n" + DiscoverySvcs + "\n" + ClientRpc + "\n" + Spooler),
        new("code.12", Codes, "0x00000012", "Sai tài khoản khi kết nối máy chia sẻ — sửa RPC rồi mở Credential Manager để lưu tài khoản của máy chia sẻ",
            ClientRpc + "\n" + Spooler + "\nStart-Process control.exe '/name Microsoft.CredentialManager'\n'• Đã mở Credential Manager: Windows Credentials → Add → \\\\TÊN-MÁY-CHIA-SẺ + tài khoản của máy đó'"),
        new("code.3eb", Codes, "0x000003eb", "Không cài được máy in / driver — xoá hàng đợi, sửa RPC, khởi động lại dịch vụ cài thiết bị",
            "ClearQueue\n" + ClientRpc + "\nSvc 'DeviceInstall' 'Manual'\nSvc 'DsmSvc' 'Manual'\n" + Spooler),
        new("code.3e3", Codes, "0x000003e3", "Kết nối bị huỷ — sửa đầy đủ RPC, tường lửa, dịch vụ chia sẻ, xoá hàng đợi",
            "ClearQueue\n" + ClientRpc + "\n" + ShareFirewall + "\n" + DiscoverySvcs),
        new("code.771", Codes, "0x00000771", "Máy in chia sẻ bị lỗi cấu hình — xoá hàng đợi, khởi động lại Spooler; sau đó gỡ và thêm lại máy in",
            "ClearQueue\nSvc 'PrintNotify' 'Manual'\n'• Nếu vẫn lỗi: gỡ máy in đó ở danh sách trên rồi thêm lại'"),
        new("code.bcb", Codes, "0x00000bcb", "Máy này chưa có driver cho máy in chia sẻ — tạm cho phép cài driver từ máy chia sẻ (Point and Print)",
            "RegSet $pnp 'RestrictDriverInstallationToAdministrators' 0\nRegSet $pnp 'NoWarningNoElevationOnInstall' 1\nRegSet $pnp 'UpdatePromptSettings' 2\n" + Spooler +
            "\n'• Kết nối máy in xong, bấm “Khoá lại Point and Print” ở nhóm RPC & Point and Print'", WarnPnp),
        new("code.7e", Codes, "0x0000007e", "Thiếu mscms.dll khi thêm máy in — chép mscms.dll vào thư mục driver máy in",
            """
            $map = @{ 'x64\3' = (Join-Path $env:windir 'System32\mscms.dll'); 'W32X86\3' = (Join-Path $env:windir 'SysWOW64\mscms.dll') }
            foreach ($k in $map.Keys) {
              $src = $map[$k]; $dst = Join-Path $spool "drivers\$k"
              if ((Test-Path $src) -and (Test-Path $dst)) { Copy-Item $src $dst -Force; "• Đã chép $(Split-Path $src -Leaf) → spool\drivers\$k" }
            }
            Svc 'Spooler'
            """),
        new("code.6ba", Codes, "0x000006ba", "RPC Server unavailable — bật RPC, RPC Endpoint Mapper, DCOM và Print Spooler",
            "Svc 'RpcSs' 'Keep'\nSvc 'RpcEptMapper' 'Keep'\nSvc 'DcomLaunch' 'Keep'\n" + Spooler),
        new("code.4005", Codes, "0x00004005", "Access denied khi kết nối — lưu tài khoản máy chia sẻ trong Credential Manager, sửa RPC",
            ClientRpc + "\n" + Spooler + "\nStart-Process control.exe '/name Microsoft.CredentialManager'\n'• Thêm tài khoản của máy chia sẻ ở Windows Credentials. Nếu vẫn bị chặn cài driver, dùng 0x00000bcb.'"),

        // ===== Sửa nhanh tổng hợp =====
        new("all.reset", "Sửa nhanh tổng hợp", "Reset hệ thống in", "Dừng Spooler → xoá lệnh in kẹt → dọn driver không dùng → bật lại mọi dịch vụ in. Không gỡ máy in đang có",
            """
            ClearQueue
            $used = @(Get-Printer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DriverName -Unique)
            $n = 0
            foreach ($d in @(Get-PrinterDriver -ErrorAction SilentlyContinue | Where-Object { $used -notcontains $_.Name -and $_.Name -notmatch '^Microsoft ' })) {
              Remove-PrinterDriver -Name $d.Name -PrinterEnvironment $d.PrinterEnvironment -RemoveFromDriverStore -ErrorAction SilentlyContinue; if ($?) { $n++ }
            }
            "• Đã gỡ $n driver máy in không còn máy in nào dùng"
            Svc 'Spooler'; Svc 'PrintNotify' 'Manual'
            """, Glyph: ""),
        new("all.network", "Sửa nhanh tổng hợp", "Sửa in qua mạng (đầy đủ)", "Mạng Private + tường lửa chia sẻ + dịch vụ khám phá mạng + RPC Named Pipes + khởi động lại Spooler",
            ShareFirewall + "\n" + DiscoverySvcs + "\n" + ClientRpc + "\n" + Spooler, Glyph: ""),
        new("all.firewall", "Sửa nhanh tổng hợp", "Mở tường lửa máy in", "File and Printer Sharing + Network Discovery + cổng 9100 / 515 / 631",
            ShareFirewall + "\nFwPort 9100 'Máy in RAW'\nFwPort 515 'Máy in LPR'\nFwPort 631 'Máy in IPP'", Glyph: ""),
        new("all.features", "Sửa nhanh tổng hợp", "Bật mọi tính năng in", "Printing Foundation, in qua Internet (IPP), LPR, LPD, XPS, Microsoft Print to PDF",
            "Feat 'Printing-Foundation-Features'\nFeat 'Printing-Foundation-InternetPrinting-Client'\nFeat 'Printing-Foundation-LPRPortMonitor'\nFeat 'Printing-Foundation-LPDPrintService'\nFeat 'Printing-XPSServices-Features'\nFeat 'Printing-PrintToPDFServices-Features'",
            Glyph: ""),
        new("all.services", "Sửa nhanh tổng hợp", "Khởi động lại mọi dịch vụ in", "Spooler, PrintNotify, khám phá mạng (Function Discovery, SSDP, UPnP), Server, Workstation",
            Spooler + "\nSvc 'PrintNotify' 'Manual'\n" + DiscoverySvcs, Glyph: ""),

        // ===== Dịch vụ in =====
        Restart("Spooler", "Print Spooler", "Dịch vụ in chính — gửi lệnh in tới máy in"),
        Restart("PrintNotify", "Printer Extensions and Notifications", "Thông báo và tiện ích của driver máy in", "Manual"),
        Restart("LPDSVC", "LPD Service", "Nhận lệnh in qua giao thức LPD (cần bật tính năng LPD)"),
        Restart("Fax", "Fax", "Dịch vụ gửi / nhận fax", "Manual"),
        Restart("DeviceInstall", "Device Install Service", "Cài driver khi cắm máy in USB", "Manual"),
        Restart("DsmSvc", "Device Setup Manager", "Tải driver / ứng dụng của thiết bị mới", "Manual"),
        Restart("fdPHost", "Function Discovery Provider Host", "Tìm máy in, máy tính trong mạng"),
        Restart("FDResPub", "Function Discovery Resource Publication", "Cho máy này hiện ra trong mạng (Network)"),
        Restart("SSDPSRV", "SSDP Discovery", "Tìm thiết bị UPnP / máy in WSD trong mạng", "Manual"),
        Restart("upnphost", "UPnP Device Host", "Thiết bị UPnP", "Manual"),
        Restart("LanmanServer", "Server", "Chia sẻ file / máy in của máy này cho máy khác"),
        Restart("LanmanWorkstation", "Workstation", "Kết nối tới thư mục / máy in chia sẻ của máy khác"),

        // ===== Tính năng Windows =====
        Feature("Printing-Foundation-Features", "Printing Foundation", "Nền tảng in cơ bản của Windows"),
        Feature("Printing-Foundation-InternetPrinting-Client", "Internet Printing Client", "In qua Internet / IPP (http://…:631)"),
        Feature("Printing-Foundation-LPRPortMonitor", "LPR Port Monitor", "Cổng LPR cho máy in mạng đời cũ"),
        Feature("Printing-Foundation-LPDPrintService", "LPD Print Service", "Cho máy Linux / Mac in qua máy này bằng LPD"),
        Feature("Printing-XPSServices-Features", "XPS Services", "In ra file XPS"),
        Feature("Printing-PrintToPDFServices-Features", "Microsoft Print to PDF", "Máy in ảo lưu ra PDF"),
        new("feat.smb1.on", "Tính năng Windows", "Bật SMB 1.0 (máy cũ)", "Cho máy photocopy / NAS đời cũ scan vào máy tính hoặc chia sẻ được",
            "Feat 'SMB1Protocol'", WarnSmb1, Glyph: ""),
        new("feat.smb1.off", "Tính năng Windows", "Tắt SMB 1.0 (an toàn)", "Tắt giao thức cũ — nên tắt khi không dùng máy in / NAS đời cũ",
            "Feat 'SMB1Protocol' $false", Glyph: ""),

        // ===== Tường lửa & mạng =====
        new("net.fps", "Tường lửa & mạng", "Bật File and Printer Sharing", "Mở tường lửa chia sẻ máy in / thư mục (mạng Private) và chuyển mạng đang dùng sang Private",
            "PrivateNet\nFwGroup '@FirewallAPI.dll,-28502' 'File and Printer Sharing'", Glyph: ""),
        new("net.discovery", "Tường lửa & mạng", "Bật Network Discovery", "Thấy máy tính / máy in khác trong mục Network",
            "PrivateNet\nFwGroup '@FirewallAPI.dll,-32752' 'Network Discovery'\nSvc 'fdPHost'; Svc 'FDResPub'; Svc 'SSDPSRV' 'Manual'", Glyph: ""),
        new("net.9100", "Tường lửa & mạng", "Mở cổng 9100 (RAW)", "Máy in mạng in qua cổng 9100 (HP JetDirect, Ricoh, Canon…)", "FwPort 9100 'Máy in RAW'", Glyph: ""),
        new("net.515", "Tường lửa & mạng", "Mở cổng 515 (LPR)", "Máy in mạng dùng giao thức LPR", "FwPort 515 'Máy in LPR'", Glyph: ""),
        new("net.631", "Tường lửa & mạng", "Mở cổng 631 (IPP)", "Máy in mạng dùng IPP / AirPrint", "FwPort 631 'Máy in IPP'", Glyph: ""),
        new("net.dns", "Tường lửa & mạng", "Xoá cache DNS", "Sửa lỗi gọi máy in / máy chia sẻ theo tên bị sai địa chỉ",
            "ipconfig /flushdns | Select-Object -Last 1\nnbtstat -R | Select-Object -Last 1", Glyph: ""),
        new("net.renew", "Tường lửa & mạng", "Xin lại địa chỉ IP", "ipconfig /renew — mạng chập chờn sau khi đổi modem / switch",
            "ipconfig /renew | Select-String 'IPv4|Default Gateway|Cổng|Địa chỉ' | ForEach-Object { '• ' + $_.Line.Trim() }", Glyph: "",
            Warn: "Mạng sẽ mất kết nối vài giây trong lúc xin lại địa chỉ IP. Nếu đang điều khiển máy từ xa (UltraViewer, AnyDesk…) có thể bị ngắt."),
        new("net.winsock", "Tường lửa & mạng", "Đặt lại Winsock", "Sửa lỗi ngăn xếp mạng hỏng (phần mềm VPN / diệt virus cũ để lại) — cần khởi động lại máy",
            "netsh winsock reset | Select-Object -First 3\n'• Khởi động lại máy để hoàn tất'", Glyph: "",
            Warn: "Đặt lại Winsock xoá các thành phần mạng do phần mềm VPN / diệt virus cài thêm; những phần mềm đó có thể phải cài lại. Cần khởi động lại máy."),

        // ===== Spooler & driver =====
        new("sp.queue", "Spooler & driver", "Xoá hàng đợi in", "Xoá mọi file lệnh in kẹt trong spool\\PRINTERS (của mọi máy in)", "ClearQueue", Glyph: ""),
        new("sp.drivers", "Spooler & driver", "Dọn driver máy in không dùng", "Gỡ driver máy in (x64 và x86) không còn máy in nào dùng, xoá khỏi Driver Store",
            """
            $used = @(Get-Printer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DriverName -Unique)
            $list = @(Get-PrinterDriver -ErrorAction SilentlyContinue | Where-Object { $used -notcontains $_.Name -and $_.Name -notmatch '^Microsoft ' })
            if ($list.Count -eq 0) { '• Không có driver thừa' }
            foreach ($d in $list) {
              Remove-PrinterDriver -Name $d.Name -PrinterEnvironment $d.PrinterEnvironment -RemoveFromDriverStore -ErrorAction SilentlyContinue
              if ($?) { "• Đã gỡ $($d.Name) ($($d.PrinterEnvironment))" } else { "• Chưa gỡ được $($d.Name): đang bị dùng" }
            }
            """, Glyph: ""),
        new("sp.default", "Spooler & driver", "Sửa máy in mặc định (user)", "Xoá thiết lập máy in mặc định bị hỏng của người dùng — sửa 0x00000709",
            "RegSet 'HKCU:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Windows' 'LegacyDefaultPrinterMode' 1\nRegDel 'HKCU:\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Windows' 'Device'\n" + Spooler,
            Glyph: ""),
        new("sp.mscms", "Spooler & driver", "Chép mscms.dll (lỗi 0x7e)", "Chép mscms.dll vào spool\\drivers\\x64\\3 và W32X86\\3",
            """
            $map = @{ 'x64\3' = (Join-Path $env:windir 'System32\mscms.dll'); 'W32X86\3' = (Join-Path $env:windir 'SysWOW64\mscms.dll') }
            foreach ($k in $map.Keys) {
              $src = $map[$k]; $dst = Join-Path $spool "drivers\$k"
              if ((Test-Path $src) -and (Test-Path $dst)) { Copy-Item $src $dst -Force; "• Đã chép → spool\drivers\$k" } else { "• Bỏ qua spool\drivers\$k (không có)" }
            }
            """, Glyph: ""),
        new("sp.log", "Spooler & driver", "Xoá lệnh in lỗi + nhật ký in", "Xoá hàng đợi và nhật ký PrintService để theo dõi lỗi mới cho rõ",
            "ClearQueue\nwevtutil cl 'Microsoft-Windows-PrintService/Admin' 2>$null; '• Đã xoá nhật ký PrintService/Admin'", Glyph: ""),
        new("sp.notify", "Spooler & driver", "Khởi động lại PrintNotify + Spooler", "Làm mới thông báo và tiện ích driver máy in",
            "Svc 'PrintNotify' 'Manual'\n" + Spooler, Glyph: ""),

        // ===== RPC & Point and Print =====
        new("rpc.client", "RPC & Point and Print", "RPC qua Named Pipes (máy kết nối)", "RpcUseNamedPipeProtocol = 1, RpcProtocols = 7, ForceKerberosForRpc = 0 — chính sách Microsoft cho Windows 11 22H2+ khi kết nối máy chia sẻ đời cũ",
            ClientRpc + "\n" + Spooler, Glyph: ""),
        new("rpc.11b", "RPC & Point and Print", "RpcAuthnLevelPrivacyEnabled = 0", "Sửa 0x0000011b / 0x0000007c — chạy trên MÁY ĐANG CHIA SẺ", Rpc11b + "\n" + Spooler, WarnRpc11b, Glyph: ""),
        new("rpc.restore", "RPC & Point and Print", "Khôi phục RPC mặc định", "Xoá các thiết lập RPC ở trên, bật lại mức bảo mật mặc định",
            "RegDel $rpcPol 'RpcUseNamedPipeProtocol'\nRegDel $rpcPol 'RpcProtocols'\nRegDel $rpcPol 'ForceKerberosForRpc'\nRegDel $printKey 'RpcAuthnLevelPrivacyEnabled'\n" + Spooler, Glyph: ""),
        new("pnp.open", "RPC & Point and Print", "Tạm mở Point and Print", "Cho cài driver từ máy chia sẻ không cần admin (sửa 0x00000bcb, 0x00004005)",
            "RegSet $pnp 'RestrictDriverInstallationToAdministrators' 0\nRegSet $pnp 'NoWarningNoElevationOnInstall' 1\nRegSet $pnp 'UpdatePromptSettings' 2\n" + Spooler, WarnPnp, Glyph: ""),
        new("pnp.lock", "RPC & Point and Print", "Khoá lại Point and Print", "Về mặc định an toàn: chỉ admin cài được driver máy in",
            "RegDel $pnp 'NoWarningNoElevationOnInstall'\nRegDel $pnp 'UpdatePromptSettings'\nRegSet $pnp 'RestrictDriverInstallationToAdministrators' 1\n" + Spooler, Glyph: ""),
        new("smb.guest.on", "RPC & Point and Print", "Cho phép SMB Guest", "AllowInsecureGuestAuth = 1 — vào máy in / thư mục chia sẻ không mật khẩu (NAS, máy cũ)",
            "RegSet 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters' 'AllowInsecureGuestAuth' 1\nRegSet 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\LanmanWorkstation' 'AllowInsecureGuestAuth' 1\nSvc 'LanmanWorkstation'",
            WarnGuest, Glyph: ""),
        new("smb.guest.off", "RPC & Point and Print", "Tắt SMB Guest (an toàn)", "Về mặc định: không vào chia sẻ bằng Guest",
            "RegDel 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\LanmanWorkstation\\Parameters' 'AllowInsecureGuestAuth'\nRegDel 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\LanmanWorkstation' 'AllowInsecureGuestAuth'\nSvc 'LanmanWorkstation'",
            Glyph: ""),

        // ===== Chẩn đoán =====
        new("diag.settings", "Chẩn đoán", "Mở Settings → Máy in", "Bluetooth & devices → Printers & scanners", Open: "ms-settings:printers", Glyph: ""),
        new("diag.devices", "Chẩn đoán", "Mở Devices and Printers", "Cửa sổ máy in kiểu Control Panel", Open: "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}", Glyph: ""),
        new("diag.pm", "Chẩn đoán", "Mở Print Management", "printmanagement.msc — quản lý máy in, driver, cổng (Windows Pro trở lên)", Open: "@printmgmt", Glyph: "\uE7F8"),
        new("diag.printers", "Chẩn đoán", "Liệt kê máy in", "Tên, driver, cổng, trạng thái của mọi máy in", ShowOutput: true, Glyph: "",
            Script: "Get-Printer | Sort-Object Name | ForEach-Object { \"• $($_.Name)`n    driver: $($_.DriverName) · cổng: $($_.PortName) · $($_.PrinterStatus)\" }"),
        new("diag.ports", "Chẩn đoán", "Liệt kê cổng máy in", "Cổng TCP/IP, USB, WSD… và địa chỉ máy in", ShowOutput: true, Glyph: "",
            Script: "Get-PrinterPort | Sort-Object Name | ForEach-Object { \"• $($_.Name)  $($_.PrinterHostAddress) $($_.PortNumber) $($_.Description)\" }"),
        new("diag.drivers", "Chẩn đoán", "Liệt kê driver máy in", "Driver máy in đã cài và gói trong Driver Store", ShowOutput: true, Glyph: "",
            Script: "Get-PrinterDriver | Sort-Object Name | ForEach-Object { \"• $($_.Name) ($($_.PrinterEnvironment)) $($_.MajorVersion)\" }"),
        new("diag.trouble", "Chẩn đoán", "Mở trình khắc phục sự cố máy in", "Trình sửa lỗi tự động của Windows (Get Help)",
            Open: "ms-settings:troubleshoot", Glyph: ""),
        new("diag.events", "Chẩn đoán", "Xem lỗi in gần đây", "20 lỗi mới nhất trong nhật ký PrintService", ShowOutput: true, Glyph: "",
            Script: """
            $e = @(Get-WinEvent -LogName 'Microsoft-Windows-PrintService/Admin' -MaxEvents 20 -ErrorAction SilentlyContinue)
            if ($e.Count -eq 0) { '• Không có lỗi in nào được ghi lại' }
            $e | ForEach-Object { "• $($_.TimeCreated.ToString('dd/MM HH:mm'))  [$($_.Id)] $(($_.Message -split "`n")[0])" }
            """),

        // ===== Theo hãng =====
        Vendor("HP", "HP Smart / HP Status / HP Print Scan Doctor", "HP|Hewlett"),
        Vendor("Canon", "Canon IJ / CNB / Canon Status Monitor", "Canon|CNB|CNMN|CNBJ"),
        Vendor("Epson", "Epson Status Monitor / EpsonScan / Epson Customer Research", "Epson|EPSON|EPLTarget|EpsonCustomerParticipation"),
        Vendor("Brother", "Brother Status Monitor / BrYNSvc", "Brother|BrYNSvc|BrSvc"),
        Vendor("Samsung / Xerox", "Samsung Printer Diagnostics / Xerox Device Agent", "Samsung|Xerox|SPDS"),
        Vendor("Ricoh / Kyocera", "Ricoh Status Monitor / Kyocera KX", "Ricoh|RICOH|Kyocera|KMPrint"),

        // ===== Nâng cao =====
        new("adv.gp", "Nâng cao", "Áp dụng Group Policy ngay", "gpupdate /force — máy in triển khai qua GPO của công ty", "gpupdate /force | Select-Object -Last 3", Glyph: ""),
        new("adv.explorer", "Nâng cao", "Khởi động lại Explorer", "Làm mới thanh taskbar / cửa sổ sau khi sửa (không mất dữ liệu đang làm)",
            "Stop-Process -Name explorer -Force; Start-Sleep 2; if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }; '• Đã khởi động lại Explorer'",
            Glyph: ""),
    ];

    private static PrinterFix Restart(string svc, string name, string desc, string start = "Automatic") =>
        new($"svc.{svc.ToLowerInvariant()}", "Dịch vụ in", $"Khởi động lại {name}", $"{desc} · đặt {(start == "Manual" ? "Thủ công" : "Tự động")} rồi chạy lại",
            $"Svc '{svc}' '{start}'", Glyph: "");

    private static PrinterFix Feature(string feat, string name, string desc) =>
        new($"feat.{feat.ToLowerInvariant()}", "Tính năng Windows", $"Bật {name}", desc, $"Feat '{feat}'", Glyph: "");

    private static PrinterFix Vendor(string vendor, string desc, string pattern) =>
        new($"vendor.{vendor.Split(' ')[0].ToLowerInvariant()}", "Theo hãng", $"Khởi động lại dịch vụ {vendor} bị treo", desc,
            $$"""
            $svcs = @(Get-CimInstance Win32_Service | Where-Object { ($_.Name -match '{{pattern}}' -or $_.DisplayName -match '{{pattern}}') -and $_.PathName -notmatch '\\Windows\\System32\\svchost' })
            if ($svcs.Count -eq 0) { '• Máy này không có dịch vụ của {{vendor}}' }
            foreach ($s in $svcs) {
              if ($s.State -eq 'Running') { Restart-Service -Name $s.Name -Force -ErrorAction SilentlyContinue } elseif ($s.StartMode -ne 'Disabled') { Start-Service -Name $s.Name -ErrorAction SilentlyContinue }
              "• $($s.DisplayName): " + (Get-Service -Name $s.Name).Status
            }
            $p = @(Get-Process | Where-Object { $_.Name -match '{{pattern}}' -and $_.Path -and $_.Path -notmatch '\\Windows\\' })
            foreach ($x in $p) { Stop-Process -Id $x.Id -Force -ErrorAction SilentlyContinue; "• Đã tắt chương trình treo $($x.Name)" }
            Svc 'Spooler'
            """, Glyph: "");

    public static readonly IReadOnlyList<string> GroupOrder =
        [Codes, "Sửa nhanh tổng hợp", "Dịch vụ in", "Tính năng Windows", "Tường lửa & mạng", "Spooler & driver", "RPC & Point and Print", "Chẩn đoán", "Theo hãng", "Nâng cao"];

    public static readonly IReadOnlyDictionary<string, string> GroupNotes = new Dictionary<string, string>
    {
        [Codes] = "Chọn đúng mã lỗi Windows báo khi kết nối / thêm máy in.",
        ["RPC & Point and Print"] = "Mục có dấu ⚠ hạ mức bảo mật — chỉ dùng trong mạng nội bộ, xong việc bấm mục “Khôi phục / Khoá lại / Tắt” tương ứng.",
        ["Theo hãng"] = "Khởi động lại dịch vụ và tắt chương trình theo dõi trạng thái của hãng khi chúng treo làm kẹt lệnh in.",
    };

    public static List<PrinterFixGroup> Groups() =>
        GroupOrder.Select(g => new PrinterFixGroup(g, GroupNotes.GetValueOrDefault(g, ""), All.Where(f => f.Group == g))).ToList();

    /// <summary>Chạy một thao tác, trả kết quả gồm các dòng “• …” script in ra.</summary>
    public static async Task<OpResult> RunAsync(PrinterFix f, CancellationToken ct = default)
    {
        if (f.Open.Length > 0)
        {
            ShellLauncher.Open(f.Open.StartsWith("shell:") ? "explorer.exe" : f.Open, f.Open.StartsWith("shell:") ? f.Open : null);
            return OpResult.Success($"Đã mở: {f.Title}");
        }
        var r = await ProcessRunner.PowerShellAsync(Prelude + f.Script, ct: ct);
        var text = string.Join("\n", r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
        var err = string.Join("\n", r.Error.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("At line") && !l.StartsWith("+ ")).Take(3));
        if (text.Length == 0 && err.Length > 0) return OpResult.Fail($"{f.Title}: {err}");
        return OpResult.Success($"{f.Title}\n{text}".TrimEnd() + (err.Length > 0 ? $"\n(cảnh báo: {err})" : ""));
    }
}
