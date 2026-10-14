using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.ProcessRunner;

namespace ITHelpdeskTool.Services.SysConfig;

public static partial class SysConfigService
{
    // ================= Tên máy & nhóm =================
    public static async Task<OpResult> RenameComputerAsync(string newName)
    {
        newName = newName.Trim();
        if (ValidateComputerName(newName) is { } err) return OpResult.Fail(err);
        if (newName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("Tên mới trùng tên hiện tại.");
        var r = await PowerShellAsync($"Rename-Computer -NewName {PsQuote(newName)} -Force -WarningAction SilentlyContinue");
        return OpResult.From(r, $"Đã đổi tên máy thành {newName}. Khởi động lại để áp dụng.", "Không đổi được tên máy", restart: true);
    }

    public static async Task<OpResult> SetWorkgroupAsync(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length is 0 or > 15 || name.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>']) >= 0)
            return OpResult.Fail("Tên workgroup dài 1–15 ký tự, không chứa ký tự đặc biệt.");
        if (ReadIdentity().InDomain)
            return OpResult.Fail("Máy đang trong domain. Rời domain cần tài khoản quản trị domain — hãy làm trong System Properties.");
        var r = await PowerShellAsync($"Add-Computer -WorkgroupName {PsQuote(name)} -Force -WarningAction SilentlyContinue");
        return OpResult.From(r, $"Đã chuyển sang workgroup {name}. Khởi động lại để áp dụng.", "Không đổi được workgroup", restart: true);
    }

    // ================= Thời gian & khu vực =================
    public static async Task<OpResult> SetTimeZoneAsync(string id)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return OpResult.Fail("Múi giờ không hợp lệ.");
        var r = await ConsoleAsync("tzutil.exe", $"/s \"{id}\"");
        TimeZoneInfo.ClearCachedData();
        return OpResult.From(r, $"Đã đặt múi giờ: {tz.DisplayName}", "Không đặt được múi giờ");
    }

    /// <summary>Bật dịch vụ thời gian, trỏ về máy chủ NTP rồi đồng bộ ngay.</summary>
    public static async Task<OpResult> SyncTimeAsync(string server)
    {
        server = string.IsNullOrWhiteSpace(server) ? "time.windows.com" : server.Trim();
        if (server.IndexOfAny([' ', '"', '\'', ';', '&', '|']) >= 0) return OpResult.Fail("Tên máy chủ NTP không hợp lệ.");

        await ConsoleAsync("sc.exe", "config w32time start= auto");
        await ConsoleAsync("net.exe", "start w32time");
        var cfg = await ConsoleAsync("w32tm.exe", $"/config /manualpeerlist:\"{server},0x9 time.windows.com,0x9\" /syncfromflags:manual /reliable:no /update");
        if (!cfg.Success) return OpResult.Fail($"Không cấu hình được NTP: {OpResult.FirstLine(cfg.Text)}");
        var sync = await ConsoleAsync("w32tm.exe", "/resync /force");
        if (!sync.Success)
        {
            // Dịch vụ vừa khởi động có thể chưa sẵn sàng, thử lại một lần
            await Task.Delay(2000);
            sync = await ConsoleAsync("w32tm.exe", "/resync /force");
        }
        return OpResult.From(sync, $"Đã đồng bộ giờ với {server} · bây giờ là {DateTime.Now:HH:mm:ss dd/MM/yyyy}", "Đồng bộ giờ lỗi");
    }

    /// <summary>Định dạng ngày/số cho tài khoản đang dùng (đăng xuất lại để mọi app nhận).</summary>
    public static async Task<OpResult> SetCultureAsync(string name)
    {
        try { _ = System.Globalization.CultureInfo.GetCultureInfo(name); }
        catch { return OpResult.Fail("Định dạng không hợp lệ."); }
        var r = await PowerShellAsync($"Set-Culture {PsQuote(name)}");
        return OpResult.From(r, $"Đã đặt định dạng khu vực {name}. Một số ứng dụng cần mở lại mới nhận.", "Không đặt được định dạng");
    }

    // ================= Mạng =================
    private static string NetshName(string adapter) => "\"" + adapter.Replace("\"", "") + "\"";

