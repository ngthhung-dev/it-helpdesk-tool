using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.SysConfig;

/// <summary>
/// Đọc và thay đổi cấu hình Windows cho trang "Cấu hình hệ thống".
/// Hàm đọc chạy đồng bộ (gọi trong Task.Run), hàm thay đổi trả <see cref="OpResult"/>.
/// </summary>
public static partial class SysConfigService
{
    // Nhóm luật tường lửa (ID không đổi theo ngôn ngữ Windows)
    internal const string FwRemoteDesktop = "@FirewallAPI.dll,-28752";
    internal const string FwFileSharing = "@FirewallAPI.dll,-28502";
    internal const string FwNetDiscovery = "@FirewallAPI.dll,-32752";
    internal const string AdminsSid = "S-1-5-32-544";

    // ================= Tên máy =================
    public static IdentityInfo ReadIdentity()
    {
        var cs = Wmi.First("SELECT Domain, PartOfDomain, Workgroup FROM Win32_ComputerSystem");
        using var pending = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
        var active = Environment.MachineName;
        var next = pending?.GetValue("ComputerName") as string ?? active;
        var inDomain = cs?.Bool("PartOfDomain") == true;
        return new IdentityInfo(active,
            next.Equals(active, StringComparison.OrdinalIgnoreCase) ? "" : next,
            inDomain, inDomain ? cs!.Str("Domain") : "",
            inDomain ? "" : (cs?.Str("Workgroup") is { Length: > 0 } wg ? wg : cs?.Str("Domain") ?? "WORKGROUP"));
    }

