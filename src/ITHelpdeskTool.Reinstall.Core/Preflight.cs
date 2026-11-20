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
        public Check(CheckLevel level, string title, string detail = "", string confirmKey = "")
        {
            Level = level;
            Title = title;
            Detail = detail;
            ConfirmKey = confirmKey;
        }

        public CheckLevel Level { get; }
        public string Title { get; }
        public string Detail { get; }
        /// <summary>Mục cần người dùng xác nhận riêng (khoá xác nhận). Chưa xác nhận thì Level = Block.</summary>
        public string ConfirmKey { get; }

        public override string ToString() => $"[{Level}] {Title}" + (Detail.Length > 0 ? " — " + Detail : "") +
                                             (ConfirmKey.Length > 0 && Level == CheckLevel.Block ? $" (xác nhận: --confirm {ConfirmKey})" : "");
    }

    /// <summary>Khoá xác nhận của các mục cần người dùng đồng ý riêng.</summary>
    public static class ConfirmKeys
    {
        /// <summary>Máy thuộc quản lý của tổ chức (Entra ID / Intune / domain).</summary>
        public const string Organization = "org";
        /// <summary>Đã chép khoá khôi phục BitLocker của các ổ dữ liệu.</summary>
        public const string BitLockerKeys = "bitlocker-keys";
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
        /// <summary>CPU chạy được 64-bit (Win32_Processor.DataWidth).</summary>
        public int CpuBits { get; set; } = 64;
        public string CpuName { get; set; } = "";
        /// <summary>Bản Windows đang dùng: EditionID (Core, Professional…) và tên.</summary>
        public string EditionId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public int Build { get; set; }
        public bool HasBattery { get; set; }
        public bool OnAc { get; set; } = true;
        public List<string> StorageControllers { get; set; } = new List<string>();
        /// <summary>Ổ (C:) → tình trạng BitLocker.</summary>
        public Dictionary<string, BitLockerVolume> BitLocker { get; set; } = new Dictionary<string, BitLockerVolume>(StringComparer.OrdinalIgnoreCase);
        public AccountFacts Account { get; set; } = new AccountFacts();

        public bool HasRaid => StorageControllers.Any(c => Regex.IsMatch(c, @"RAID|RST|Rapid Storage|VMD|Optane", RegexOptions.IgnoreCase));

        /// <summary>Đọc BitLocker mọi ổ: BITLOCKER=ổ|bảo vệ|chuyển đổi|%|loại ổ|khoá.</summary>
        internal const string BitLockerScript =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-WmiObject -Namespace root\\cimv2\\Security\\MicrosoftVolumeEncryption -Class Win32_EncryptableVolume | %{ " +
            "$c=$_.GetConversionStatus(); $l=$_.GetLockStatus(); " +
            "'BITLOCKER=' + $_.DriveLetter + '|' + $_.ProtectionStatus + '|' + $c.ConversionStatus + '|' + $c.EncryptionPercentage + '|' + $_.VolumeType + '|' + $l.LockStatus }";

        private const string Script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "$m=Get-WmiObject Win32_ComputerSystem; 'RAM=' + $m.TotalPhysicalMemory; 'DOMAIN=' + [int]$m.PartOfDomain + '|' + $m.Domain;" +
            "$b=@(Get-WmiObject Win32_Battery); 'BATTERY=' + $b.Count; if($b.Count){ 'AC=' + [int](($b | ?{ $_.BatteryStatus -eq 2 }).Count -gt 0) };" +
            "$p=Get-WmiObject Win32_Processor | Select -First 1; 'CPUBITS=' + $p.DataWidth; 'CPUNAME=' + $p.Name.Trim();" +
            "$v=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion'; 'EDITION=' + $v.EditionID; 'PRODUCT=' + $v.ProductName; 'BUILD=' + $v.CurrentBuild;" +
            "$t=Get-WmiObject -Namespace root\\cimv2\\security\\microsofttpm -Class Win32_Tpm; 'TPM=' + $t.SpecVersion;" +
            "try { 'SECUREBOOT=' + [int](Confirm-SecureBootUEFI) } catch { 'SECUREBOOT=' };" +
            "Get-WmiObject Win32_SCSIController | %{ 'STORAGE=' + $_.Name };" +
            "Get-WmiObject Win32_IDEController | %{ 'STORAGE=' + $_.Name };" +
            "try { 'ACCOUNT=' + (Get-LocalUser -Name '{{USER}}').PrincipalSource } catch { 'ACCOUNT=' };" +
            "dsregcmd /status | Select-String 'AzureAdJoined|WorkplaceJoined' | %{ 'JOIN=' + ($_.Line -replace '\\s','') };" +
            "'MDM=' + @(Get-ChildItem HKLM:\\SOFTWARE\\Microsoft\\Enrollments | ?{ (Get-ItemProperty $_.PSPath).ProviderID -eq 'MS DM Server' }).Count;";

        /// <param name="userName">Tài khoản cần xét (người đang đăng nhập); mặc định là tài khoản chạy chương trình.</param>
        public static MachineFacts Read(IRunner run, string? userName = null)
        {
            var f = new MachineFacts { Firmware = BootEntry.DetectFirmware(run) };
            var user = (userName ?? Environment.UserName).Replace("'", "''").Replace("\"", "");
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + Script.Replace("{{USER}}", user) + BitLockerScript + "\"");
            Parse(f, r.Output);
            return f;
        }

        internal static void Parse(MachineFacts f, string output)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line.Substring(0, i);
                var v = line.Substring(i + 1);
                switch (k)
                {
                    case "RAM": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ram); f.RamBytes = ram; break;
                    case "CPUBITS": f.CpuBits = v == "32" ? 32 : 64; break;
                    case "CPUNAME": f.CpuName = v; break;
                    case "EDITION": f.EditionId = v; break;
                    case "PRODUCT": f.ProductName = v; break;
                    case "BUILD": int.TryParse(v, out var build); f.Build = build; break;
                    case "BATTERY": f.HasBattery = v != "0" && v.Length > 0; break;
                    case "AC": f.OnAc = v == "1"; break;
                    case "TPM": f.Tpm = v.Length == 0 ? 0 : v.TrimStart().StartsWith("2", StringComparison.Ordinal) ? 2 : 1; break;
                    case "SECUREBOOT": f.SecureBoot = v == "1" ? true : v == "0" ? false : (bool?)null; break;
                    case "STORAGE": if (v.Length > 0) f.StorageControllers.Add(v); break;
                    case "ACCOUNT": f.Account.Source = v; break;
                    case "DOMAIN":
                        var d = v.Split('|');
                        f.Account.DomainJoined = d[0] == "1";
                        f.Account.Domain = d.Length > 1 ? d[1] : "";
                        break;
                    case "JOIN":
                        if (v.StartsWith("AzureAdJoined:YES", StringComparison.OrdinalIgnoreCase)) f.Account.AzureAdJoined = true;
                        if (v.StartsWith("WorkplaceJoined:YES", StringComparison.OrdinalIgnoreCase)) f.Account.WorkplaceJoined = true;
                        break;
                    case "MDM": f.Account.MdmEnrolled = v.Length > 0 && v != "0"; break;
                }
            }
            foreach (var b in ParseBitLocker(output)) f.BitLocker[b.Drive] = b;
            // Windows 11 vẫn ghi ProductName "Windows 10 …" trong registry
            if (f.Build >= 22000) f.ProductName = f.ProductName.Replace("Windows 10", "Windows 11");
        }

        internal static List<BitLockerVolume> ParseBitLocker(string output)
        {
            var list = new List<BitLockerVolume>();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("BITLOCKER=", StringComparison.Ordinal)) continue;
                var p = line.Substring(10).Split('|');
                if (p.Length < 4 || p[0].Length == 0) continue;
                int I(int idx, int def) => p.Length > idx && int.TryParse(p[idx], out var x) ? x : def;
                double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
                list.Add(new BitLockerVolume
                {
                    Drive = p[0].ToUpperInvariant(), Protected = I(1, 0) == 1, Conversion = I(2, 0), Percent = pct,
                    VolumeType = I(4, -1), Locked = I(5, 0) == 1,
                });
            }
            return list;
        }
    }

    /// <summary>Kiểm tra trước khi cài lại. Có mục Block thì không cho chạy.</summary>
    public static class Preflight
    {
        /// <param name="cpuSupportsWin11">Kết quả kiểm tra CPU của app (null = không rõ).</param>
        /// <param name="confirmed">Khoá các mục người dùng đã xác nhận (<see cref="ConfirmKeys"/>).</param>
        public static List<Check> Run(ReinstallPlan p, MachineFacts f, long neededBytes, bool? cpuSupportsWin11 = null, ICollection<string>? confirmed = null)
        {
            var list = new List<Check>();
            var target = Norm(p.TargetDrive);
            var stagingRoot = Norm(Path.GetPathRoot(Path.GetFullPath(p.StagingDir)) ?? "");
            var sysDrive = Norm(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:");
            Check Confirmable(string key, string title, string detail) =>
                new Check(confirmed != null && confirmed.Contains(key) ? CheckLevel.Warn : CheckLevel.Block, title, detail, key);

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
                var need = neededBytes + 3L * 1024 * 1024 * 1024;   // boot.wim, driver, dư
                if (d.AvailableFreeSpace < need)
                    list.Add(new Check(CheckLevel.Block, $"Ổ {stagingRoot} không đủ chỗ trống",
                        $"Cần ~{need / 1073741824.0:0.0} GB (file cài + sao lưu), còn {d.AvailableFreeSpace / 1073741824.0:0.0} GB."));
                else list.Add(new Check(CheckLevel.Ok, $"Ổ đệm {stagingRoot} đủ chỗ ({d.AvailableFreeSpace / 1073741824.0:0.0} GB trống)"));
            }

            // ---- BitLocker / mã hoá thiết bị ----
            if (f.BitLocker.TryGetValue(target, out var tv) && tv.Encrypted)
                list.Add(tv.Decrypting
                    ? new Check(CheckLevel.Block, $"Ổ {target} đang giải mã ({tv.Percent:0}% còn mã hoá)", "Chờ giải mã xong (vẫn dùng máy bình thường được) rồi làm tiếp.")
                    : new Check(CheckLevel.Block, $"Ổ {target} đang bật mã hoá BitLocker / mã hoá thiết bị",
                        "Phải tắt mã hoá trước: nút “Tắt mã hoá” (ithreinstall decrypt --drive " + target + ") hoặc Settings → Privacy & security → Device encryption → Off. " +
                        "Giải mã chạy nền 10–60 phút."));
            if (!stagingRoot.Equals(target, StringComparison.OrdinalIgnoreCase) && f.BitLocker.TryGetValue(stagingRoot, out var sv) && sv.Encrypted)
                list.Add(new Check(CheckLevel.Block, $"Ổ đệm {stagingRoot} đang mã hoá BitLocker",
                    "Trình khởi động không đọc được ổ mã hoá — chọn ổ khác làm ổ đệm, hoặc tắt mã hoá ổ này."));
            var others = f.BitLocker.Values
                .Where(v => v.Encrypted && !v.Drive.Equals(target, StringComparison.OrdinalIgnoreCase) && !v.Drive.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase) && v.VolumeType != 2)
                .ToList();
            foreach (var v in others.Where(v => v.Locked))
                list.Add(new Check(CheckLevel.Block, $"Ổ {v.Drive} đang khoá BitLocker", "Mở khoá ổ này trước để app lấy được khoá khôi phục."));
            if (others.Any(v => !v.Locked))
                list.Add(new Check(CheckLevel.Warn, "Ổ " + string.Join(", ", others.Select(v => v.Drive)) + " đang mã hoá BitLocker",
                    "Windows mới sẽ không tự mở được các ổ này — app sẽ lưu khoá khôi phục vào bản sao lưu và bắt bạn chép lại trước khi cài."));

            // ---- Tài khoản và quản lý của tổ chức ----
            if (f.Account.ManagedByOrganization)
                list.Add(Confirmable(ConfirmKeys.Organization, $"Máy đang thuộc quản lý của tổ chức: {f.Account.OrganizationText}",
                    "Cài lại sẽ gỡ máy khỏi hệ thống quản lý của công ty (chính sách, phần mềm, Wi-Fi/VPN công ty). " +
                    "Cần IT công ty đồng ý; máy đăng ký Autopilot có thể bị khoá cho tới khi đăng nhập bằng tài khoản công ty; vào lại domain cần tài khoản quản trị domain."));
            if (f.Account.IsMicrosoftAccount)
                list.Add(new Check(CheckLevel.Info, "Đang đăng nhập bằng tài khoản Microsoft",
                    "Windows mới dùng tài khoản riêng: sau khi cài phải đăng nhập lại Microsoft / OneDrive / Office / Store, đặt lại mã PIN, vân tay. " +
                    "File chỉ có trên OneDrive vẫn còn trên cloud. Khoá khôi phục BitLocker của tài khoản xem tại https://aka.ms/myrecoverykey."));

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

        /// <summary>Ổ dữ liệu đang mã hoá (không phải ổ Windows, ổ đệm, ổ rời) — cần lưu khoá khôi phục.</summary>
        public static List<BitLockerVolume> EncryptedDataDrives(ReinstallPlan p, MachineFacts f)
        {
            var target = Norm(p.TargetDrive);
            var stagingRoot = Norm(Path.GetPathRoot(Path.GetFullPath(p.StagingDir)) ?? "");
            return f.BitLocker.Values.Where(v => v.Encrypted && !v.Locked && v.VolumeType != 2
                    && !v.Drive.Equals(target, StringComparison.OrdinalIgnoreCase) && !v.Drive.Equals(stagingRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
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
