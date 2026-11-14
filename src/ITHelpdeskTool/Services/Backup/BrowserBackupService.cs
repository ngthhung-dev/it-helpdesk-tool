using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Backup;

public enum BackupMode { BookmarksPasswords, Cookies }

/// <summary>Một trình duyệt có trên máy. Root là thư mục dữ liệu; chỉ các profile có dữ liệu được xử lý.</summary>
public sealed record Browser(string Key, string Name, string Glyph, string Family, string Root, string[] ProcNames)
{
    public long Size { get; init; }
    public int Profiles { get; init; }
    /// <summary>Đường dẫn đầy đủ các profile có dữ liệu (bookmark / mật khẩu / cookie).</summary>
    public string[] ProfileDirs { get; init; } = [];
    public bool Running => ProcNames.SelectMany(Process.GetProcessesByName).Any();
    public string Detail => string.Join(" · ", new[]
    {
        Family == "Firefox" ? "Firefox" : "Chromium",
        Profiles > 0 ? $"{Profiles} hồ sơ có dữ liệu" : "",
        Size > 0 ? Fmt.Size(Size) : "",
    }.Where(s => s.Length > 0));
}

/// <summary>
/// Sao lưu / xuất bookmark, mật khẩu đã lưu và cookie của mọi profile mọi trình duyệt.
/// Sao lưu và cookie là CHÉP FILE (không giải mã) — mở lại được khi khôi phục trên CÙNG máy / tài khoản Windows.
/// Xuất bookmark ra HTML do app làm; xuất mật khẩu do chính trình duyệt làm (app chỉ mở trang Export) — app không giải mã mật khẩu.
/// </summary>
public static class BrowserBackupService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static IEnumerable<Browser> Known() =>
    [
        new("chrome", "Google Chrome", "", "Chromium", Path.Combine(Local, @"Google\Chrome\User Data"), ["chrome"]),
        new("edge", "Microsoft Edge", "", "Chromium", Path.Combine(Local, @"Microsoft\Edge\User Data"), ["msedge"]),
        new("coccoc", "Cốc Cốc", "", "Chromium", Path.Combine(Local, @"CocCoc\Browser\User Data"), ["browser"]),
        new("brave", "Brave", "", "Chromium", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), ["brave"]),
        new("vivaldi", "Vivaldi", "", "Chromium", Path.Combine(Local, @"Vivaldi\User Data"), ["vivaldi"]),
        new("chromium", "Chromium", "", "Chromium", Path.Combine(Local, @"Chromium\User Data"), ["chrome"]),
        new("opera", "Opera", "", "Opera", Path.Combine(Roaming, @"Opera Software\Opera Stable"), ["opera"]),
        new("operagx", "Opera GX", "", "Opera", Path.Combine(Roaming, @"Opera Software\Opera GX Stable"), ["opera"]),
        new("firefox", "Mozilla Firefox", "", "Firefox", Path.Combine(Roaming, @"Mozilla\Firefox"), ["firefox"]),
    ];

    // Tên file dữ liệu theo từng nhóm (ở gốc mỗi profile). Chromium đời mới để Cookies trong thư mục con Network.
    private static string[] ProfileFiles(string family, BackupMode mode) => (family, mode) switch
    {
        ("Firefox", BackupMode.BookmarksPasswords) => ["places.sqlite", "favicons.sqlite", "logins.json", "key4.db", "key3.db"],
        ("Firefox", BackupMode.Cookies) => ["cookies.sqlite"],
        (_, BackupMode.BookmarksPasswords) => ["Bookmarks", "Bookmarks.bak", "Login Data", "Login Data For Account"],
        (_, BackupMode.Cookies) => ["Cookies", "Cookies-journal", @"Network\Cookies", @"Network\Cookies-journal"],
    };

    // File dùng chung của cả trình duyệt (gồm khóa giải mã mật khẩu / cookie) — luôn kèm theo khi sao lưu.
    private static string[] SharedFiles(string family) => family switch
    {
        "Firefox" => ["profiles.ini", "installs.ini"],
        "Opera" => ["Local State"],
        _ => ["Local State", "First Run"],
    };

    public static async Task<List<Browser>> DetectAsync() => await Task.Run(() =>
        Known().Where(b => Directory.Exists(b.Root)).Select(WithProfiles).Where(b => b.Profiles > 0).ToList());

    private static Browser WithProfiles(Browser b)
    {
        var kept = AllProfileDirs(b).Where(d => HasData(b.Family, d)).ToList();
        long size = 0;
        foreach (var d in kept)
            foreach (var mode in new[] { BackupMode.BookmarksPasswords, BackupMode.Cookies })
                foreach (var f in ProfileFiles(b.Family, mode))
                    if (File.Exists(Path.Combine(d, f))) size += new FileInfo(Path.Combine(d, f)).Length;
        return b with { Profiles = kept.Count, ProfileDirs = kept.ToArray(), Size = size };
    }

    private static List<string> AllProfileDirs(Browser b)
    {
        try
        {
            return b.Family switch
            {
                "Opera" => [b.Root],
                "Firefox" => Directory.Exists(Path.Combine(b.Root, "Profiles")) ? Directory.GetDirectories(Path.Combine(b.Root, "Profiles")).ToList() : [],
                _ => Directory.GetDirectories(b.Root)
                        .Where(d => File.Exists(Path.Combine(d, "Preferences")) &&
                                    Path.GetFileName(d) is not ("System Profile" or "Guest Profile")).ToList(),
            };
        }
        catch { return []; }
    }

    /// <summary>Profile có bookmark, mật khẩu hoặc cookie không (chỉ đọc dấu hiệu, không giải mã).</summary>
    private static bool HasData(string family, string dir)
    {
        if (family == "Firefox")
            return File.Exists(Path.Combine(dir, "places.sqlite")) || FileHasAny(Path.Combine(dir, "logins.json"), "encryptedUsername")
                   || File.Exists(Path.Combine(dir, "cookies.sqlite"));
        if (FileHasAny(Path.Combine(dir, "Bookmarks"), "\"url\"")) return true;
        foreach (var f in new[] { "Login Data", "Login Data For Account" })
            if (FileHasAny(Path.Combine(dir, f), "v10", "v11", "v20")) return true;
        return File.Exists(Path.Combine(dir, "Cookies")) || File.Exists(Path.Combine(dir, @"Network\Cookies"));
    }

    private static bool FileHasAny(string path, params string[] needles)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(fs.Length, 8 * 1024 * 1024)];
            var n = fs.Read(buf, 0, buf.Length);
            var text = Encoding.Latin1.GetString(buf, 0, n);
            return needles.Any(s => text.Contains(s, StringComparison.Ordinal));
        }
        catch { return true; }
    }

    public static List<Process> RunningProcs(Browser b) => b.ProcNames.SelectMany(Process.GetProcessesByName).ToList();

    public static async Task CloseAsync(Browser b)
    {
        foreach (var p in RunningProcs(b)) try { p.CloseMainWindow(); } catch { }
        for (var i = 0; i < 30 && RunningProcs(b).Count > 0; i++) await Task.Delay(500).ConfigureAwait(false);
        foreach (var p in RunningProcs(b)) try { p.Kill(true); } catch { }
        await Task.Delay(800).ConfigureAwait(false);
    }

    public static string NewSetDir(string root, string prefix) => Path.Combine(root, $"{prefix}-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}");

    private static string ProfileLabel(Browser b, string dir) =>
        b.Family == "Firefox" || b.Family == "Opera" ? Path.GetFileName(dir) : Path.GetFileName(dir);   // Default, Profile 1, <id>.default…

    private static void CopyIfExists(string src, string dst)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>Sao lưu bookmark+mật khẩu HOẶC cookie của một trình duyệt (đóng trình duyệt trước). Chỉ chép đúng các file liên quan.</summary>
    public static async Task<OpResult> BackupAsync(Browser b, BackupMode mode, string setDir, Action<string> status, CancellationToken ct)
    {
        if (RunningProcs(b).Count > 0) return OpResult.Fail($"{b.Name} đang mở — đóng {b.Name} trước khi sao lưu.");
        var dstRoot = Path.Combine(setDir, b.Key);
        var files = ProfileFiles(b.Family, mode);
        var copied = 0;
        long bytes = 0;
        await Task.Run(() =>
        {
            foreach (var f in SharedFiles(b.Family))
            {
                var s = Path.Combine(b.Root, f);
                if (File.Exists(s)) { CopyIfExists(s, Path.Combine(dstRoot, f)); }
            }
            foreach (var dir in b.ProfileDirs)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(b.Root, dir);
                status($"Đang chép {b.Name} · {ProfileLabel(b, dir)}…");
                foreach (var f in files)
                {
                    var s = Path.Combine(dir, f);
                    if (File.Exists(s)) { CopyIfExists(s, Path.Combine(dstRoot, rel, f)); copied++; bytes += new FileInfo(s).Length; }
                }
            }
        }, ct).ConfigureAwait(false);
        if (copied == 0) return OpResult.Fail($"{b.Name}: không có dữ liệu {(mode == BackupMode.Cookies ? "cookie" : "bookmark / mật khẩu")} để sao lưu.");
        return OpResult.Success($"{b.Name}: đã sao lưu {copied} file ({Fmt.Size(bytes)}).");
    }

    /// <summary>Khôi phục từ thư mục sao lưu: chép các file đã lưu về đúng chỗ (đóng trình duyệt trước). Chỉ ghi đè, không xoá.</summary>
    public static async Task<OpResult> RestoreAsync(Browser b, string backupDir, Action<string> status, CancellationToken ct)
    {
        var src = Path.Combine(backupDir, b.Key);
        if (!Directory.Exists(src)) return OpResult.Fail($"Thư mục sao lưu không có dữ liệu {b.Name}.");
        if (RunningProcs(b).Count > 0) return OpResult.Fail($"{b.Name} đang mở — đóng {b.Name} trước khi khôi phục.");
        var n = 0;
        await Task.Run(() =>
        {
            foreach (var s in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(src, s);
                status($"Đang khôi phục {b.Name} · {rel}…");
                CopyIfExists(s, Path.Combine(b.Root, rel));
                n++;
            }
        }, ct).ConfigureAwait(false);
        return OpResult.Success($"{b.Name}: đã khôi phục {n} file. Mở {b.Name} kiểm tra lại.");
    }

    public static List<Browser> InBackup(string backupDir)
    {
        var known = Known().ToList();
        var list = new List<Browser>();
        try
        {
            foreach (var d in Directory.GetDirectories(backupDir))
            {
                var b = known.FirstOrDefault(x => x.Key.Equals(Path.GetFileName(d), StringComparison.OrdinalIgnoreCase));
                if (b is not null) list.Add(b);
            }
        }
        catch { }
        return list;
    }

    // ================= Xuất bookmark ra HTML =================
    /// <summary>Xuất bookmark các profile của trình duyệt ra file HTML (Chromium/Opera). Firefox không đọc được bằng chép file → trả lỗi nhẹ.</summary>
    public static async Task<OpResult> ExportBookmarksAsync(Browser b, string destDir)
    {
        if (b.Family == "Firefox")
            return OpResult.Fail($"{b.Name}: xuất bookmark bằng chính Firefox (menu Bookmarks → Manage → Import and Backup → Export).");
        var files = 0;
        await Task.Run(() =>
        {
            foreach (var dir in b.ProfileDirs)
            {
                var bm = Path.Combine(dir, "Bookmarks");
                if (!File.Exists(bm)) continue;
                try
                {
                    var html = BookmarksToHtml(File.ReadAllText(bm));
                    if (html is null) continue;
                    Directory.CreateDirectory(destDir);
                    var name = $"{b.Key}-{ProfileLabel(b, dir)}.html".Replace(' ', '_');
                    File.WriteAllText(Path.Combine(destDir, name), html, new UTF8Encoding(false));
                    files++;
                }
                catch { }
            }
        });
        return files > 0 ? OpResult.Success($"{b.Name}: đã xuất {files} file bookmark HTML.")
                         : OpResult.Fail($"{b.Name}: không có bookmark để xuất.");
    }

    private static string? BookmarksToHtml(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return null;
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE><H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        foreach (var name in new[] { "bookmark_bar", "other", "synced" })
            if (roots.TryGetProperty(name, out var node)) WriteNode(node, sb, 1);
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    private static void WriteNode(JsonElement node, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 4);
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : "";
        var title = System.Net.WebUtility.HtmlEncode(node.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "");
        if (type == "url")
        {
            var url = System.Net.WebUtility.HtmlEncode(node.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "");
            sb.AppendLine($"{pad}<DT><A HREF=\"{url}\">{title}</A>");
        }
        else if (type == "folder")
        {
            sb.AppendLine($"{pad}<DT><H3>{title}</H3>");
            sb.AppendLine($"{pad}<DL><p>");
            if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) WriteNode(c, sb, depth + 1);
            sb.AppendLine($"{pad}</DL><p>");
        }
    }

    public static void WriteManifest(string setDir, string title, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine($"Máy: {Environment.MachineName} · Người sao lưu: {Environment.UserName} · Lúc: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Khôi phục: mở IT Helpdesk Tool trên máy này → Sao lưu trình duyệt → Khôi phục, chọn thư mục này.");
        sb.AppendLine("- Khôi phục trên CÙNG máy / Windows: mật khẩu, cookie mở lại được.");
        sb.AppendLine("- Cài lại WINDOWS / máy khác: bookmark về được; mật khẩu và cookie có thể KHÔNG mở được (khóa mã hóa đã khác).");
        sb.AppendLine("Bản sao lưu chứa dữ liệu đăng nhập — giữ ở nơi an toàn, đừng để lẫn.");
        sb.AppendLine(new string('=', 78));
        foreach (var l in lines) sb.AppendLine(l);
        try { File.WriteAllText(Path.Combine(setDir, "DOC-TRUOC-KHI-KHOI-PHUC.txt"), sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // ================= Mở trang Export mật khẩu của trình duyệt =================
    private static string? ExeOf(Browser b)
    {
        string[] cands = b.Key switch
        {
            "chrome" => [@"Google\Chrome\Application\chrome.exe"],
            "edge" => [@"Microsoft\Edge\Application\msedge.exe"],
            "coccoc" => [@"CocCoc\Browser\Application\browser.exe"],
            "brave" => [@"BraveSoftware\Brave-Browser\Application\brave.exe"],
            "vivaldi" => [@"Vivaldi\Application\vivaldi.exe"],
            "opera" => [@"..\..\Local\Programs\Opera\opera.exe"],
            "operagx" => [@"..\..\Local\Programs\Opera GX\opera.exe"],
            "firefox" => [@"Mozilla Firefox\firefox.exe"],
            _ => [],
        };
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Local })
            foreach (var c in cands)
            {
                var p = Path.GetFullPath(Path.Combine(root, c));
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Mở trang quản lý mật khẩu của trình duyệt để người dùng bấm Export (trình duyệt tự giải mã, app không đụng).</summary>
    public static OpResult OpenPasswordPage(Browser b)
    {
        var exe = ExeOf(b);
        if (exe is null) return OpResult.Fail($"Không tìm thấy file chạy của {b.Name}.");
        var url = b.Family == "Firefox" ? "about:logins" :
                  b.Key is "edge" ? "edge://settings/passwords" : "chrome://password-manager/passwords";
        try { Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = false }); return OpResult.Success($"Đã mở trang mật khẩu {b.Name} — bấm Export để xuất (trình duyệt sẽ hỏi xác thực Windows)."); }
        catch (Exception ex) { return OpResult.Fail($"Không mở được: {ex.Message}"); }
    }
}
