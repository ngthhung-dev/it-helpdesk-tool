using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Backup;

/// <summary>
/// Lấy icon THẬT của trình duyệt từ file .exe đã cài trên máy (không nhúng logo thương hiệu vào app).
/// Trả về null nếu không tìm thấy file exe — khi đó UI hiển thị icon mặc định.
/// </summary>
public static class BrowserIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon của trình duyệt theo Key (chrome, edge, coccoc…). Có cache; an toàn gọi từ UI.</summary>
    public static ImageSource? For(string key)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            var exe = ExePath(key);
            if (exe is not null) img = FromExe(exe);
        }
        catch { img = null; }
        img?.Freeze();
        _cache[key] = img;
        return img;
    }

    private static ImageSource? FromExe(string exe)
    {
        var large = new nint[1];
        if (ExtractIconEx(exe, 0, large, null, 1) == 0 || large[0] == 0) return null;
        try { return Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
        catch { return null; }
        finally { DestroyIcon(large[0]); }
    }

    private static string? ExePath(string key)
    {
        foreach (var p in Candidates(key))
            if (File.Exists(p)) return p;
        // Dự phòng: tra registry App Paths của file exe tương ứng.
        var app = AppPaths(ExeName(key));
        if (app is not null) return app;
        // Opera cài theo thư mục phiên bản con — quét tìm.
        return OperaScan(key);
    }

    private static string PF => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string PFx86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string Local => ActiveUser.Folder(Environment.SpecialFolder.LocalApplicationData);

    private static string ExeName(string key) => key switch
    {
        "edge" => "msedge.exe",
        "coccoc" => "browser.exe",
        "chromium" => "chrome.exe",
        "operagx" => "opera.exe",
        _ => key + ".exe",
    };

    private static IEnumerable<string> Candidates(string key) => key switch
    {
        "chrome" => [Path.Combine(PF, @"Google\Chrome\Application\chrome.exe"), Path.Combine(PFx86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(Local, @"Google\Chrome\Application\chrome.exe")],
        "edge" => [Path.Combine(PFx86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(PF, @"Microsoft\Edge\Application\msedge.exe")],
        "coccoc" => [Path.Combine(Local, @"CocCoc\Browser\Application\browser.exe"), Path.Combine(PFx86, @"CocCoc\Browser\Application\browser.exe"), Path.Combine(PF, @"CocCoc\Browser\Application\browser.exe")],
        "brave" => [Path.Combine(PF, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(PFx86, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(Local, @"BraveSoftware\Brave-Browser\Application\brave.exe")],
        "vivaldi" => [Path.Combine(Local, @"Vivaldi\Application\vivaldi.exe"), Path.Combine(PF, @"Vivaldi\Application\vivaldi.exe"), Path.Combine(PFx86, @"Vivaldi\Application\vivaldi.exe")],
        "chromium" => [Path.Combine(Local, @"Chromium\Application\chrome.exe"), Path.Combine(PF, @"Chromium\Application\chrome.exe")],
        "opera" => [Path.Combine(Local, @"Programs\Opera\opera.exe"), Path.Combine(Local, @"Programs\Opera\launcher.exe"), Path.Combine(PF, @"Opera\opera.exe")],
        "operagx" => [Path.Combine(Local, @"Programs\Opera GX\opera.exe"), Path.Combine(Local, @"Programs\Opera GX\launcher.exe")],
        "firefox" => [Path.Combine(PF, @"Mozilla Firefox\firefox.exe"), Path.Combine(PFx86, @"Mozilla Firefox\firefox.exe")],
        _ => [],
    };

    private static string? AppPaths(string exeName)
    {
        foreach (var root in new[] { ActiveUser.Registry, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                if (k?.GetValue(null) is string s)
                {
                    var p = s.Trim('"');
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* bỏ qua */ }
        }
        return null;
    }

    private static string? OperaScan(string key)
    {
        if (key is not ("opera" or "operagx")) return null;
        var dir = Path.Combine(Local, "Programs", key == "operagx" ? "Opera GX" : "Opera");
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (var name in new[] { "opera.exe", "launcher.exe" })
            {
                var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }
        catch { /* bỏ qua */ }
        return null;
    }
}
