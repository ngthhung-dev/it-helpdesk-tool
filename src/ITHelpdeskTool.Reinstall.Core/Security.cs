using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Tình trạng BitLocker / mã hoá thiết bị của một ổ.</summary>
    public sealed class BitLockerVolume
    {
        public string Drive { get; set; } = "";
        /// <summary>ProtectionStatus = 1 (đang bảo vệ).</summary>
        public bool Protected { get; set; }
        /// <summary>0 đã giải mã hết, 1 đã mã hoá hết, 2 đang mã hoá, 3 đang giải mã, 4 / 5 tạm dừng.</summary>
        public int Conversion { get; set; }
        public double Percent { get; set; }
        /// <summary>0 = ổ Windows, 1 = ổ dữ liệu cố định, 2 = ổ rời; -1 = không rõ (Win 7).</summary>
        public int VolumeType { get; set; } = -1;
        public bool Locked { get; set; }

        /// <summary>Còn dữ liệu bị mã hoá (kể cả khi BitLocker đang tạm dừng bảo vệ).</summary>
        public bool Encrypted => Conversion != 0 || Percent > 0 || Protected;
        public bool Decrypting => Conversion == 3 || Conversion == 5;

        public override string ToString() =>
            $"{Drive} {(Encrypted ? $"mã hoá {Percent:0}%" : "không mã hoá")}{(Decrypting ? " (đang giải mã)" : "")}{(Locked ? " (đang khoá)" : "")}";
    }

    /// <summary>Khoá khôi phục BitLocker (48 chữ số) của một ổ.</summary>
    public sealed class RecoveryKey
    {
        public string Drive { get; set; } = "";
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
    }

    /// <summary>Tắt mã hoá, đọc tiến độ, lấy khoá khôi phục — dùng manage-bde / WMI có sẵn trong Windows.</summary>
    public static class Encryption
    {
        /// <summary>Giải mã ổ (thường là C:) — chạy nền trong Windows, có thể mất 10–60 phút.</summary>
        public static RunResult TurnOff(IRunner run, string drive) => run.Run("manage-bde.exe", $"-off {Drive(drive)}");

        /// <summary>Đọc lại tình trạng một ổ (để hiện % giải mã).</summary>
        public static BitLockerVolume? Status(IRunner run, string drive)
        {
            var r = run.Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + MachineFacts.BitLockerScript + "\"");
            return MachineFacts.ParseBitLocker(r.Output).FirstOrDefault(v => v.Drive.Equals(Drive(drive), StringComparison.OrdinalIgnoreCase));
        }

        private static readonly Regex KeyRx = new Regex(@"\b\d{6}(-\d{6}){7}\b");
        private static readonly Regex IdRx = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");

        /// <summary>Khoá khôi phục dạng 48 số của ổ (ổ phải đang mở khoá). Không có thì trả danh sách rỗng.</summary>
        public static List<RecoveryKey> RecoveryKeys(IRunner run, string drive)
        {
            var r = run.Run("manage-bde.exe", $"-protectors -get {Drive(drive)} -Type RecoveryPassword");
            return ParseKeys(r.Output, Drive(drive));
        }

        /// <summary>Output của manage-bde (mọi ngôn ngữ): mỗi khoá có một ID {GUID} rồi tới dãy 48 số.</summary>
        public static List<RecoveryKey> ParseKeys(string output, string drive)
        {
            var list = new List<RecoveryKey>();
            var lastId = "";
            foreach (var line in output.Split('\n'))
            {
                var id = IdRx.Match(line);
                if (id.Success) lastId = id.Value;
                var k = KeyRx.Match(line);
                if (k.Success) list.Add(new RecoveryKey { Drive = drive, Id = lastId, Key = k.Value });
            }
            return list;
        }

        public static string KeysText(IEnumerable<RecoveryKey> keys)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"KHOÁ KHÔI PHỤC BITLOCKER — lưu lúc {DateTime.Now:dd/MM/yyyy HH:mm} trước khi cài lại Windows");
            sb.AppendLine("Sau khi cài lại, Windows mới cần khoá này để mở các ổ dưới đây. Mất khoá = mất dữ liệu ổ đó.");
            sb.AppendLine("Chép ra giấy hoặc lưu ở nơi khác ngoài máy này.");
            sb.AppendLine();
            foreach (var k in keys)
            {
                sb.AppendLine($"Ổ {k.Drive}   ID khoá: {k.Id}");
                sb.AppendLine($"   Khoá khôi phục: {k.Key}");
            }
            sb.AppendLine();
            sb.AppendLine("Mở ổ trên Windows mới: bấm đúp vào ổ → More options → Enter recovery key. Sau đó có thể bật lại Auto-unlock.");
            return sb.ToString();
        }

        private static string Drive(string d) => d.Trim().TrimEnd('\\').ToUpperInvariant();
    }

    /// <summary>Tài khoản đang dùng và máy có thuộc quản lý của tổ chức không.</summary>
    public sealed class AccountFacts
    {
        /// <summary>MicrosoftAccount, Local, ActiveDirectory, AzureAD; rỗng = không rõ (Windows 7).</summary>
        public string Source { get; set; } = "";
        public bool AzureAdJoined { get; set; }
        public bool WorkplaceJoined { get; set; }
        public bool DomainJoined { get; set; }
        public string Domain { get; set; } = "";
        /// <summary>Đã đăng ký quản lý thiết bị (Intune / MDM khác).</summary>
        public bool MdmEnrolled { get; set; }

        public bool IsMicrosoftAccount => Source.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase);
        public bool ManagedByOrganization => AzureAdJoined || DomainJoined || MdmEnrolled;

        public string OrganizationText => string.Join(", ", new[]
        {
            AzureAdJoined ? "Microsoft Entra ID (Azure AD)" : "",
            MdmEnrolled ? "Intune / MDM" : "",
            DomainJoined ? $"domain {Domain}" : "",
        }.Where(s => s.Length > 0));
    }
}
