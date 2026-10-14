using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ITHelpdeskTool.Core;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Report;

public static partial class ReportCollector
{
    // ================= Đồ hoạ & màn hình =================
    private static SectionData Graphics()
    {
        var d = new SectionData();
        var gpus = Wmi.TryQuery("SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController");
        var vram = ReadVramFromRegistry();

        var g = d.Table("Card đồ hoạ", ["Tên", "VRAM", "Driver", "Ngày driver", "Đang xuất hình"], [2.2, 0.8, 1.2, 1, 1.3]);
        foreach (var x in gpus)
        {
            var name = x.Str("Name");
            if (name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) && gpus.Count > 1) continue;

            // AdapterRAM của WMI bị giới hạn 4 GB → ưu tiên số trong registry
            var mem = vram.TryGetValue(name, out var v) ? v : x.Long("AdapterRAM");
            var w = x.Long("CurrentHorizontalResolution");
            var h = x.Long("CurrentVerticalResolution");
            var hz = x.Long("CurrentRefreshRate");
            g.Add(name,
                Fmt.Size(mem),
                x.Str("DriverVersion"),
                Fmt.Date(x.Date("DriverDate")),
                w > 0 ? $"{w}×{h}" + (hz > 1 ? $" @ {hz} Hz" : "") : "—");
        }

        // Màn hình: tên/serial thật đọc từ EDID qua root\wmi
        var ids = Wmi.TryQuery("SELECT InstanceName, ManufacturerName, UserFriendlyName, SerialNumberID, YearOfManufacture FROM WmiMonitorID", @"root\wmi");
        var sizes = Wmi.TryQuery("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", @"root\wmi")
            .ToDictionary(r => r.Str("InstanceName"), StringComparer.OrdinalIgnoreCase);
        var conns = Wmi.TryQuery("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", @"root\wmi")
            .ToDictionary(r => r.Str("InstanceName"), StringComparer.OrdinalIgnoreCase);

