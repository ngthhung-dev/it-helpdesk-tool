using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ITHelpdeskTool.Services;

/// <summary>
/// Người đang ngồi máy — tài khoản đăng nhập phiên Windows mà app đang hiển thị, KHÔNG phải tài khoản chạy app.
/// Khi người dùng thường (vd "user") mở app và gõ mật khẩu một tài khoản admin khác ở hộp thoại UAC, app chạy dưới tài khoản admin đó:
/// HKCU, %APPDATA%, Desktop… lúc này là của admin. Mọi thông tin / cài đặt "của người dùng" phải đọc ghi qua lớp này
/// (HKEY_USERS\SID và thư mục hồ sơ của người đang đăng nhập). Dữ liệu riêng của app (log, đăng nhập, cache) vẫn để ở tài khoản chạy app.
/// </summary>
public static class ActiveUser
{
    /// <summary>SID người đang đăng nhập phiên này.</summary>
    public static string Sid { get; }
    /// <summary>Tên đăng nhập (không kèm domain).</summary>
    public static string Name { get; }
    /// <summary>DOMAIN\tên.</summary>
    public static string Account { get; }
    /// <summary>Thư mục hồ sơ, vd C:\Users\user.</summary>
    public static string ProfilePath { get; }
    /// <summary>Người đang đăng nhập khác tài khoản chạy app (đã đổi sang tài khoản admin khi UAC hỏi).</summary>
    public static bool IsOther { get; }

    /// <summary>Hive registry của người đang đăng nhập (thay cho Registry.CurrentUser). Không Dispose.</summary>
    public static RegistryKey Registry { get; }

    /// <summary>Tên gốc dạng reg.exe: HKEY_CURRENT_USER hoặc HKEY_USERS\SID.</summary>
    public static string RegRoot => IsOther ? $@"HKEY_USERS\{Sid}" : "HKEY_CURRENT_USER";

    /// <summary>Gốc dùng trong PowerShell: HKCU: hoặc Registry::HKEY_USERS\SID.</summary>
    public static string PsRoot => IsOther ? $@"Registry::HKEY_USERS\{Sid}" : "HKCU:";

