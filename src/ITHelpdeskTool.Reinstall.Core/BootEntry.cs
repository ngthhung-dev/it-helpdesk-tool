using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Mục khởi động tạm vào môi trường cài (WinPE nạp từ boot.wim vào RAM).
    /// Dùng /bootsequence: chỉ khởi động vào đó ĐÚNG MỘT LẦN — lỗi hay mất điện thì lần sau máy về Windows cũ.
    /// </summary>
    public sealed class BootEntry
    {
        public string EntryId { get; set; } = "";
        public string RamdiskId { get; set; } = "";

        private static readonly Regex Guid = new Regex(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");

        /// <summary>UEFI hay BIOS: Windows đang chạy khởi động bằng winload.efi hay winload.exe.</summary>
        public static FirmwareMode DetectFirmware(IRunner run)
        {
            var r = run.Run("bcdedit.exe", "/enum {current}");
            if (!r.Ok) throw new ReinstallException("Không đọc được cấu hình khởi động (bcdedit) — cần chạy bằng quyền Administrator.");
            return Regex.IsMatch(r.Output, @"winload\.efi", RegexOptions.IgnoreCase) ? FirmwareMode.Uefi : FirmwareMode.Bios;
        }

        /// <summary>Đường dẫn tới boot.wim / boot.sdi theo dạng bcdedit cần: phân vùng (D:) + đường dẫn trong phân vùng (\ITH\boot.wim).</summary>
        internal static (string Drive, string Path) Split(string file)
        {
            var full = System.IO.Path.GetFullPath(file);
            var root = System.IO.Path.GetPathRoot(full) ?? "";
            return (root.TrimEnd('\\'), "\\" + full.Substring(root.Length));
        }

        /// <summary>Các lệnh bcdedit tạo mục khởi động (dùng để hiển thị / ghi log / test).</summary>
        public static IEnumerable<string> CreateCommands(string bootWim, string bootSdi, FirmwareMode fw, string ramdiskId, string entryId)
        {
            var (wimDrive, wimPath) = Split(bootWim);
            var (sdiDrive, sdiPath) = Split(bootSdi);
            var winload = fw == FirmwareMode.Uefi ? @"\windows\system32\boot\winload.efi" : @"\windows\system32\boot\winload.exe";
            var dev = $"ramdisk=[{wimDrive}]{wimPath},{ramdiskId}";
            yield return $"/set {ramdiskId} ramdisksdidevice partition={sdiDrive}";
            yield return $"/set {ramdiskId} ramdisksdipath {sdiPath}";
            yield return $"/set {entryId} device {dev}";
            yield return $"/set {entryId} osdevice {dev}";
            yield return $"/set {entryId} path {winload}";
            yield return $"/set {entryId} systemroot \\windows";
            yield return $"/set {entryId} winpe yes";
            yield return $"/set {entryId} detecthal yes";
            yield return $"/displayorder {entryId} /addlast";
            yield return $"/bootsequence {entryId}";
        }

        /// <summary>Tạo mục khởi động tạm và đặt khởi động vào đó ở lần bật máy kế tiếp (một lần).</summary>
        public static BootEntry Create(IRunner run, string bootWim, string bootSdi, FirmwareMode fw)
        {
            var e = new BootEntry();
            try
            {
                e.RamdiskId = NewObject(run, "/create /d \"ITH Reinstall Ramdisk\" /device");
                e.EntryId = NewObject(run, "/create /d \"ITH - Cai lai Windows\" /application osloader");
                foreach (var cmd in CreateCommands(bootWim, bootSdi, fw, e.RamdiskId, e.EntryId))
                {
                    var r = run.Run("bcdedit.exe", cmd);
                    if (!r.Ok) throw new ReinstallException($"bcdedit {cmd} lỗi: {ProcessRunner.FirstLines(r.Output, 2)}");
                }
                return e;
            }
            catch
            {
                e.Delete(run);
                throw;
            }
        }

        private static string NewObject(IRunner run, string args)
        {
            var r = run.Run("bcdedit.exe", args);
            var m = Guid.Match(r.Output);
            if (!r.Ok || !m.Success) throw new ReinstallException($"Không tạo được mục khởi động: {ProcessRunner.FirstLines(r.Output, 2)}");
            return m.Value;
        }

        /// <summary>Xoá mục khởi động tạm (khi huỷ, hoặc khi đã cài xong).</summary>
        public void Delete(IRunner run)
        {
            if (EntryId.Length > 0) run.Run("bcdedit.exe", $"/delete {EntryId} /cleanup");
            if (RamdiskId.Length > 0) run.Run("bcdedit.exe", $"/delete {RamdiskId}");
        }

        /// <summary>Lưu mã mục khởi động vào thư mục đệm để môi trường cài / lệnh huỷ xoá được.</summary>
        public void Save(string stagingDir) =>
            File.WriteAllText(Path.Combine(stagingDir, "bootids.cmd"), $"set ITH_ENTRY={EntryId}\r\nset ITH_RAMDISK={RamdiskId}\r\n");

        public static BootEntry? Load(string stagingDir)
        {
            var f = Path.Combine(stagingDir, "bootids.cmd");
            if (!File.Exists(f)) return null;
            var e = new BootEntry();
            foreach (var line in File.ReadAllLines(f))
            {
                var m = Regex.Match(line, @"^set (ITH_ENTRY|ITH_RAMDISK)=(\{.+\})");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "ITH_ENTRY") e.EntryId = m.Groups[2].Value; else e.RamdiskId = m.Groups[2].Value;
            }
            return e;
        }
    }
}