        var m = d.Table("Màn hình", ["Tên", "Hãng", "Kích thước", "Kết nối", "Năm SX", "Serial"], [1.6, 0.8, 0.9, 1.1, 0.6, 1.2]);
        foreach (var x in ids)
        {
            var inst = x.Str("InstanceName");
            var size = "";
            if (sizes.TryGetValue(inst, out var sz) && sz.Long("MaxHorizontalImageSize") > 0)
            {
                var diag = Math.Sqrt(Math.Pow(sz.Long("MaxHorizontalImageSize"), 2) + Math.Pow(sz.Long("MaxVerticalImageSize"), 2)) / 2.54;
                size = $"{diag:0.#}\"";
            }
            var conn = conns.TryGetValue(inst, out var cn) ? Connection(cn.Long("VideoOutputTechnology")) : "";
            var friendly = x.Str("UserFriendlyName");
            m.Add(friendly.Length > 0 ? friendly : "Màn hình chung",
                PnpVendor(x.Str("ManufacturerName")),
                size, conn,
                x.Long("YearOfManufacture") is > 1990 and var y ? y.ToString() : "",
                Clean(x.Str("SerialNumberID")));
        }
        if (m.Rows.Count == 0) d.Tables.Remove(m);
        return d;
    }

    private static Dictionary<string, long> ReadVramFromRegistry()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return map;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                long size = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        long l => l,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                if (size > 0) map[desc] = size;
            }
        }
        catch (Exception ex) { Log.Warn($"Đọc VRAM lỗi: {ex.Message}"); }
        return map;
    }

    private static string Connection(long t) => t switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (liền)",
        10 => "DisplayPort",
        11 => "DisplayPort (liền)",
        15 => "Miracast",
        16 => "USB-C / Thunderbolt",
        0x80000000 or -2147483648 => "Màn hình liền",
        _ => "",
    };

    private static string PnpVendor(string code) => code.ToUpperInvariant() switch
    {
        "AUO" => "AU Optronics",
        "BOE" => "BOE",
        "LGD" => "LG Display",
        "SDC" => "Samsung Display",
        "SAM" or "SEC" => "Samsung",
        "GSM" => "LG",
        "DEL" => "Dell",
        "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo",
        "ACR" => "Acer",
        "AUS" => "ASUS",
        "VSC" => "ViewSonic",
        "PHL" => "Philips",
        "AOC" => "AOC",
        "BNQ" => "BenQ",
        "CMN" => "Innolux",
        "SHP" => "Sharp",
        "MSI" => "MSI",
        "GBT" => "Gigabyte",
        "IVM" => "Iiyama",
        _ => code,
    };

    // ================= Ổ cứng =================
    private static async Task<SectionData> StorageAsync()
    {
        var d = new SectionData();
        const string storage = @"root\Microsoft\Windows\Storage";

        var phys = await Task.Run(() => Wmi.TryQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, SerialNumber, FirmwareVersion FROM MSFT_PhysicalDisk", storage));

        // Nhiệt độ / hao mòn / giờ chạy: thử đọc thẳng WMI (nhanh), không được mới gọi PowerShell (chậm vài giây)
        var rel = await Task.Run(() => ReadReliabilityWmi(storage));
        if (rel.Count == 0 && phys.Count > 0) rel = await ReadReliabilityAsync();

        var p = d.Table("Ổ vật lý", ["Tên", "Loại", "Dung lượng", "Sức khoẻ", "Nhiệt độ", "Hao mòn", "Giờ chạy", "Serial"],
            [2, 1, 0.8, 0.8, 0.7, 0.7, 0.8, 1.3]);
        foreach (var x in phys.OrderBy(x => x.Str("DeviceId")))
        {
            var health = (int)x.Long("HealthStatus");
            var isSsd = x.Long("MediaType") == 4;
            rel.TryGetValue(x.Str("DeviceId"), out var r);
            p.Add(
                x.Str("FriendlyName"),
                $"{Media((int)x.Long("MediaType"))} {Bus((int)x.Long("BusType"))}".Trim(),
                Fmt.Size(x.Long("Size")),
                new ReportCell(health switch { 0 => "Tốt", 1 => "Cảnh báo", 2 => "Hỏng", _ => "Không rõ" },
                    health switch { 0 => Tone.Good, 1 => Tone.Warn, 2 => Tone.Bad, _ => Tone.Muted }),
                r.Temp > 0 ? new ReportCell($"{r.Temp} °C", r.Temp >= 70 ? Tone.Bad : r.Temp >= 60 ? Tone.Warn : Tone.Normal) : "",
                isSsd && r.Wear is { } w ? new ReportCell($"{w}%", w >= 90 ? Tone.Bad : w >= 70 ? Tone.Warn : Tone.Normal) : "",
                r.Hours > 0 ? Fmt.Num(r.Hours) : "",
                Clean(x.Str("SerialNumber")));
        }

        // Phân vùng: chỉ ổ cứng trong + USB (DriveType 2/3) nên không đụng ổ mạng
        var bitlocker = await Task.Run(() => Wmi.TryQuery("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume",
                @"root\cimv2\Security\MicrosoftVolumeEncryption")
            .Where(b => b.Str("DriveLetter").Length > 0)
            .ToDictionary(b => b.Str("DriveLetter"), b => (int)b.Long("ProtectionStatus"), StringComparer.OrdinalIgnoreCase));
        var vols = await Task.Run(() => Wmi.TryQuery("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3"));

        var v = d.Table("Phân vùng", ["Ổ", "Nhãn", "Định dạng", "Dung lượng", "Còn trống", "BitLocker"], [0.5, 1.5, 0.8, 0.9, 1.2, 0.9]);
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        foreach (var x in vols.OrderBy(x => x.Str("DeviceID")))
        {
            var size = x.Long("Size");
            var free = x.Long("FreeSpace");
            if (size <= 0) continue;
            var pct = free * 100.0 / size;
            var id = x.Str("DeviceID");
            var isSys = id.Equals(sysDrive, StringComparison.OrdinalIgnoreCase);
            var bl = bitlocker.TryGetValue(id, out var ps) ? ps switch { 1 => "Đang bật", 0 => "Tắt", _ => "Không rõ" } : "—";
            v.Add(
                isSys ? $"{id} (hệ thống)" : id,
                x.Str("VolumeName") is { Length: > 0 } lbl ? lbl : (x.Long("DriveType") == 2 ? "USB" : "Local Disk"),
                x.Str("FileSystem"),
                Fmt.Size(size),
                new ReportCell($"{Fmt.Size(free)} ({pct:0}%)", pct < 8 ? Tone.Bad : pct < 15 ? Tone.Warn : Tone.Normal),
                bl);
        }
        return d;
    }

    private readonly record struct Reliability(int Temp, int? Wear, long Hours);

    private static Dictionary<string, Reliability> ReadReliabilityWmi(string scope)
    {
        var map = new Dictionary<string, Reliability>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var r in Wmi.TryQuery("SELECT DeviceId, Temperature, Wear, PowerOnHours FROM MSFT_StorageReliabilityCounter", scope))
        {
            var id = r.Str("DeviceId");
            if (id.Length == 0) continue;
            map[id] = new Reliability((int)r.Long("Temperature"), r["Wear"] is null ? null : (int)r.Long("Wear"), r.Long("PowerOnHours"));
        }
        Log.Info($"Reliability qua WMI: {map.Count} ổ, {sw.ElapsedMilliseconds} ms");
        return map;
    }

    private static async Task<Dictionary<string, Reliability>> ReadReliabilityAsync()
    {
        var map = new Dictionary<string, Reliability>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var res = await ProcessRunner.PowerShellAsync(
                "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
                "[pscustomobject]@{ Id = [string]$_.DeviceId; T = $r.Temperature; W = $r.Wear; H = $r.PowerOnHours } } | ConvertTo-Json -Compress",
                ct: cts.Token);
            var json = res.Output.Trim();
            if (json.Length == 0) return map;
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            foreach (var e in items)
            {
                var id = e.GetProperty("Id").GetString() ?? "";
                int I(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                long L(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                // Wear = 0 trên nhiều ổ chỉ có nghĩa "không báo" → coi là không có số liệu, trừ khi là SSD có báo nhiệt độ
                int? wear = e.TryGetProperty("W", out var wp) && wp.ValueKind == JsonValueKind.Number ? wp.GetInt32() : null;
                map[id] = new Reliability(I("T"), wear, L("H"));
            }
        }
        catch (Exception ex) { Log.Warn($"Đọc reliability ổ cứng lỗi: {ex.Message}"); }
        return map;
    }

    private static string Media(int m) => m switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };

    private static string Bus(int b) => b switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        3 => "ATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed",
        16 => "Storage Spaces",
        _ => "",
    };

    // ================= Mạng =================
    private static async Task<SectionData> NetworkAsync()
    {
        var d = new SectionData();
        var wifiTask = ReadWifiAsync();

        var (configs, adapters) = await Task.Run(() => (
            Wmi.TryQuery("SELECT Index, Description, MACAddress, IPAddress, IPSubnet, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled, DHCPServer FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"),
            Wmi.TryQuery("SELECT Index, NetConnectionID, Speed, PhysicalAdapter FROM Win32_NetworkAdapter")
                .ToDictionary(a => a.Long("Index"))));

        var (ssid, signal) = await wifiTask;
        if (ssid.Length > 0) d.F("Wi-Fi đang kết nối", signal.Length > 0 ? $"{ssid} · tín hiệu {signal}" : ssid);

        var t = d.Table("Card mạng đang hoạt động",
            ["Tên kết nối", "Thiết bị", "MAC", "IPv4", "Gateway", "DNS", "Cấp IP", "Tốc độ"],
            [1, 1.8, 1.2, 1.1, 1, 1.4, 0.8, 0.8]);
        foreach (var c in configs)
        {
            adapters.TryGetValue(c.Long("Index"), out var a);
            var ipv4 = c.Arr("IPAddress").Where(ip => ip.Contains('.')).ToArray();
            var speed = a?.Long("Speed") ?? 0;
            t.Add(
                a?.Str("NetConnectionID") ?? "",
                c.Str("Description"),
                c.Str("MACAddress"),
                string.Join(", ", ipv4),
                string.Join(", ", c.Arr("DefaultIPGateway").Where(ip => ip.Contains('.'))),
                string.Join(", ", c.Arr("DNSServerSearchOrder").Take(3)),
                c.Bool("DHCPEnabled") == true ? "DHCP" : "Tĩnh",
                speed > 0 ? speed >= 1_000_000_000 ? $"{speed / 1e9:0.#} Gbps" : $"{speed / 1e6:0} Mbps" : "");
        }

        var gw = configs.SelectMany(c => c.Arr("DefaultIPGateway")).FirstOrDefault(ip => ip.Contains('.'));
        d.F("Có Internet / gateway", gw is null ? "Không có gateway — có thể đang mất mạng" : $"Gateway {gw}",
            gw is null ? Tone.Warn : Tone.Normal);
        return d;
    }

    private static async Task<(string Ssid, string Signal)> ReadWifiAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var r = await ProcessRunner.RunAsync("netsh.exe", "wlan show interfaces", ct: cts.Token);
            string ssid = "", signal = "";
            foreach (var raw in r.Output.Split('\n'))
            {
                var parts = raw.Split(':', 2);
                if (parts.Length < 2) continue;
                var key = parts[0].Trim();
                var val = parts[1].Trim();
                if (key == "SSID") ssid = val;
                else if (Regex.IsMatch(val, @"^\d{1,3}\s?%$")) signal = val.Replace(" ", "");
            }
            return (ssid, signal);
        }
        catch { return ("", ""); }
    }

    // ================= Pin =================
    private static SectionData Battery()
    {
        var d = new SectionData();
        var st = Wmi.TryQuery("SELECT DesignedCapacity, ManufactureName, DeviceName, SerialNumber, Chemistry FROM BatteryStaticData", @"root\wmi");
        if (st.Count == 0 && Wmi.TryQuery("SELECT Name FROM Win32_Battery").Count == 0)
        {
            d.SkipReason = "Máy không có pin (máy bàn hoặc pin đã tháo).";
            return d;
        }

        var full = Wmi.TryQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi");
        var cycle = Wmi.TryQuery("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi");
        var s = st.FirstOrDefault();

        d.F("Tên pin", $"{s?.Str("ManufactureName")} {s?.Str("DeviceName")}".Trim());
        var design = st.Sum(x => x.Long("DesignedCapacity"));
        var fullCap = full.Sum(x => x.Long("FullChargedCapacity"));
        if (design > 0) d.F("Dung lượng thiết kế", $"{Fmt.Num(design)} mWh");
        if (fullCap > 0) d.F("Dung lượng sạc đầy hiện tại", $"{Fmt.Num(fullCap)} mWh");
        if (design > 0 && fullCap > 0)
        {
            var wear = Math.Max(0, (1 - (double)fullCap / design) * 100);
            d.F("Độ chai pin", $"{wear:0}%", wear >= 40 ? Tone.Bad : wear >= 20 ? Tone.Warn : Tone.Good);
        }
        if ((cycle.FirstOrDefault()?.Long("CycleCount") ?? 0) is > 0 and var cc) d.F("Số lần sạc", Fmt.Num(cc));

        if (NativeMethods.GetSystemPowerStatus(out var ps) && ps.BatteryLifePercent <= 100)
            d.F("Hiện tại", $"{ps.BatteryLifePercent}% · " + (ps.ACLineStatus == 1 ? "đang cắm sạc" : "đang dùng pin"));
        return d;
    }
}