    /// <summary>Tên máy NetBIOS hợp lệ: 1–15 ký tự, chữ/số/gạch ngang, không toàn số.</summary>
    public static string? ValidateComputerName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 15) return "Tên máy dài 1–15 ký tự.";
        if (!name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            return "Chỉ dùng chữ không dấu, số và dấu gạch ngang (-).";
        if (name.All(char.IsDigit)) return "Tên máy không được toàn là số.";
        if (name.StartsWith('-') || name.EndsWith('-')) return "Không bắt đầu/kết thúc bằng dấu gạch ngang.";
        return null;
    }

    // ================= Thời gian & khu vực =================
    public static TimeInfo ReadTime()
    {
        using var p = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters");
        var ntp = (p?.GetValue("NtpServer") as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(',')[0]).FirstOrDefault() ?? "";
        var running = Wmi.First("SELECT State FROM Win32_Service WHERE Name = 'W32Time'")?.Str("State") == "Running";
        using var intl = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
        var culture = intl?.GetValue("LocaleName") as string ?? CultureInfo.CurrentCulture.Name;
        return new TimeInfo(TimeZoneInfo.Local.Id, ntp, running, culture);
    }

    /// <summary>Các định dạng khu vực hay dùng.</summary>
    public static IReadOnlyList<(string Name, string Display)> CommonCultures { get; } =
    [
        ("vi-VN", "Tiếng Việt (Việt Nam) — 31/12/2026, 1.234,5"),
        ("en-US", "English (United States) — 12/31/2026, 1,234.5"),
        ("en-GB", "English (United Kingdom) — 31/12/2026, 1,234.5"),
        ("ja-JP", "日本語 (日本) — 2026/12/31"),
        ("ko-KR", "한국어 (대한민국) — 2026. 12. 31."),
        ("zh-CN", "中文 (中国) — 2026/12/31"),
    ];

    // ================= Mạng =================
    public static List<AdapterInfo> ReadAdapters()
    {
        var adapters = Wmi.TryQuery("SELECT Index, NetConnectionID, Name, NetConnectionStatus, GUID, MACAddress, PhysicalAdapter FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
        var configs = Wmi.TryQuery("SELECT Index, DHCPEnabled, IPAddress, IPSubnet, DefaultIPGateway, DNSServerSearchOrder, SettingID FROM Win32_NetworkAdapterConfiguration")
            .ToDictionary(c => c.Long("Index"));

        var list = new List<AdapterInfo>();
        foreach (var a in adapters)
        {
            if (!configs.TryGetValue(a.Long("Index"), out var c)) continue;
            var name = a.Str("NetConnectionID");
            var desc = a.Str("Name");
            // Bỏ card ảo của VPN/Hyper-V switch nội bộ nếu không phải card vật lý và đang ngắt
            var ips = c.Arr("IPAddress");
            var masks = c.Arr("IPSubnet");
            var i4 = Array.FindIndex(ips, ip => ip.Contains('.'));
            var settingId = c.Str("SettingID");
            list.Add(new AdapterInfo(
                name, desc, settingId,
                a.Long("NetConnectionStatus") == 2,
                c.Bool("DHCPEnabled") == true,
                i4 >= 0 ? ips[i4] : "",
                i4 >= 0 && i4 < masks.Length ? masks[i4] : "",
                c.Arr("DefaultIPGateway").FirstOrDefault(g => g.Contains('.')) ?? "",
                c.Arr("DNSServerSearchOrder").Where(d => d.Contains('.')).ToArray(),
                IsDnsAuto(settingId),
                a.Str("MACAddress")));
        }
        // Card đang kết nối lên đầu
        return list.OrderByDescending(x => x.Connected).ThenBy(x => x.Name).ToList();
    }

    private static bool IsDnsAuto(string settingId)
    {
        if (settingId.Length == 0) return true;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{settingId}");
        return string.IsNullOrWhiteSpace(k?.GetValue("NameServer") as string);
    }

    public static bool IsValidIpv4(string s) =>
        System.Net.IPAddress.TryParse(s.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && s.Trim().Count(c => c == '.') == 3;

    // ================= Tài khoản =================
    public static async Task<List<UserInfo>> ReadUsersAsync()
    {
        const string script = """
            $admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue | ForEach-Object { $_.SID.Value })
            Get-LocalUser | ForEach-Object {
              [pscustomobject]@{
                Name = $_.Name; FullName = $_.FullName; Enabled = $_.Enabled; Sid = $_.SID.Value
                Admin = $admins -contains $_.SID.Value
                LastLogon = if ($_.LastLogon) { $_.LastLogon.ToString('dd/MM/yyyy HH:mm') } else { '' }
                NeverExpires = ($null -eq $_.PasswordExpires)
              }
            } | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));

        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        var list = new List<UserInfo>();
        var json = r.Output.Trim();
        if (json.Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            bool B(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
            var sid = S("Sid");
            list.Add(new UserInfo(S("Name"), S("FullName"), B("Enabled"), B("Admin"),
                sid.EndsWith("-500"), sid.Equals(me, StringComparison.OrdinalIgnoreCase), S("LastLogon"), sid, B("NeverExpires")));
        }
        // Tài khoản hệ thống ẩn (Guest, DefaultAccount, WDAGUtilityAccount) xuống cuối
        return list.OrderBy(u => IsSystemAccount(u) ? 1 : 0).ThenBy(u => u.Name, StringComparer.Create(new System.Globalization.CultureInfo("vi-VN"), true)).ToList();
    }

    public static bool IsSystemAccount(UserInfo u) => u.IsSystem;

    public static string? ValidateUserName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 20) return "Tên đăng nhập dài 1–20 ký tự.";
        if (name.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@']) >= 0)
            return "Tên không được chứa các ký tự \" / \\ [ ] : ; | = , + * ? < > @";
        if (name.All(c => c is '.' or ' ')) return "Tên không hợp lệ.";
        return null;
    }

    // ================= Truy cập & chia sẻ =================
    public static async Task<SharingInfo> ReadSharingAsync()
    {
        using var ts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        using var rdpTcp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
        var rdp = ts?.GetValue("fDenyTSConnections") is int deny && deny == 0;
        var nla = rdpTcp?.GetValue("UserAuthentication") is int ua && ua == 1;

        // Bật = có ít nhất một luật (không phải chỉ Public) đang bật
        var script = $$"""
            function On($g) { [bool](Get-NetFirewallRule -Group $g -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' -and $_.Profile -ne 'Public' } | Select-Object -First 1) }
            "{0}|{1}" -f (On '{{FwFileSharing}}'), (On '{{FwNetDiscovery}}')
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        var parts = r.Output.Trim().Split('|');
        bool P(int i) => parts.Length > i && parts[i].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        return new SharingInfo(rdp, nla, P(0), P(1));
    }

    // ================= Hệ thống =================
    public static SystemInfo ReadSystem()
    {
        using var power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        var hib = power?.GetValue("HibernateEnabled") is int h && h == 1;
        var fast = sm?.GetValue("HiberbootEnabled") is int f && f == 1;

        // Windows Server không có System Restore
        var isServer = Wmi.First("SELECT ProductType FROM Win32_OperatingSystem")?.Long("ProductType") is 2 or 3;
        var sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        var protectedOn = false;
        using (var spp = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients"))
        {
            if (spp?.GetValue("{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}") is string[] vols)
                protectedOn = vols.Any(v => v.Contains($"({sysDrive.Replace(":", "%3A")})", StringComparison.OrdinalIgnoreCase));
        }
        var last = "";
        if (!isServer)
        {
            var rp = Wmi.TryQuery("SELECT Description, CreationTime FROM SystemRestore", @"root\default")
                .OrderByDescending(x => x.Str("CreationTime")).FirstOrDefault();
            if (rp is not null) last = $"{Fmt.Stamp(rp.Date("CreationTime"))} · {rp.Str("Description")}";
        }
        var pf = Wmi.First("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")?.Bool("AutomaticManagedPagefile") == true;
        return new SystemInfo(hib, fast, protectedOn, last, pf, !isServer);
    }

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
}
