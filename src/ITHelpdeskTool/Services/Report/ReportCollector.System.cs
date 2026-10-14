using System.Globalization;
using ITHelpdeskTool.Core;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Report;

public static partial class ReportCollector
{
    // ================= Tổng quan máy =================
    private static SectionData Machine()
    {
        var d = new SectionData();
        var cs = Wmi.First("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, Domain, PartOfDomain, Workgroup, UserName FROM Win32_ComputerSystem");
        var csp = Wmi.First("SELECT UUID FROM Win32_ComputerSystemProduct");
        var bios = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS");
        var board = Wmi.First("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
        var chassis = Wmi.First("SELECT ChassisTypes FROM Win32_SystemEnclosure");

        d.F("Tên máy", Environment.MachineName);
        d.F("Người dùng đang đăng nhập", cs?.Str("UserName") is { Length: > 0 } u ? u : $@"{Environment.UserDomainName}\{Environment.UserName}");
        if (cs?.Bool("PartOfDomain") == true) d.F("Domain", cs.Str("Domain"));
        else d.F("Workgroup", cs?.Str("Workgroup") is { Length: > 0 } wg ? wg : cs?.Str("Domain"));

        d.F("Hãng sản xuất", cs?.Str("Manufacturer"));
        var model = cs?.Str("Model") ?? "";
        var family = cs?.Str("SystemFamily") ?? "";
        d.F("Model", family.Length > 0 && !model.Contains(family, StringComparison.OrdinalIgnoreCase) ? $"{family} · {model}" : model);
        d.F("Mã SKU", Clean(cs?.Str("SystemSKUNumber")));
        d.F("Loại máy", ChassisName(chassis?.UShorts("ChassisTypes") ?? []));
        d.F("Serial (Service Tag)", Clean(bios?.Str("SerialNumber")));
        d.F("UUID", csp?.Str("UUID"));

        var boardText = $"{board?.Str("Manufacturer")} {board?.Str("Product")}".Trim();
        var boardVer = Clean(board?.Str("Version"));
        d.F("Mainboard", boardVer.Length > 0 ? $"{boardText} (rev {boardVer})" : boardText);
        d.F("Serial mainboard", Clean(board?.Str("SerialNumber")));
        d.F("BIOS", $"{bios?.Str("Manufacturer")} {bios?.Str("SMBIOSBIOSVersion")}".Trim());
        d.F("Ngày BIOS", Fmt.Date(bios?.Date("ReleaseDate")));

        var fw = FirmwareType();
        d.F("Chế độ khởi động", fw, fw == "Legacy BIOS" ? Tone.Warn : Tone.Normal);
        var sb = SecureBoot();
        d.F("Secure Boot", Fmt.YesNo(sb, "Bật", "Tắt"), sb == true ? Tone.Good : sb == false ? Tone.Warn : Tone.Muted);
        d.F("Thời gian chạy liên tục", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        return d;
    }

    // ================= Hệ điều hành =================
    private static SectionData Os()
    {
        var d = new SectionData();
        var os = Wmi.First("SELECT Caption, OSArchitecture, InstallDate, LastBootUpTime, SerialNumber, WindowsDirectory, RegisteredUser, Organization FROM Win32_OperatingSystem");

        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = cv?.GetValue("CurrentBuild") as string ?? "";
        var ubr = cv?.GetValue("UBR")?.ToString();
        var name = os?.Str("Caption") is { Length: > 0 } c ? c.Replace("Microsoft ", "") : cv?.GetValue("ProductName") as string ?? "Windows";
        if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");

        d.F("Tên", name);
        d.F("Phiên bản", cv?.GetValue("DisplayVersion") as string ?? cv?.GetValue("ReleaseId") as string);
        d.F("Build", ubr is null ? build : $"{build}.{ubr}");
        d.F("Kiến trúc", os?.Str("OSArchitecture"));
        d.F("Ngày cài Windows", Fmt.Date(os?.Date("InstallDate")));
        d.F("Khởi động lần cuối", Fmt.Stamp(os?.Date("LastBootUpTime")));
        d.F("Product ID", os?.Str("SerialNumber"));
        d.F("Đăng ký cho", $"{os?.Str("RegisteredUser")} {os?.Str("Organization")}".Trim());
        d.F("Ngôn ngữ giao diện", CultureInfo.InstalledUICulture.DisplayName);
        d.F("Múi giờ", TimeZoneInfo.Local.DisplayName);
        d.F("Thư mục Windows", os?.Str("WindowsDirectory"));

        // Kích hoạt — truy vấn này hơi chậm (vài giây)
        var lic = Wmi.TryQuery(
            "SELECT Name, Description, LicenseStatus, PartialProductKey FROM SoftwareLicensingProduct " +
            "WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL",
            timeoutSec: 30).FirstOrDefault();
        if (lic is null)
            d.F("Kích hoạt", "Không đọc được", Tone.Muted);
        else
        {
            var st = (int)lic.Long("LicenseStatus");
            d.F("Kích hoạt", LicenseText(st), st == 1 ? Tone.Good : Tone.Bad);
            d.F("Kênh bản quyền", LicenseChannel(lic.Str("Description")));
            d.F("Khoá (5 ký tự cuối)", lic.Str("PartialProductKey"));
        }
        return d;
    }

    // ================= CPU =================
    private static SectionData Cpu()
    {
        var d = new SectionData();
        var cpus = Wmi.TryQuery("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled, LoadPercentage FROM Win32_Processor");
        var cs = Wmi.First("SELECT HypervisorPresent FROM Win32_ComputerSystem");
        if (cpus.Count == 0) throw new InvalidOperationException("Không đọc được thông tin CPU");

        var c = cpus[0];
        d.F("Tên", c.Str("Name"));
        d.F("Hãng", c.Str("Manufacturer").Replace("GenuineIntel", "Intel").Replace("AuthenticAMD", "AMD"));
        if (cpus.Count > 1) d.F("Số CPU vật lý", cpus.Count.ToString());
        d.F("Số nhân / luồng", $"{cpus.Sum(x => x.Long("NumberOfCores"))} nhân / {cpus.Sum(x => x.Long("NumberOfLogicalProcessors"))} luồng");
        if (c.Long("MaxClockSpeed") is > 0 and var mhz) d.F("Xung cơ bản", $"{mhz / 1000.0:0.00} GHz");
        if (c.Long("L2CacheSize") is > 0 and var l2) d.F("Cache L2", CacheText(l2));
        if (c.Long("L3CacheSize") is > 0 and var l3) d.F("Cache L3", CacheText(l3));
        d.F("Socket", Clean(c.Str("SocketDesignation")));

        // Khi Hyper-V/WSL2 đang chạy, Windows báo VirtualizationFirmwareEnabled = false dù BIOS đã bật
        if (cs?.Bool("HypervisorPresent") == true)
            d.F("Ảo hoá (VT-x/AMD-V)", "Đang dùng (Hyper-V/WSL)", Tone.Good);
        else
        {
            var vt = c.Bool("VirtualizationFirmwareEnabled");
            d.F("Ảo hoá (VT-x/AMD-V)", Fmt.YesNo(vt, "Đã bật trong BIOS", "Tắt trong BIOS"), vt == true ? Tone.Good : Tone.Muted);
        }
        if (c["LoadPercentage"] is not null) d.F("Tải hiện tại", $"{c.Long("LoadPercentage")}%");
        return d;
    }

    // ================= RAM =================
    private static SectionData Memory()
    {
        var d = new SectionData();
        var sticks = Wmi.TryQuery("SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, SerialNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
        var arrays = Wmi.TryQuery("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity FROM Win32_PhysicalMemoryArray");

        var total = sticks.Sum(s => s.Long("Capacity"));
        var slots = arrays.Sum(a => a.Long("MemoryDevices"));
        var maxKb = arrays.Sum(a => a.Long("MaxCapacityEx") is > 0 and var ex ? ex : a.Long("MaxCapacity"));

        d.F("Tổng dung lượng", Fmt.Size(total));
        d.F("Khe đã dùng", slots > 0 ? $"{sticks.Count} / {slots} khe" : $"{sticks.Count} thanh");
        if (maxKb > 0) d.F("Hỗ trợ tối đa", Fmt.Size(maxKb * 1024d));
        if (sticks.Count > 0)
        {
            var first = sticks[0];
            d.F("Loại", $"{MemoryType((int)first.Long("SMBIOSMemoryType"))} {FormFactor((int)first.Long("FormFactor"))}".Trim());
            var speeds = sticks.Select(s => s.Long("ConfiguredClockSpeed") is > 0 and var cfg ? cfg : s.Long("Speed")).Distinct().ToList();
            d.F("Tốc độ đang chạy", string.Join(" / ", speeds.Where(x => x > 0).Select(x => $"{x} MT/s")),
                speeds.Count > 1 ? Tone.Warn : Tone.Normal);
            if (sticks.Select(s => s.Long("Capacity")).Distinct().Count() > 1)
                d.F("Ghi chú", "Các thanh RAM khác dung lượng — có thể không chạy kênh đôi đầy đủ", Tone.Warn);
        }

        var t = d.Table("Từng thanh RAM", ["Khe", "Dung lượng", "Loại", "Tốc độ", "Hãng", "Part number", "Serial"],
            [1.2, 1, 1, 1, 1.1, 1.6, 1.1]);
        foreach (var s in sticks)
        {
            var slot = Clean(s.Str("DeviceLocator"));
            var bank = Clean(s.Str("BankLabel"));
            t.Add(
                bank.Length > 0 && !slot.Contains(bank) ? $"{slot} ({bank})".Trim() : slot,
                Fmt.Size(s.Long("Capacity")),
                MemoryType((int)s.Long("SMBIOSMemoryType")),
                (s.Long("ConfiguredClockSpeed") is > 0 and var cfg ? cfg : s.Long("Speed")) is > 0 and var mts ? $"{mts} MT/s" : "",
                RamVendor(s.Str("Manufacturer")),
                Clean(s.Str("PartNumber")),
                Clean(s.Str("SerialNumber")));
        }
        return d;
    }

    // ================= Tiện ích =================
    private static string Clean(string? s)
    {
        s = s?.Trim() ?? "";
        s = s.TrimEnd('.', ' ');
        string[] junk = ["To be filled by O.E.M.", "To Be Filled By O.E.M", "Default string", "System Serial Number", "System Product Name",
                         "None", "0", "00000000", "Not Specified", "Not Available", "N/A", "123456789", "Unknown", "Base Board Serial Number"];
        return junk.Any(j => s.Equals(j, StringComparison.OrdinalIgnoreCase)) ? "" : s;
    }

    private static string ChassisName(ushort[] types)
    {
        foreach (var t in types)
        {
            var n = t switch
            {
                3 or 4 or 5 or 6 or 7 or 15 or 16 => "Máy bàn (Desktop)",
                8 or 9 or 10 or 14 => "Laptop",
                31 or 32 => "Laptop 2-in-1",
                30 => "Máy tính bảng",
                13 => "All-in-One",
                35 or 36 => "Mini PC",
                17 or 23 or 28 => "Máy chủ",
                _ => null,
            };
            if (n is not null) return n;
        }
        return "";
    }

    private static string FirmwareType()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
        return (k?.GetValue("PEFirmwareType") as int?) switch
        {
            2 => "UEFI",
            1 => "Legacy BIOS",
            _ => "",
        };
    }

    internal static bool? SecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
    }

    private static string LicenseText(int s) => s switch
    {
        0 => "Chưa kích hoạt",
        1 => "Đã kích hoạt",
        2 or 3 or 6 => "Đang trong thời gian dùng thử",
        4 => "Bản quyền không hợp lệ",
        5 => "Chưa kích hoạt (đang nhắc kích hoạt)",
        _ => $"Không rõ ({s})",
    };

    private static string LicenseChannel(string desc)
    {
        // vd "Windows(R) Operating System, RETAIL channel"
        var i = desc.LastIndexOf(',');
        var ch = (i >= 0 ? desc[(i + 1)..] : desc).Replace("channel", "", StringComparison.OrdinalIgnoreCase).Trim();
        return ch.ToUpperInvariant() switch
        {
            "RETAIL" => "Retail (bản lẻ)",
            "OEM_DM" or "OEM_SLP" or "OEM_COA_NSLP" or "OEM_COA_SLP" => $"OEM (bản quyền theo máy) · {ch}",
            "VOLUME_KMSCLIENT" => "Volume KMS",
            "VOLUME_MAK" => "Volume MAK",
            _ => ch,
        };
    }

    private static string CacheText(long kb) => kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

    private static string MemoryType(int t) => t switch
    {
        20 => "DDR",
        21 or 22 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    private static string FormFactor(int f) => f switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        0 or 1 => "",
        _ => "",
    };

    /// <summary>Một số máy trả mã JEDEC thay vì tên hãng.</summary>
    private static string RamVendor(string m)
    {
        var key = m.Trim().ToUpperInvariant();
        if (key.Length >= 4 && key.All(Uri.IsHexDigit))
        {
            key = key.TrimStart('0');
            return key switch
            {
                "80CE" or "CE" or "80CE000080CE" => "Samsung",
                "80AD" or "AD" or "80AD000080AD" => "SK Hynix",
                "802C" or "2C" or "802C0000802C" => "Micron",
                "859B" or "9B" => "Crucial",
                "198" or "8198" => "Kingston",
                "29E" or "829E" => "Corsair",
                "4CD" or "84CD" => "G.Skill",
                "4CB" or "84CB" => "ADATA",
                "551" or "8551" => "Qimonda",
                "5CB" or "85CB" => "Lexar",
                _ => m,
            };
        }
        return m;
    }
}