    public static async Task<OpResult> SetDhcpAsync(string adapter, bool alsoDns)
    {
        var r = await ConsoleAsync("netsh.exe", $"interface ipv4 set address name={NetshName(adapter)} source=dhcp");
        // "đã bật DHCP" → netsh trả lỗi nhưng không sao
        if (!r.Success && !r.Text.Contains("DHCP", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail($"Không chuyển được sang DHCP: {OpResult.FirstLine(r.Text)}");
        if (alsoDns)
        {
            var d = await ConsoleAsync("netsh.exe", $"interface ipv4 set dnsservers name={NetshName(adapter)} source=dhcp");
            if (!d.Success && !d.Text.Contains("DHCP", StringComparison.OrdinalIgnoreCase))
                return OpResult.Fail($"Đã bật DHCP cho IP nhưng không đặt được DNS tự động: {OpResult.FirstLine(d.Text)}");
        }
        return OpResult.Success($"{adapter}: đã chuyển sang nhận IP tự động (DHCP).");
    }

    public static async Task<OpResult> SetStaticAsync(string adapter, string ip, string mask, string gateway, string dns1, string dns2)
    {
        if (!IsValidIpv4(ip)) return OpResult.Fail("Địa chỉ IP không hợp lệ.");
        if (!IsValidIpv4(mask)) return OpResult.Fail("Subnet mask không hợp lệ.");
        if (gateway.Trim().Length > 0 && !IsValidIpv4(gateway)) return OpResult.Fail("Gateway không hợp lệ.");
        var gw = gateway.Trim().Length > 0 ? $" gateway={gateway.Trim()} gwmetric=1" : " gateway=none";
        var r = await ConsoleAsync("netsh.exe", $"interface ipv4 set address name={NetshName(adapter)} static {ip.Trim()} {mask.Trim()}{gw}");
        if (!r.Success) return OpResult.Fail($"Không đặt được IP tĩnh: {OpResult.FirstLine(r.Text)}");
        var dns = await SetDnsAsync(adapter, dns1, dns2);
        return dns.Ok ? OpResult.Success($"{adapter}: đã đặt IP tĩnh {ip.Trim()}" + (dns1.Trim().Length > 0 ? $", DNS {dns1.Trim()}" : ""))
                      : dns;
    }

    /// <summary>Đặt DNS; bỏ trống cả hai = nhận DNS tự động.</summary>
    public static async Task<OpResult> SetDnsAsync(string adapter, string dns1, string dns2)
    {
        dns1 = dns1.Trim(); dns2 = dns2.Trim();
        if (dns1.Length == 0 && dns2.Length == 0)
        {
            var a = await ConsoleAsync("netsh.exe", $"interface ipv4 set dnsservers name={NetshName(adapter)} source=dhcp");
            return a.Success || a.Text.Contains("DHCP", StringComparison.OrdinalIgnoreCase)
                ? OpResult.Success($"{adapter}: DNS nhận tự động.")
                : OpResult.Fail($"Không đặt được DNS tự động: {OpResult.FirstLine(a.Text)}");
        }
        if (dns1.Length == 0) (dns1, dns2) = (dns2, "");
        if (!IsValidIpv4(dns1) || dns2.Length > 0 && !IsValidIpv4(dns2)) return OpResult.Fail("Địa chỉ DNS không hợp lệ.");

        var r = await ConsoleAsync("netsh.exe", $"interface ipv4 set dnsservers name={NetshName(adapter)} static {dns1} primary validate=no");
        if (!r.Success) return OpResult.Fail($"Không đặt được DNS: {OpResult.FirstLine(r.Text)}");
        if (dns2.Length > 0)
        {
            var r2 = await ConsoleAsync("netsh.exe", $"interface ipv4 add dnsservers name={NetshName(adapter)} {dns2} index=2 validate=no");
            if (!r2.Success) return OpResult.Fail($"Đặt được DNS chính nhưng DNS phụ lỗi: {OpResult.FirstLine(r2.Text)}");
        }
        await ConsoleAsync("ipconfig.exe", "/flushdns");
        return OpResult.Success($"{adapter}: DNS {dns1}{(dns2.Length > 0 ? ", " + dns2 : "")}");
    }

    public static async Task<OpResult> FlushDnsAsync()
    {
        var r = await ConsoleAsync("ipconfig.exe", "/flushdns");
        return OpResult.From(r, "Đã xoá bộ nhớ đệm DNS.", "Xoá cache DNS lỗi");
    }

    public static async Task<OpResult> RenewIpAsync()
    {
        await ConsoleAsync("ipconfig.exe", "/release");
        var r = await ConsoleAsync("ipconfig.exe", "/renew");
        return OpResult.From(r, "Đã xin lại địa chỉ IP từ DHCP.", "Xin lại IP lỗi");
    }

    public static async Task<OpResult> ResetNetworkAsync()
    {
        var a = await ConsoleAsync("netsh.exe", "winsock reset");
        var b = await ConsoleAsync("netsh.exe", "int ip reset");
        await ConsoleAsync("ipconfig.exe", "/flushdns");
        return a.Success
            ? OpResult.Success("Đã reset Winsock và TCP/IP. Khởi động lại để hoàn tất.", restart: true)
            : OpResult.Fail($"Reset mạng lỗi: {OpResult.FirstLine(a.Text + b.Text)}");
    }

    // ================= Tài khoản =================
    public static async Task<OpResult> CreateUserAsync(string name, string password, bool admin)
    {
        name = name.Trim();
        if (ValidateUserName(name) is { } err) return OpResult.Fail(err);
        var script = $$"""
            $n = {{PsQuote(name)}}
            if ($env:HD_PW) { $pw = ConvertTo-SecureString $env:HD_PW -AsPlainText -Force; New-LocalUser -Name $n -Password $pw -PasswordNeverExpires | Out-Null }
            else { New-LocalUser -Name $n -NoPassword | Out-Null }
            Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $n -ErrorAction SilentlyContinue
            if ({{(admin ? "$true" : "$false")}}) { Add-LocalGroupMember -SID '{{AdminsSid}}' -Member $n }
            """;
        var r = await PowerShellAsync(script, env: new Dictionary<string, string> { ["HD_PW"] = password });
        return OpResult.From(r, $"Đã tạo tài khoản {name}{(admin ? " (quản trị)" : "")}.", "Không tạo được tài khoản");
    }

    public static async Task<OpResult> SetPasswordAsync(string name, string password)
    {
        var script = $$"""
            if ($env:HD_PW) { Set-LocalUser -Name {{PsQuote(name)}} -Password (ConvertTo-SecureString $env:HD_PW -AsPlainText -Force) }
            else { ([ADSI]"WinNT://$env:COMPUTERNAME/{{name.Replace("'", "")}},user").SetPassword('') }
            """;
        var r = await PowerShellAsync(script, env: new Dictionary<string, string> { ["HD_PW"] = password });
        return OpResult.From(r, password.Length > 0 ? $"Đã đặt mật khẩu mới cho {name}." : $"Đã xoá mật khẩu của {name}.",
            "Không đặt được mật khẩu");
    }

    public static async Task<OpResult> SetUserEnabledAsync(UserInfo u, bool enabled, IReadOnlyList<UserInfo> all)
    {
        if (!enabled && u.IsCurrent) return OpResult.Fail("Không thể tắt tài khoản đang đăng nhập.");
        if (!enabled && u.IsAdmin && all.Count(x => x.IsAdmin && x.Enabled) <= 1)
            return OpResult.Fail("Đây là tài khoản quản trị duy nhất đang bật — tắt đi sẽ không còn ai quản trị máy.");
        var r = await PowerShellAsync($"{(enabled ? "Enable" : "Disable")}-LocalUser -Name {PsQuote(u.Name)}");
        return OpResult.From(r, $"Đã {(enabled ? "bật" : "tắt")} tài khoản {u.Name}.", "Thao tác lỗi");
    }

    public static async Task<OpResult> SetUserAdminAsync(UserInfo u, bool admin, IReadOnlyList<UserInfo> all)
    {
        if (!admin && u.IsCurrent) return OpResult.Fail("Không thể bỏ quyền quản trị của chính tài khoản đang dùng.");
        if (!admin && all.Count(x => x.IsAdmin && x.Enabled) <= 1)
            return OpResult.Fail("Đây là tài khoản quản trị duy nhất — phải còn ít nhất một tài khoản quản trị.");
        var r = await PowerShellAsync($"{(admin ? "Add" : "Remove")}-LocalGroupMember -SID '{AdminsSid}' -Member {PsQuote(u.Name)}");
        return OpResult.From(r, admin ? $"{u.Name} đã có quyền quản trị." : $"{u.Name} đã thành tài khoản thường.", "Thao tác lỗi");
    }

    public static async Task<OpResult> DeleteUserAsync(UserInfo u, IReadOnlyList<UserInfo> all)
    {
        if (u.IsCurrent) return OpResult.Fail("Không thể xoá tài khoản đang đăng nhập.");
        if (u.IsBuiltinAdmin || IsSystemAccount(u)) return OpResult.Fail("Không xoá tài khoản có sẵn của Windows, chỉ tắt được.");
        if (u.IsAdmin && all.Count(x => x.IsAdmin && x.Enabled) <= 1) return OpResult.Fail("Không thể xoá tài khoản quản trị cuối cùng.");
        var r = await PowerShellAsync($"Remove-LocalUser -Name {PsQuote(u.Name)}");
        return OpResult.From(r, $"Đã xoá tài khoản {u.Name} (thư mục C:\\Users\\{u.Name} vẫn được giữ lại).", "Không xoá được");
    }

    // ================= Truy cập & chia sẻ =================
    public static async Task<OpResult> SetRemoteDesktopAsync(bool on)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: true)
                ?? throw new InvalidOperationException("Không thấy cấu hình Terminal Server.");
            k.SetValue("fDenyTSConnections", on ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { return OpResult.Fail($"Không đổi được Remote Desktop: {ex.Message}"); }

        var r = await PowerShellAsync($"{(on ? "Enable" : "Disable")}-NetFirewallRule -Group '{FwRemoteDesktop}'");
        var edition = Wmi.First("SELECT Caption FROM Win32_OperatingSystem")?.Str("Caption") ?? "";
        var homeNote = on && edition.Contains("Home", StringComparison.OrdinalIgnoreCase)
            ? " Lưu ý: bản Windows Home không nhận kết nối Remote Desktop." : "";
        return r.Success
            ? OpResult.Success((on ? "Đã bật Remote Desktop và mở cổng 3389 trên tường lửa." : "Đã tắt Remote Desktop.") + homeNote)
            : OpResult.Success($"Đã {(on ? "bật" : "tắt")} Remote Desktop, nhưng không chỉnh được tường lửa: {OpResult.FirstLine(r.Text)}");
    }

    public static Task<OpResult> SetNlaAsync(bool on)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", writable: true)
                ?? throw new InvalidOperationException("Không thấy cấu hình RDP-Tcp.");
            k.SetValue("UserAuthentication", on ? 1 : 0, RegistryValueKind.DWord);
            return Task.FromResult(OpResult.Success(on ? "Đã bật xác thực NLA cho Remote Desktop (an toàn hơn)."
                                                       : "Đã tắt NLA — máy cũ kết nối được nhưng kém an toàn hơn."));
        }
        catch (Exception ex) { return Task.FromResult(OpResult.Fail($"Không đổi được NLA: {ex.Message}")); }
    }

    /// <summary>Bật/tắt nhóm luật tường lửa cho mạng Private/Domain (không mở trên mạng Public).</summary>
    private static async Task<OpResult> SetFirewallGroupAsync(string group, bool on, string label, string? extra = null)
    {
        var script = on
            ? $"Get-NetFirewallRule -Group '{group}' | Where-Object {{ $_.Profile -ne 'Public' }} | Enable-NetFirewallRule; {extra}"
            : $"Get-NetFirewallRule -Group '{group}' | Disable-NetFirewallRule";
        var r = await PowerShellAsync(script);
        return OpResult.From(r, on ? $"Đã bật {label} (chỉ cho mạng Private/Domain)." : $"Đã tắt {label}.", $"Không đổi được {label}");
    }

    public static Task<OpResult> SetFileSharingAsync(bool on) =>
        SetFirewallGroupAsync(FwFileSharing, on, "chia sẻ file & máy in (kèm cho phép ping)");

    public static Task<OpResult> SetNetworkDiscoveryAsync(bool on) =>
        SetFirewallGroupAsync(FwNetDiscovery, on, "Network Discovery",
            "foreach ($s in 'FDResPub','fdPHost','SSDPSRV','upnphost') { Set-Service $s -StartupType Automatic -ErrorAction SilentlyContinue; Start-Service $s -ErrorAction SilentlyContinue }");

    // ================= Hệ thống =================
    public static async Task<OpResult> SetHibernateAsync(bool on)
    {
        var r = await ConsoleAsync("powercfg.exe", $"/hibernate {(on ? "on" : "off")}");
        return OpResult.From(r, on ? "Đã bật Hibernate (ngủ đông)." : "Đã tắt Hibernate — giải phóng file hiberfil.sys.", "Không đổi được Hibernate");
    }

    public static Task<OpResult> SetFastStartupAsync(bool on)
    {
        try
        {
            if (on && ReadSystem().HibernateOn == false)
                return Task.FromResult(OpResult.Fail("Fast Startup cần bật Hibernate trước."));
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", writable: true)
                ?? throw new InvalidOperationException("Không thấy khoá Power.");
            k.SetValue("HiberbootEnabled", on ? 1 : 0, RegistryValueKind.DWord);
            return Task.FromResult(OpResult.Success(on ? "Đã bật Fast Startup." : "Đã tắt Fast Startup — tắt máy là tắt hẳn (hay dùng khi sửa lỗi driver, dual boot)."));
        }
        catch (Exception ex) { return Task.FromResult(OpResult.Fail($"Không đổi được Fast Startup: {ex.Message}")); }
    }

    public static async Task<OpResult> CreateRestorePointAsync(string description)
    {
        if (!ReadSystem().RestoreSupported) return OpResult.Fail("Windows Server không hỗ trợ System Restore.");
        description = string.IsNullOrWhiteSpace(description) ? "IT Helpdesk Tool" : description.Trim();
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        // Windows chỉ cho tạo 1 điểm / 24h → tạm đặt tần suất = 0 rồi trả lại như cũ
        var script = $$"""
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old = (Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Enable-ComputerRestore -Drive {{PsQuote(sysDrive)}}
            Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            try { Checkpoint-Computer -Description {{PsQuote(description)}} -RestorePointType MODIFY_SETTINGS }
            finally { if ($null -eq $old) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $old } }
            """;
        var r = await PowerShellAsync(script);
        return OpResult.From(r, $"Đã tạo điểm khôi phục \"{description}\".", "Không tạo được điểm khôi phục");
    }

    public const string DefaultHosts = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        #
        # This is a sample HOSTS file used by Microsoft TCP/IP for Windows.
        #
        # This file contains the mappings of IP addresses to host names. Each
        # entry should be kept on an individual line. The IP address should
        # be placed in the first column followed by the corresponding host name.
        # The IP address and the host name should be separated by at least one
        # space.
        #
        # localhost name resolution is handled within DNS itself.
        #	127.0.0.1       localhost
        #	::1             localhost

        """;

    /// <summary>Trả file hosts về mặc định, có sao lưu bản cũ cạnh đó.</summary>
    public static Task<OpResult> ResetHostsAsync() => Task.Run(() =>
    {
        try
        {
            var path = HostsPath;
            string? backup = null;
            if (File.Exists(path))
            {
                backup = path + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(path, backup);
                File.SetAttributes(path, FileAttributes.Normal);   // bỏ thuộc tính chỉ đọc nếu có
            }
            File.WriteAllText(path, DefaultHosts.Replace("\n", "\r\n"), new UTF8Encoding(false));
            _ = ConsoleAsync("ipconfig.exe", "/flushdns");
            return OpResult.Success($"Đã khôi phục file hosts mặc định{(backup is null ? "" : $" (bản cũ: {Path.GetFileName(backup)})")}.");
        }
        catch (Exception ex) { return OpResult.Fail($"Không ghi được file hosts (có thể do phần mềm diệt virus chặn): {ex.Message}"); }
    });

    public static int CountHostsEntries()
    {
        try
        {
            return File.ReadLines(HostsPath).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        }
        catch { return -1; }
    }

    public static void OpenHostsInNotepad() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{HostsPath}\"") { UseShellExecute = true });

    public static OpResult RestartComputer(int seconds = 10)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", $"/r /t {seconds} /c \"IT Helpdesk Tool: khởi động lại để áp dụng thay đổi\"")
                { UseShellExecute = false, CreateNoWindow = true });
            return OpResult.Success($"Máy sẽ khởi động lại sau {seconds} giây (huỷ bằng lệnh shutdown /a).");
        }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }
}
