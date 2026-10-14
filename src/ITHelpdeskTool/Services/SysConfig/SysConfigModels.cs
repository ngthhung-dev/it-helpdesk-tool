namespace ITHelpdeskTool.Services.SysConfig;

/// <summary>Kết quả một thao tác.</summary>
public sealed record OpResult(bool Ok, string Message, bool NeedsRestart = false)
{
    public static OpResult Success(string msg, bool restart = false) => new(true, msg, restart);
    public static OpResult Fail(string msg) => new(false, msg);

    public static OpResult From(ProcessResult r, string okMsg, string failPrefix, bool restart = false) =>
        r.Success ? Success(okMsg, restart) : Fail($"{failPrefix}: {FirstLine(r.Text)}");

    internal static string FirstLine(string s)
    {
        // Dịch vài lỗi hay gặp của Windows sang tiếng Việt
        if (s.Contains("password policy", StringComparison.OrdinalIgnoreCase) || s.Contains("password does not meet", StringComparison.OrdinalIgnoreCase))
            return "mật khẩu không đạt chính sách của máy (độ dài tối thiểu / phải có chữ hoa, số, ký tự đặc biệt). Hãy đặt mật khẩu mạnh hơn.";
        if (s.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return "tài khoản đã tồn tại.";
        if (s.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return "bị từ chối quyền truy cập (app cần chạy quyền Administrator).";
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 0 ? line : "lỗi không rõ";
    }
}

public sealed record IdentityInfo(string ComputerName, string PendingName, bool InDomain, string Domain, string Workgroup);

public sealed record TimeInfo(string TimeZoneId, string NtpServer, bool TimeServiceRunning, string Culture);

public sealed record AdapterInfo(
    string Name, string Description, string SettingId, bool Connected,
    bool Dhcp, string Ip, string Mask, string Gateway, string[] Dns, bool DnsAuto, string Mac);

public sealed record UserInfo(string Name, string FullName, bool Enabled, bool IsAdmin, bool IsBuiltinAdmin,
    bool IsCurrent, string LastLogon, string Sid, bool PasswordNeverExpires)
{
    /// <summary>Dòng mô tả phụ: họ tên · lần đăng nhập cuối.</summary>
    public string Detail => string.Join(" · ", new[]
    {
        FullName,
        LastLogon.Length > 0 ? $"đăng nhập lần cuối {LastLogon}" : "chưa đăng nhập lần nào",
    }.Where(x => x.Length > 0));
}

public sealed record SharingInfo(bool RdpEnabled, bool NlaRequired, bool FileSharing, bool NetworkDiscovery);

public sealed record SystemInfo(bool HibernateOn, bool FastStartupOn, bool RestoreOnSystemDrive, string LastRestorePoint,
    bool PagefileAuto, bool RestoreSupported);
