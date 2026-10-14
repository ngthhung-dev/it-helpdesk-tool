using System.IO;
using System.Management;
using Microsoft.Win32;

namespace DeskMate.Services;

public sealed record LiveStats(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double RamPercent,
    BatteryState Battery,
    TimeSpan Uptime);

public sealed record BatteryState(bool HasBattery, int Percent, bool Charging, TimeSpan? Remaining);

public sealed record DriveState(
    string Name, string Label, string Format, string Kind,
    double TotalGb, double FreeGb)
{
    public double UsedGb => TotalGb - FreeGb;
    public double UsedPercent => TotalGb <= 0 ? 0 : UsedGb / TotalGb * 100;
}

public sealed record HardwareInfo(
    string OsName, string OsVersion, string OsBuild, string Architecture,
    string ComputerName, string UserName, string Domain,
    string Manufacturer, string Model, string SerialNumber,
    string CpuName, int Cores, int Threads,
    string Gpu, string Mainboard, string Bios,
    double RamTotalGb, string RamDetail);

/// <summary>Đọc thông số máy: số liệu realtime qua Win32 API, phần cứng tĩnh qua WMI.</summary>
public sealed class SystemInfoService
{
    private long _prevIdle, _prevTotal;

    /// <summary>Gọi mỗi giây. Lần gọi đầu CPU = 0 vì chưa có mốc so sánh.</summary>
    public LiveStats Sample()
    {
        return new LiveStats(
            SampleCpu(),
            ReadRam(out var total, out var pct),
            total, pct,
            ReadBattery(),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel + user; // kernel đã bao gồm idle
        var dIdle = idle - _prevIdle;
        var dTotal = total - _prevTotal;
        var first = _prevTotal == 0;
        _prevIdle = idle;
        _prevTotal = total;
        if (first || dTotal <= 0) return 0;
        return Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
    }

    private static double ReadRam(out double totalGb, out double percent)
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m)) { totalGb = percent = 0; return 0; }
        totalGb = m.ullTotalPhys / Gb;
        var used = (m.ullTotalPhys - m.ullAvailPhys) / Gb;
        percent = totalGb > 0 ? used / totalGb * 100 : 0;
        return used;
    }

    private static BatteryState ReadBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryFlag == 255)
            return new BatteryState(false, 0, false, null);

        var pct = s.BatteryLifePercent == 255 ? 0 : s.BatteryLifePercent;
        TimeSpan? remain = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new BatteryState(true, pct, s.ACLineStatus == 1, remain);
    }

    public static IReadOnlyList<DriveState> GetDrives()
    {
        var list = new List<DriveState>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(new DriveState(
                    d.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                    d.DriveFormat,
                    d.DriveType == DriveType.Removable ? "USB" : "Ổ cứng",
                    d.TotalSize / Gb,
                    d.AvailableFreeSpace / Gb));
            }
            catch (IOException) { /* ổ vừa rút ra */ }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>Đọc thông tin phần cứng (chậm ~1–2s) — gọi trong Task.Run.</summary>
    public static HardwareInfo GetHardware()
    {
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var cs = First("SELECT Manufacturer, Model, Domain FROM Win32_ComputerSystem");
        var board = First("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var bios = First("SELECT SMBIOSBIOSVersion, SerialNumber, ReleaseDate FROM Win32_BIOS");

        var gpus = All("SELECT Name FROM Win32_VideoController")
            .Select(o => Str(o, "Name"))
            .Where(n => n.Length > 0 && !n.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .Distinct();

        var sticks = All("SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory").ToList();
        var ramTotal = sticks.Sum(s => Convert.ToDouble(s["Capacity"] ?? 0)) / Gb;
        var speed = sticks.Select(s => Num(s, "ConfiguredClockSpeed") is > 0 and var c ? c : Num(s, "Speed")).FirstOrDefault();
        var ramDetail = sticks.Count == 0 ? "" :
            $"{sticks.Count} thanh" + (speed > 0 ? $" · {speed} MT/s" : "");

        var (osName, display, build, arch) = ReadOs();

        return new HardwareInfo(
            osName, display, build, arch,
            Environment.MachineName, Environment.UserName, Str(cs, "Domain"),
            Str(cs, "Manufacturer"), Str(cs, "Model"), Str(bios, "SerialNumber"),
            Str(cpu, "Name").Trim(), Num(cpu, "NumberOfCores"), Num(cpu, "NumberOfLogicalProcessors"),
            string.Join(" + ", gpus),
            $"{Str(board, "Manufacturer")} {Str(board, "Product")}".Trim(),
            Str(bios, "SMBIOSBIOSVersion"),
            ramTotal, ramDetail);
    }

    private static (string Name, string Display, string Build, string Arch) ReadOs()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var name = k?.GetValue("ProductName") as string ?? "Windows";
        var build = k?.GetValue("CurrentBuild") as string ?? "";
        var ubr = k?.GetValue("UBR")?.ToString();
        var display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";

        // Windows 11 vẫn ghi "Windows 10" trong ProductName — sửa theo số build
        if (int.TryParse(build, out var b) && b >= 22000)
            name = name.Replace("Windows 10", "Windows 11");

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return (name, display, ubr is null ? build : $"{build}.{ubr}", arch);
    }

    private const double Gb = 1024d * 1024 * 1024;

    private static IEnumerable<ManagementBaseObject> All(string query)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch { return []; }
    }

    private static ManagementBaseObject? First(string query) => All(query).FirstOrDefault();

    private static string Str(ManagementBaseObject? o, string prop)
    {
        try { return o?[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; }
    }

    private static int Num(ManagementBaseObject? o, string prop)
    {
        try { return o?[prop] is { } v ? Convert.ToInt32(v) : 0; } catch { return 0; }
    }
}
