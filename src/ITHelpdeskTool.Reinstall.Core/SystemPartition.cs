using System;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>
    /// Phân vùng khởi động mà Windows hiện tại đang dùng (EFI với UEFI, System Reserved / phân vùng Active với BIOS).
    /// Windows mới ghi file khởi động vào ĐÚNG phân vùng này — máy nhiều ổ cứng (ổ dữ liệu còn phân vùng EFI cũ)
    /// không bị ghi nhầm sang ổ khác, firmware khởi động y như trước.
    /// Môi trường cài tìm lại ổ cứng theo mã ổ (uniqueid disk), không theo số thứ tự (có thể đổi trong WinPE).
    /// </summary>
    public sealed class SystemPartition
    {
        public int Disk { get; set; } = -1;
        public int Partition { get; set; } = -1;
        /// <summary>Mã ổ cứng: GUID (GPT) hoặc chữ ký 8 ký tự hex (MBR), như diskpart "uniqueid disk" in ra.</summary>
        public string DiskId { get; set; } = "";
        /// <summary>Ổ cứng chứa ổ Windows.</summary>
        public int TargetDisk { get; set; } = -1;
        /// <summary>Phân vùng khởi động chính là ổ Windows (BIOS không có System Reserved) → để bcdboot tự làm.</summary>
        public bool IsTarget { get; set; }

        public bool Found => Disk >= 0 && Partition >= 0 && DiskId.Length > 0 && !IsTarget;
        public bool OnOtherDisk => Found && TargetDisk >= 0 && Disk != TargetDisk;

        public override string ToString() => Found ? $"ổ cứng {Disk}, phân vùng {Partition} (mã {DiskId})" : IsTarget ? "chính ổ Windows" : "không rõ";

        internal static string Script(string targetDrive) =>
            "$ErrorActionPreference='SilentlyContinue';" +
            $"$t=Get-Partition -DriveLetter {targetDrive.Substring(0, 1)};" +
            "$s=@(Get-Partition | ?{ $_.IsSystem });" +
            // Nhiều phân vùng khởi động (ổ dữ liệu còn EFI cũ): ưu tiên cái cùng ổ cứng với Windows
            "$p=$s | ?{ $_.DiskNumber -eq $t.DiskNumber } | Select -First 1; if(-not $p){ $p=$s | Select -First 1 };" +
            "if($p){ $d=Get-Disk -Number $p.DiskNumber; $id=if($d.PartitionStyle -eq 'GPT'){ $d.Guid } else { '{0:X8}' -f [uint32]$d.Signature };" +
            "'SYSPART=' + $p.DiskNumber + '|' + $p.PartitionNumber + '|' + $id + '|' + $t.DiskNumber + '|' + $t.PartitionNumber }";

        public static SystemPartition Probe(IRunner run, string targetDrive)
        {
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + Script(targetDrive) + "\"");
            return Parse(r.Output);
        }

        internal static SystemPartition Parse(string output)
        {
            var s = new SystemPartition();
            var m = Regex.Match(output, @"SYSPART=(\d+)\|(\d+)\|([^|\r\n]+)\|(\d*)\|(\d*)");
            if (!m.Success) return s;
            s.Disk = int.Parse(m.Groups[1].Value);
            s.Partition = int.Parse(m.Groups[2].Value);
            s.DiskId = m.Groups[3].Value.Trim().Trim('{', '}').ToUpperInvariant();
            if (int.TryParse(m.Groups[4].Value, out var td)) s.TargetDisk = td;
            s.IsTarget = int.TryParse(m.Groups[5].Value, out var tp) && td == s.Disk && tp == s.Partition;
            // Chỉ nhận mã hợp lệ (GUID hoặc 8 hex) — giá trị lạ thì để môi trường cài tự tìm như cũ
            if (!Regex.IsMatch(s.DiskId, @"^([0-9A-F]{8}|[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12})$")) s.DiskId = "";
            return s;
        }
    }
}
