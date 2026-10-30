using System.Globalization;
using System.IO;
using ITHelpdeskTool.Core;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Report;

public static partial class ReportCollector
{
    // ================= Bảo mật =================
    private static SectionData Security()
    {
        var d = new SectionData();

        // Phần mềm diệt virus đăng ký với Windows Security Center (Windows Server không có mục này)
        List<WmiRow>? avs;
        try { avs = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"); }
        catch { avs = null; }
        foreach (var av in (avs ?? []).GroupBy(a => a.Str("displayName")).Select(g => g.First()))
        {
            var state = av.Long("productState");
            var on = ((state >> 12) & 0xF) == 1;
            var outdated = ((state >> 4) & 0xF) != 0;
            d.F($"Diệt virus: {av.Str("displayName")}",
                on ? outdated ? "Đang bật · chưa cập nhật mẫu nhận diện" : "Đang bật" : "Đang tắt",
                !on ? Tone.Bad : outdated ? Tone.Warn : Tone.Good);
        }
        // Microsoft Defender
        var mp = Wmi.First("SELECT RealTimeProtectionEnabled, AntivirusEnabled, AntivirusSignatureLastUpdated, AntivirusSignatureVersion, IsTamperProtected FROM MSFT_MpComputerStatus",
            @"root\Microsoft\Windows\Defender");
        if (mp is not null && mp.Bool("AntivirusEnabled") == true)
        {
            var rt = mp.Bool("RealTimeProtectionEnabled");
            d.F("Defender · bảo vệ thời gian thực", Fmt.YesNo(rt), rt == true ? Tone.Good : Tone.Bad);
            if (mp.Date("AntivirusSignatureLastUpdated") is { } sig)
            {
                var age = (DateTime.Now - sig).TotalDays;
                d.F("Defender · cập nhật mẫu virus", $"{Fmt.Stamp(sig)} ({mp.Str("AntivirusSignatureVersion")})",
                    age > 7 ? Tone.Warn : Tone.Normal);
            }
            d.F("Defender · chống can thiệp (Tamper)", Fmt.YesNo(mp.Bool("IsTamperProtected")));
        }
        var defenderOn = mp?.Bool("AntivirusEnabled") == true;
        if (avs is { Count: 0 } && !defenderOn)
            d.F("Diệt virus", "Không thấy phần mềm diệt virus nào", Tone.Bad);

        // Tường lửa: đọc registry (nhanh, không cần dịch vụ)
        var fw = new List<string>();
        var off = new List<string>();
        foreach (var (key, name) in new[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
            if (k?.GetValue("EnableFirewall") is int v) (v == 1 ? fw : off).Add(name);
        }
        if (fw.Count + off.Count > 0)
            d.F("Tường lửa Windows", off.Count == 0 ? "Bật cả 3 cấu hình" : $"Tắt ở: {string.Join(", ", off)}",
                off.Count == 0 ? Tone.Good : off.Contains("Public") ? Tone.Bad : Tone.Warn);

        // TPM
        var tpm = Wmi.First("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion, ManufacturerIdTxt FROM Win32_Tpm",
            @"root\cimv2\Security\MicrosoftTpm");
        if (tpm is null) d.F("TPM", "Không có hoặc đang tắt trong BIOS", Tone.Warn);
        else
        {
            var ver = tpm.Str("SpecVersion").Split(',').FirstOrDefault()?.Trim() ?? "";
            var enabled = tpm.Bool("IsEnabled_InitialValue") == true;
            d.F("TPM", $"{(enabled ? "Đang bật" : "Đang tắt")} · phiên bản {ver} {tpm.Str("ManufacturerIdTxt")}".Trim(),
                enabled && ver.StartsWith('2') ? Tone.Good : Tone.Warn);
        }

        var sb = SecureBoot();
        d.F("Secure Boot", Fmt.YesNo(sb), sb == true ? Tone.Good : sb == false ? Tone.Warn : Tone.Muted);

        // BitLocker ổ hệ thống
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var bl = Wmi.TryQuery($"SELECT ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{sysDrive}'",
            @"root\cimv2\Security\MicrosoftVolumeEncryption").FirstOrDefault();
        if (bl is not null)
        {
            var (blText, blTone) = Disk.BitLockerService.WmiState(bl);
            d.F($"BitLocker ổ {sysDrive}", blText, blTone);
        }

        using (var uac = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            var lua = uac?.GetValue("EnableLUA") as int?;
            d.F("UAC (hỏi quyền admin)", lua == 0 ? "Tắt" : "Bật", lua == 0 ? Tone.Warn : Tone.Normal);
        }

        using (var rdp = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server"))
            if (rdp?.GetValue("fDenyTSConnections") is int deny)
                d.F("Remote Desktop", deny == 0 ? "Đang cho phép kết nối" : "Tắt");

        return d;
    }

    // ================= Phần mềm =================
    private static SectionData InstalledApps()
    {
        var d = new SectionData();
        var apps = new Dictionary<string, (string Name, string Ver, string Pub, string Date, long SizeKb)>(StringComparer.OrdinalIgnoreCase);

        void Scan(RegistryKey root, string path)
        {
            using var k = root.OpenSubKey(path);
            if (k is null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using var a = k.OpenSubKey(sub);
                    if (a?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (a.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (a.GetValue("ParentKeyName") is not null) continue;             // bản vá con của app khác
                    if (a.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                    var ver = a.GetValue("DisplayVersion") as string ?? "";
                    var key = $"{name.Trim()}|{ver}";
                    if (apps.ContainsKey(key)) continue;
                    var date = a.GetValue("InstallDate") as string ?? "";
                    if (DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        date = Fmt.Date(dt);
                    else date = "";
                    var size = a.GetValue("EstimatedSize") is int kb ? kb : 0L;
                    apps[key] = (name.Trim(), ver, (a.GetValue("Publisher") as string ?? "").Trim(), date, size);
                }
                catch { /* key hỏng thì bỏ qua */ }
            }
        }

        const string U = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Scan(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), U);
        Scan(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), U);
        Scan(Registry.CurrentUser, U);

        d.F("Số phần mềm", apps.Count.ToString());
        var t = d.Table("Danh sách", ["Tên", "Phiên bản", "Nhà phát hành", "Ngày cài", "Dung lượng"],
            [2.4, 1, 1.6, 0.8, 0.7], preview: 15, filterable: true);
        foreach (var a in apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            t.Add(a.Name, a.Ver, a.Pub, a.Date, a.SizeKb > 0 ? Fmt.Size(a.SizeKb * 1024d) : "");
        return d;
    }

    // ================= Cập nhật =================
    private static SectionData Updates()
    {
        var d = new SectionData();
        var rows = Wmi.TryQuery("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering", timeoutSec: 30)
            .Select(r => (Id: r.Str("HotFixID"), Kind: r.Str("Description"), Date: ParseHotfixDate(r.Str("InstalledOn"))))
            .Where(r => r.Id.Length > 0)
            .OrderByDescending(r => r.Date ?? DateTime.MinValue)
            .ToList();

        d.F("Số bản cập nhật đã cài", rows.Count.ToString());
        var last = rows.FirstOrDefault(r => r.Date is not null).Date;
        if (last is { } l)
        {
            var days = (DateTime.Now - l).TotalDays;
            d.F("Cập nhật gần nhất", $"{Fmt.Date(l)} ({days:0} ngày trước)", days > 60 ? Tone.Bad : days > 35 ? Tone.Warn : Tone.Good);
        }

        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU"))
            if (wu?.GetValue("NoAutoUpdate") is int n && n == 1)
                d.F("Tự động cập nhật", "Bị tắt bằng Policy", Tone.Warn);

        var t = d.Table("Các bản gần nhất", ["Mã", "Loại", "Ngày cài"], [1, 2, 1], preview: 10);
        foreach (var r in rows) t.Add(r.Id, r.Kind, Fmt.Date(r.Date));
        return d;
    }

    private static DateTime? ParseHotfixDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyyMMdd"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
        // Một số máy lưu FILETIME dạng hex
        if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft))
            try { return DateTime.FromFileTime(ft); } catch { }
        return null;
    }

    // ================= Máy in =================
    private static SectionData Printers()
    {
        var d = new SectionData();
        var list = Wmi.TryQuery("SELECT Name, Default, PortName, DriverName, WorkOffline, Shared, Network FROM Win32_Printer");
        string[] virt = ["Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote", "Fax", "Send To OneNote", "AnyDesk Printer"];
        var real = list.Where(p => !virt.Any(v => p.Str("Name").StartsWith(v, StringComparison.OrdinalIgnoreCase))).ToList();

        if (real.Count == 0)
        {
            d.SkipReason = "Chưa cài máy in nào (chỉ có máy in ảo như Print to PDF).";
            return d;
        }

        var t = d.Table("Máy in đã cài", ["Tên", "Mặc định", "Cổng", "Driver", "Trạng thái"], [2, 0.7, 1.3, 1.8, 0.9]);
        foreach (var p in real.OrderByVi(p => p.Str("Name")))
        {
            var offline = p.Bool("WorkOffline") == true;
            t.Add(p.Str("Name"),
                p.Bool("Default") == true ? "✓" : "",
                p.Str("PortName"),
                p.Str("DriverName"),
                new ReportCell(offline ? "Ngoại tuyến" : p.Bool("Network") == true ? "Máy in mạng" : "Sẵn sàng",
                    offline ? Tone.Warn : Tone.Normal));
        }
        return d;
    }
}