    static ActiveUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        var mySid = me.User?.Value ?? "";
        var myName = me.Name;
        Sid = mySid;
        Account = myName;
        Registry = Microsoft.Win32.Registry.CurrentUser;
        try
        {
            var (domain, user) = SessionUser();
            if (user.Length > 0)
            {
                var acc = domain.Length > 0 ? $@"{domain}\{user}" : user;
                var sid = ((SecurityIdentifier)new NTAccount(acc).Translate(typeof(SecurityIdentifier))).Value;
                if (!sid.Equals(mySid, StringComparison.OrdinalIgnoreCase))
                {
                    // Hive của người đang đăng nhập luôn được nạp sẵn ở HKEY_USERS\SID
                    var hive = Microsoft.Win32.Registry.Users.OpenSubKey(sid, writable: true);
                    if (hive is not null)
                    {
                        Sid = sid;
                        Account = acc;
                        Registry = hive;
                        IsOther = true;
                    }
                    else Log.Warn($"Người đăng nhập {acc} ({sid}) chưa có hive trong HKEY_USERS — dùng tài khoản chạy app");
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Không xác định được người đang đăng nhập: {ex.Message}"); }

        Name = Account.Contains('\\') ? Account[(Account.LastIndexOf('\\') + 1)..] : Account;
        ProfilePath = IsOther ? ReadProfilePath(Sid) ?? Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users", Name)
                              : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsOther) Log.Info($"App chạy bằng {myName}, người đang đăng nhập: {Account} — đọc/ghi thông tin người dùng theo {Account}");
    }

    // ================= Thư mục =================
    /// <summary>Tên giá trị trong "User Shell Folders" ứng với từng thư mục.</summary>
    private static string? ShellFolderName(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.ApplicationData => "AppData",
        Environment.SpecialFolder.LocalApplicationData => "Local AppData",
        Environment.SpecialFolder.Desktop or Environment.SpecialFolder.DesktopDirectory => "Desktop",
        Environment.SpecialFolder.MyDocuments => "Personal",
        Environment.SpecialFolder.MyPictures => "My Pictures",
        Environment.SpecialFolder.MyMusic => "My Music",
        Environment.SpecialFolder.MyVideos => "My Video",
        Environment.SpecialFolder.Favorites => "Favorites",
        Environment.SpecialFolder.StartMenu => "Start Menu",
        Environment.SpecialFolder.Programs => "Programs",
        Environment.SpecialFolder.Startup => "Startup",
        Environment.SpecialFolder.Recent => "Recent",
        Environment.SpecialFolder.SendTo => "SendTo",
        Environment.SpecialFolder.Templates => "Templates",
        Environment.SpecialFolder.InternetCache => "Cache",
        Environment.SpecialFolder.Cookies => "Cookies",
        Environment.SpecialFolder.History => "History",
        _ => null,
    };

    private static string DefaultRelative(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.ApplicationData => @"AppData\Roaming",
        Environment.SpecialFolder.LocalApplicationData => @"AppData\Local",
        Environment.SpecialFolder.Desktop or Environment.SpecialFolder.DesktopDirectory => "Desktop",
        Environment.SpecialFolder.MyDocuments => "Documents",
        Environment.SpecialFolder.MyPictures => "Pictures",
        Environment.SpecialFolder.MyMusic => "Music",
        Environment.SpecialFolder.MyVideos => "Videos",
        Environment.SpecialFolder.Favorites => "Favorites",
        Environment.SpecialFolder.StartMenu => @"AppData\Roaming\Microsoft\Windows\Start Menu",
        Environment.SpecialFolder.Programs => @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
        Environment.SpecialFolder.Startup => @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
        Environment.SpecialFolder.Recent => @"AppData\Roaming\Microsoft\Windows\Recent",
        Environment.SpecialFolder.SendTo => @"AppData\Roaming\Microsoft\Windows\SendTo",
        Environment.SpecialFolder.Templates => @"AppData\Roaming\Microsoft\Windows\Templates",
        Environment.SpecialFolder.InternetCache => @"AppData\Local\Microsoft\Windows\INetCache",
        Environment.SpecialFolder.Cookies => @"AppData\Local\Microsoft\Windows\INetCookies",
        Environment.SpecialFolder.History => @"AppData\Local\Microsoft\Windows\History",
        _ => "",
    };

    /// <summary>Thư mục của người đang đăng nhập (thay cho Environment.GetFolderPath với các thư mục cá nhân).</summary>
    public static string Folder(Environment.SpecialFolder f)
    {
        if (!IsOther) return Environment.GetFolderPath(f);
        if (f == Environment.SpecialFolder.UserProfile) return ProfilePath;
        if (ShellFolderName(f) is not { } name) return Environment.GetFolderPath(f);   // thư mục chung của máy
        return ShellFolder(name) ?? Path.Combine(ProfilePath, DefaultRelative(f));
    }

    /// <summary>Thư mục Downloads của người đang đăng nhập.</summary>
    public static string Downloads =>
        ShellFolder("{374DE290-123F-4565-9164-39C4925E467B}") ?? Path.Combine(ProfilePath, "Downloads");

    /// <summary>Thư mục TEMP của người đang đăng nhập.</summary>
    public static string TempPath => IsOther ? Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Temp") : Path.GetTempPath();

    /// <summary>Đọc "User Shell Folders" trong hive của người dùng, thay %USERPROFILE% bằng hồ sơ của họ.</summary>
    private static string? ShellFolder(string valueName)
    {
        try
        {
            using var k = Registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (k?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw || raw.Length == 0) return null;
            var local = Path.Combine(ProfilePath, @"AppData\Local");
            var roaming = Path.Combine(ProfilePath, @"AppData\Roaming");
            var s = raw.Replace("%USERPROFILE%", ProfilePath, StringComparison.OrdinalIgnoreCase)
                       .Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase)
                       .Replace("%APPDATA%", roaming, StringComparison.OrdinalIgnoreCase)
                       .Replace("%USERNAME%", Name, StringComparison.OrdinalIgnoreCase);
            return Environment.ExpandEnvironmentVariables(s);
        }
        catch { return null; }
    }

    private static string? ReadProfilePath(string sid)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return k?.GetValue("ProfileImagePath") is string p && p.Length > 0 ? Environment.ExpandEnvironmentVariables(p) : null;
    }

    // ================= Token =================
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008, TOKEN_IMPERSONATE = 0x0004, TOKEN_DUPLICATE = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    /// <summary>
    /// Token của người đang đăng nhập (mượn từ explorer.exe của họ trong phiên này), dùng cho API cần "người dùng nào"
    /// như SHGetKnownFolderPath. Null khi app chạy đúng tài khoản người dùng (dùng token mặc định) hoặc không lấy được.
    /// </summary>
    public static SafeAccessTokenHandle? OpenToken()
    {
        if (!IsOther) return null;
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                if (p.SessionId != session) continue;
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!OpenProcessToken(h, TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, out var tok)) continue;
                    using (var id = new WindowsIdentity(tok.DangerousGetHandle()))
                        if (id.User?.Value.Equals(Sid, StringComparison.OrdinalIgnoreCase) == true) return tok;
                    tok.Dispose();
                }
                catch { }
                finally { CloseHandle(h); }
            }
        }
        Log.Warn($"Không lấy được token của {Account} từ explorer.exe");
        return null;
    }

    // ================= Người đăng nhập phiên =================
    private const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string Query(int session, int info)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, info, out var buf, out _)) return "";
        try { return Marshal.PtrToStringUni(buf) ?? ""; }
        finally { WTSFreeMemory(buf); }
    }

    /// <summary>Tài khoản đăng nhập phiên chứa tiến trình này (khác tài khoản chạy tiến trình khi nâng quyền bằng tài khoản khác).</summary>
    private static (string Domain, string User) SessionUser()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return (Query(session, WTSDomainName), Query(session, WTSUserName));
    }
}
