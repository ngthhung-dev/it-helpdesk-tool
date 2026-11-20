using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    public enum CheckLevel { Ok, Info, Warn, Block }

    public sealed class Check
    {
        public Check(CheckLevel level, string title, string detail = "")
        {
            Level = level;
            Title = title;
            Detail = detail;
        }

        public CheckLevel Level { get; }
        public string Title { get; }
        public string Detail { get; }

        public override string ToString() => $"[{Level}] {Title}" + (Detail.Length > 0 ? " — " + Detail : "");
    }

    /// <summary>Thông tin máy đọc một lần bằng PowerShell / WMI (chạy được cả Windows 7).</summary>
    public sealed class MachineFacts
    {
        public FirmwareMode Firmware { get; set; }
        public bool? SecureBoot { get; set; }
        /// <summary>Phiên bản TPM chính (2 = TPM 2.0, 1 = 1.2, 0 = không có).</summary>
        public int Tpm { get; set; }
        public long RamBytes { get; set; }
        public bool Is64Bit { get; set; } = Environment.Is64BitOperatingSystem;
        public bool HasBattery { get; set; }
        public bool OnAc { get; set; } = true;
        public List<string> StorageControllers { get; set; } = new List<string>();
        /// <summary>Ổ (C:) → 1 = BitLocker đang bật / đang mã hoá dở.</summary>
        public Dictionary<string, int> BitLocker { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public bool HasRaid => StorageControllers.Any(c => Regex.IsMatch(c, @"RAID|RST|Rapid Storage|VMD|Optane", RegexOptions.IgnoreCase));

        private const string Script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "$m=Get-WmiObject Win32_ComputerSystem; 'RAM=' + $m.TotalPhysicalMemory;" +
            "$b=@(Get-WmiObject Win32_Battery); 'BATTERY=' + $b.Count; if($b.Count){ 'AC=' + [int](($b | ?{ $_.BatteryStatus -eq 2 }).Count -gt 0) };" +
            "$t=Get-WmiObject -Namespace root\\cimv2\\security\\microsofttpm -Class Win32_Tpm; 'TPM=' + $t.SpecVersion;" +
            "try { 'SECUREBOOT=' + [int](Confirm-SecureBootUEFI) } catch { 'SECUREBOOT=' };" +
            "Get-WmiObject Win32_SCSIController | %{ 'STORAGE=' + $_.Name };" +
            "Get-WmiObject Win32_IDEController | %{ 'STORAGE=' + $_.Name };" +
            "Get-WmiObject -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -Class Win32_EncryptableVolume | " +
            "%{ 'BITLOCKER=' + $_.DriveLetter + '|' + [int]($_.ProtectionStatus -eq 1 -or $_.ConversionStatus -ne 0) }";

        public static MachineFacts Read(IRunner run)
        {
            var f = new MachineFacts { Firmware = BootEntry.DetectFirmware(run) };
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + Script + "\"");
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line.Substring(0, i);
                var v = line.Substring(i + 1);
                switch (k)
                {
                    case "RAM": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ram); f.RamBytes = ram; break;
                    case "BATTERY": f.HasBattery = v != "0" && v.Length > 0; break;
                    case "AC": f.OnAc = v == "1"; break;
                    case "TPM": f.Tpm = v.Length == 0 ? 0 : v.TrimStart().StartsWith("2", StringComparison.Ordinal) ? 2 : 1; break;
                    case "SECUREBOOT": f.SecureBoot = v == "1" ? true : v == "0" ? false : (bool?)null; break;
                    case "STORAGE": if (v.Length > 0) f.StorageControllers.Add(v); break;
                    case "BITLOCKER":
                        var p = v.Split('|');
                        if (p.Length == 2 && p[0].Length > 0) f.BitLocker[p[0]] = p[1] == "1" ? 1 : 0;
                        break;
                }
            }
            return f;
        }
    }

    /// <summary>Kiểm tra trước khi cài lại. Có mục Block thì không cho chạy.</summary>
    public static class Preflight
    {
        /// <param name="cpuSupportsWin11">Kết quả kiểm tra CPU của app (null = không rõ).</param>
        public static List<Check> Run(ReinstallPlan p, MachineFacts f, long imageBytes, bool? cpuSupportsWin11 = null)
        {
            var list = new List<Check>();
            var target = Norm(p.TargetDrive);
            var stagingRoot = Norm(Path.GetPathRoot(Path.GetFullPath(p.StagingDir)) ?? "");
            var sysDrive = Norm(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:");

            if (!target.Equals(sysDrive, StringComparison.OrdinalIgnoreCase))
                list.Add(new Check(CheckLevel.Block, $"Chỉ cài lại được ổ Windows đang chạy ({sysDrive})", $"Đang chọn {target}."));
            if (stagingRoot.Equals(target, StringComparison.OrdinalIgnoreCase))
                list.Add(new Check(CheckLevel.Block, "Thư mục đệm phải nằm trên ổ khác ổ Windows", $"{p.StagingDir} nằm trên {target} — ổ này sẽ bị xoá."));

            // ---- Ổ đệm ----
            DriveInfo? d = null;
            try { d = new DriveInfo(stagingRoot); } catch { }
            if (d == null || !d.IsReady)
                list.Add(new Check(CheckLevel.Block, $"Không đọc được ổ {stagingRoot}"));
            else
            {
                if (d.DriveFormat != "NTFS" && !d.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
                    list.Add(new Check(CheckLevel.Block, $"Ổ {stagingRoot} định dạng {d.DriveFormat}", "Trình khởi động chỉ đọc được NTFS / FAT32."));
                if (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Network || d.DriveType == DriveType.CDRom)
                    list.Add(new Check(CheckLevel.Block, $"Ổ {stagingRoot} là ổ rời / ổ mạng", "Hãy dùng một phân vùng ổ cứng trong máy (vd D:)."));
                var need = imageBytes + 3L * 1024 * 1024 * 1024;   // boot.wim, driver, dư
                if (d.AvailableFreeSpace < need)
                    list.Add(new Check(CheckLevel.Block, $"Ổ {stagingRoot} không đủ chỗ trống",
                        $"Cần ~{need / 1073741824.0:0.0} GB, còn {d.AvailableFreeSpace / 1073741824.0:0.0} GB."));
                else list.Add(new Check(CheckLevel.Ok, $"Ổ đệm {stagingRoot} đủ chỗ ({d.AvailableFreeSpace / 1073741824.0:0.0} GB trống)"));
            }

            // ---- BitLocker: trình khởi động không đọc được ổ mã hoá; ổ Windows mã hoá thì phải tắt trước ----
            foreach (var drive in new[] { target, stagingRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (f.BitLocker.TryGetValue(drive, out var bl) && bl == 1)
                    list.Add(new Check(CheckLevel.Block, $"Ổ {drive} đang bật BitLocker",
                        "Tắt BitLocker (Control Panel → BitLocker → Turn off) và chờ giải mã xong rồi làm lại."));

            // ---- Điện ----
            if (f.HasBattery && !f.OnAc)
                list.Add(new Check(CheckLevel.Warn, "Máy đang chạy pin", "Cắm sạc trước khi cài — mất điện giữa chừng sẽ hỏng Windows."));

            // ---- Ổ cứng RAID / Intel RST ----
            if (f.HasRaid)
                list.Add(new Check(CheckLevel.Info, "Máy dùng RAID / Intel RST",
                    "Driver bộ điều khiển ổ cứng sẽ được nạp vào môi trường cài: " + string.Join(", ", f.StorageControllers)));

            if (!p.ExportDrivers && f.HasRaid)
                list.Add(new Check(CheckLevel.Block, "Cần xuất driver cho máy RAID / Intel RST", "Không có driver, môi trường cài sẽ không thấy ổ cứng."));

            // ---- Gợi ý bản Windows ----
            list.Add(Recommend(f, cpuSupportsWin11));
            list.Add(new Check(CheckLevel.Info, $"Khởi động kiểu {(f.Firmware == FirmwareMode.Uefi ? "UEFI" : "Legacy BIOS")}"));
            return list;
        }

        public static Check Recommend(MachineFacts f, bool? cpuSupportsWin11)
        {
            var ramGb = f.RamBytes / 1073741824.0;
            if (!f.Is64Bit || f.RamBytes > 0 && ramGb < 3.5)
                return new Check(CheckLevel.Info, "Nên cài Windows 10 (32-bit hoặc LTSC)",
                    !f.Is64Bit ? "Máy đang chạy Windows 32-bit." : $"RAM chỉ {ramGb:0.#} GB.");
            var missing = new List<string>();
            if (f.Firmware != FirmwareMode.Uefi) missing.Add("khởi động UEFI");
            if (f.Tpm < 2) missing.Add("TPM 2.0");
            if (f.SecureBoot == null) missing.Add("Secure Boot");
            if (cpuSupportsWin11 == false) missing.Add("CPU được hỗ trợ");
            if (ramGb < 4) missing.Add("RAM 4 GB");
            return missing.Count == 0
                ? new Check(CheckLevel.Ok, "Máy đủ điều kiện Windows 11")
                : new Check(CheckLevel.Info, "Nên cài Windows 10", "Thiếu điều kiện Windows 11: " + string.Join(", ", missing) + ".");
        }

        private static string Norm(string drive) => drive.Trim().TrimEnd('\\').ToUpperInvariant();
    }
}
