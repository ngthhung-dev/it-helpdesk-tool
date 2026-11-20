using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Thông tin phân vùng Windows để tính có thu nhỏ được không.</summary>
    public sealed class ShrinkInfo
    {
        public long Size { get; set; }
        /// <summary>Kích thước nhỏ nhất Windows cho phép thu về (bị giới hạn bởi file không di chuyển được: pagefile, ngủ đông, điểm khôi phục…).</summary>
        public long MinSize { get; set; }
        public long Free { get; set; }
        public int Disk { get; set; } = -1;
        public int Partition { get; set; } = -1;
        public long Offset { get; set; }
        /// <summary>GPT hoặc MBR.</summary>
        public string Style { get; set; } = "";
        /// <summary>Số phân vùng chính trên ổ MBR (tối đa 4).</summary>
        public int MbrPrimary { get; set; }
        public bool Hibernation { get; set; }
        public string Error { get; set; } = "";

        public long MaxShrink => Math.Max(0, Size - MinSize);
        public bool Read => Disk >= 0 && Size > 0;
    }

    /// <summary>
    /// Máy chỉ có ổ C: — thu nhỏ C: (như Disk Management → Shrink Volume, không mất dữ liệu) để tạo phân vùng đệm "ITH-STAGING"
    /// ngay sau C:. Cài xong, Windows mới chép bản sao lưu về C:\ITH-Backup, xoá phân vùng đệm và nới C: lại như cũ.
    /// Dùng lệnh Storage của PowerShell (Windows 8 trở lên).
    /// </summary>
    public static class StagingPartition
    {
        public const string Label = "ITH-STAGING";
        public const string Tag = "ITH-AUTOPART.tag";
        private const long GB = 1L << 30;
        private const long MB = 1L << 20;

        /// <summary>Dung lượng phân vùng đệm: file cài + sao lưu + 3 GB (boot.wim, driver) + 2 GB dư, làm tròn lên GB, tối thiểu 12 GB.</summary>
        public static long NeededBytes(long imageBytes, long backupBytes)
        {
            var need = imageBytes + backupBytes + 5 * GB;
            var gb = (need + GB - 1) / GB;
            return Math.Max(12, gb) * GB;
        }

        public static ShrinkInfo Probe(IRunner run, string drive)
        {
            var l = Letter(drive);
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                "$ErrorActionPreference='Stop'; try { " +
                $"$p=Get-Partition -DriveLetter {l}; $s=Get-PartitionSupportedSize -DriveLetter {l}; $v=Get-Volume -DriveLetter {l}; $d=Get-Disk -Number $p.DiskNumber; " +
                "'SIZE=' + $p.Size; 'MIN=' + $s.SizeMin; 'FREE=' + $v.SizeRemaining; 'DISK=' + $p.DiskNumber; 'PART=' + $p.PartitionNumber; 'OFFSET=' + $p.Offset; " +
                "'STYLE=' + $d.PartitionStyle; 'PRIMARY=' + @(Get-Partition -DiskNumber $p.DiskNumber | ?{ $_.MbrType }).Count; " +
                "'HIBER=' + [int](Test-Path (Join-Path $env:SystemDrive 'hiberfil.sys')) " +
                "} catch { 'ERROR=' + $_.Exception.Message }\"");
            return Parse(r.Output);
        }

        internal static ShrinkInfo Parse(string output)
        {
            var s = new ShrinkInfo();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var k = line.Substring(0, i);
                var v = line.Substring(i + 1);
                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n);
                switch (k)
                {
                    case "SIZE": s.Size = n; break;
                    case "MIN": s.MinSize = n; break;
                    case "FREE": s.Free = n; break;
                    case "DISK": s.Disk = (int)n; break;
                    case "PART": s.Partition = (int)n; break;
                    case "OFFSET": s.Offset = n; break;
                    case "STYLE": s.Style = v.ToUpperInvariant(); break;
                    case "PRIMARY": s.MbrPrimary = (int)n; break;
                    case "HIBER": s.Hibernation = v == "1"; break;
                    case "ERROR": s.Error = v; break;
                }
            }
            return s;
        }

        /// <summary>Có thu nhỏ C: được đủ <paramref name="needed"/> byte không. Block = không được.</summary>
        public static List<Check> Check(ShrinkInfo s, long needed)
        {
            var list = new List<Check>();
            var needGb = needed / (double)GB;
            if (!s.Read)
            {
                list.Add(new Check(CheckLevel.Block, "Không đọc được phân vùng ổ Windows để tạo phân vùng đệm",
                    s.Error.Length > 0 ? s.Error : "Windows 7 chưa hỗ trợ tự tạo — hãy tạo phân vùng D: bằng Disk Management."));
                return list;
            }
            if (s.Style == "MBR" && s.MbrPrimary >= 4)
                list.Add(new Check(CheckLevel.Block, "Ổ cứng MBR đã đủ 4 phân vùng chính", "Không tạo thêm được phân vùng đệm — hãy dùng một ổ / phân vùng có sẵn khác C:."));
            // Sau khi cắt vẫn để C: dư ít nhất 8 GB cho Windows chạy tới lúc cài lại
            const long keepFree = 8 * GB;
            if (s.Free < needed + keepFree)
                list.Add(new Check(CheckLevel.Block, $"Ổ C: không đủ chỗ trống để tạo phân vùng đệm {needGb:0} GB",
                    $"Cần trống ~{(needed + keepFree) / (double)GB:0} GB, đang trống {s.Free / (double)GB:0.#} GB. Bớt mục sao lưu, dọn ổ C:, hoặc dùng ổ khác."));
            else if (s.MaxShrink < needed)
                list.Add(new Check(CheckLevel.Block, $"Ổ C: chỉ thu nhỏ được {s.MaxShrink / (double)GB:0.#} GB (cần {needGb:0} GB)",
                    "Có file hệ thống không di chuyển được nằm cuối ổ. " +
                    (s.Hibernation ? "Tắt ngủ đông (powercfg /h off), " : "") +
                    "xoá điểm khôi phục cũ (System Protection → Delete) rồi thử lại."));
            else
                list.Add(new Check(CheckLevel.Ok, $"Sẽ thu nhỏ C: {needGb:0} GB để tạo phân vùng đệm {Label}",
                    "Không mất dữ liệu. Cài xong, phân vùng này được gộp lại vào C: (bản sao lưu chuyển về C:\\ITH-Backup)."));
            return list;
        }

        /// <summary>Thu nhỏ C: và tạo phân vùng đệm NTFS ngay sau C:. Trả về ký tự ổ mới (vd "F:").</summary>
        public static string Create(IRunner run, ShrinkInfo s, long bytes)
        {
            if (!s.Read) throw new ReinstallException("Chưa đọc được phân vùng ổ Windows.");
            var newSize = (s.Size - bytes) / MB * MB;
            var offset = s.Offset + newSize;
            var mbr = s.Style == "MBR" ? " -MbrType IFS" : "";
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                "$ErrorActionPreference='Stop'; try { " +
                $"Resize-Partition -DiskNumber {s.Disk} -PartitionNumber {s.Partition} -Size {newSize.ToString(CultureInfo.InvariantCulture)}; " +
                $"$n=New-Partition -DiskNumber {s.Disk} -Offset {offset.ToString(CultureInfo.InvariantCulture)} -UseMaximumSize -AssignDriveLetter{mbr}; " +
                $"$v=Format-Volume -Partition $n -FileSystem NTFS -NewFileSystemLabel '{Label}' -Confirm:$false; " +
                "'LETTER=' + (Get-Partition -DiskNumber $n.DiskNumber -PartitionNumber $n.PartitionNumber).DriveLetter " +
                "} catch { 'ERROR=' + $_.Exception.Message }\"");
            string letter = "", error = "";
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("LETTER=", StringComparison.Ordinal)) letter = line.Substring(7).Trim();
                if (line.StartsWith("ERROR=", StringComparison.Ordinal)) error = line.Substring(6);
            }
            if (letter.Length != 1 || !char.IsLetter(letter[0]))
                throw new ReinstallException("Không tạo được phân vùng đệm: " + (error.Length > 0 ? error : ProcessRunner.FirstLines(r.Output, 3)));
            var drive = letter.ToUpperInvariant() + ":";
            File.WriteAllText(drive + "\\" + Tag, $"Phân vùng đệm tạm của IT Helpdesk Tool — tạo {DateTime.Now:s} bằng cách thu nhỏ ổ Windows. Sẽ tự gộp lại sau khi cài xong.");
            return drive;
        }

        /// <summary>Phân vùng đệm do app tạo ở lần trước (chạy lại thì dùng lại, không tạo thêm). Null nếu chưa có.</summary>
        public static string? FindExisting()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && IsOurs(d.Name)) return d.Name.Substring(0, 2).ToUpperInvariant();
                }
                catch { }
            }
            return null;
        }

        /// <summary>Phân vùng này do app tạo (nhãn + file đánh dấu).</summary>
        public static bool IsOurs(string drive)
        {
            try
            {
                var d = new DriveInfo(Letter(drive) + ":");
                return d.IsReady && d.VolumeLabel == Label && File.Exists(Letter(drive) + ":\\" + Tag);
            }
            catch { return false; }
        }

        /// <summary>Huỷ: xoá phân vùng đệm do app tạo và nới ổ Windows lại như cũ (mất dữ liệu trong phân vùng đệm).</summary>
        public static void Remove(IRunner run, string stagingDrive, string targetDrive)
        {
            if (!IsOurs(stagingDrive)) throw new ReinstallException($"Ổ {stagingDrive} không phải phân vùng đệm do app tạo — không xoá.");
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + MergeScript(Letter(stagingDrive), Letter(targetDrive)) + "\"");
            if (!r.Output.Contains("MERGED")) throw new ReinstallException("Không gộp lại được phân vùng đệm: " + ProcessRunner.FirstLines(r.Output, 3));
        }

        /// <summary>Lệnh PowerShell xoá phân vùng đệm và nới ổ Windows (dùng cả khi huỷ lẫn trong FirstLogon.ps1).</summary>
        public static string MergeScript(char staging, char target) =>
            "$ErrorActionPreference='Stop'; try { " +
            $"$v=Get-Volume -DriveLetter {staging}; if ($v.FileSystemLabel -ne '{Label}') {{ throw 'nhan o khong dung' }}; " +
            $"$p=Get-Partition -DriveLetter {staging}; Remove-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -Confirm:$false; " +
            $"$c=Get-Partition -DriveLetter {target}; $m=(Get-PartitionSupportedSize -DiskNumber $c.DiskNumber -PartitionNumber $c.PartitionNumber).SizeMax; " +
            "if ($m -gt $c.Size) { Resize-Partition -DiskNumber $c.DiskNumber -PartitionNumber $c.PartitionNumber -Size $m }; 'MERGED' " +
            "} catch { 'ERROR=' + $_.Exception.Message }";

        private static char Letter(string drive) => char.ToUpperInvariant(drive.Trim().TrimEnd('\\', ':')[0]);
    }
}
