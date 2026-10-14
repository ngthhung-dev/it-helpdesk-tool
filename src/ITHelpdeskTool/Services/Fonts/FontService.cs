using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Fonts;

/// <summary>Thông tin một file font.</summary>
public sealed record FontFileInfo(string Path, string Family, string Face, string RegName, bool Vietnamese, bool Legacy, int Faces)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Display => Face.Length == 0 || Face is "Regular" or "Normal" ? Family : $"{Family} {Face}";

    /// <summary>FontFamily WPF đọc thẳng từ file (để xem trước, kể cả khi chưa cài).</summary>
    public FontFamily? Preview()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!.TrimEnd('\\') + "\\";
            return new FontFamily(new Uri(dir), "./" + Uri.EscapeDataString(FileName) + "#" + Family);
        }
        catch { return null; }
    }
}

public enum FontScope { Machine, User }

/// <summary>Font đã cài (đọc từ registry).</summary>
public sealed record InstalledFont(string RegName, string File, FontScope Scope, bool IsSystem, long Size)
{
    public string Name => FontService.StripType(RegName);
    public string FileName => System.IO.Path.GetFileName(File);
}

/// <summary>Cài, gỡ, sao lưu font; đọc tên font và kiểm tra hỗ trợ tiếng Việt.</summary>
public static class FontService
{
    public const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    public static readonly string[] Extensions = [".ttf", ".otf", ".ttc", ".otc", ".fon", ".fnt"];
    public static string MachineDir => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    public static string UserDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");

    /// <summary>Đủ các nguyên âm có dấu tiếng Việt (Unicode dựng sẵn).</summary>
    private const string VnChars = "ăâđêôơưĂÂĐÊÔƠƯàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ";

    public static bool IsFontFile(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    // ================= Đọc thông tin font =================
    public static FontFileInfo Read(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        if (ext is ".fon" or ".fnt")
            return new FontFileInfo(path, fileName, "", fileName, false, false, 1);

        var faces = 1;
        GlyphTypeface? g = null;
        try
        {
            if (ext is ".ttc" or ".otc")
            {
                // Bộ sưu tập: đọc mặt đầu tiên, đếm số mặt
                faces = CountFaces(path);
                g = new GlyphTypeface(new Uri(path + "#0"));
                // GlyphTypeface không đọc chỉ số mặt qua fragment ở mọi bản → thử không fragment
            }
            else g = new GlyphTypeface(new Uri(path));
        }
        catch
        {
            try { g = new GlyphTypeface(new Uri(path)); } catch { }
        }
        if (g is null) return new FontFileInfo(path, fileName, "", fileName, false, false, faces);

        string Name(IDictionary<CultureInfo, string> d) =>
            d.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var s) ? s : d.Values.FirstOrDefault() ?? "";
        var family = Name(g.Win32FamilyNames);
        var face = Name(g.Win32FaceNames);
        if (family.Length == 0) family = Name(g.FamilyNames);
        if (face.Length == 0) face = Name(g.FaceNames);

        var map = g.CharacterToGlyphMap;
        var vn = VnChars.All(c => map.ContainsKey(c));
        // Font mã cũ (TCVN3 ".VnTime", VNI "VNI-Times") gõ được tiếng Việt nhưng chỉ với bảng mã riêng
        var legacy = family.StartsWith(".Vn", StringComparison.OrdinalIgnoreCase) || family.StartsWith("VNI-", StringComparison.OrdinalIgnoreCase)
                     || family.StartsWith("VN-", StringComparison.OrdinalIgnoreCase);

        var type = ext == ".otf" && !g.Symbol ? "OpenType" : "TrueType";
        var regBase = face is "Regular" or "Normal" or "" ? family : $"{family} {face}";
        if (faces > 1) regBase = $"{family} (bộ {faces} kiểu)";
        return new FontFileInfo(path, family, face, $"{regBase} ({type})", vn, legacy, faces);
    }

    private static int CountFaces(string ttc)
    {
        try
        {
            using var fs = File.OpenRead(ttc);
            var b = new byte[12];
            if (fs.Read(b, 0, 12) < 12 || b[0] != 't' || b[1] != 't' || b[2] != 'c' || b[3] != 'f') return 1;
            return (b[8] << 24) | (b[9] << 16) | (b[10] << 8) | b[11];
        }
        catch { return 1; }
    }

    public static string StripType(string regName) =>
        System.Text.RegularExpressions.Regex.Replace(regName, @"\s*\((TrueType|OpenType|VGA res|All res|\d+[^)]*)\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ================= Font đã cài =================
    public static List<InstalledFont> ReadInstalled()
    {
        var list = new List<InstalledFont>();
        void Scan(RegistryKey root, FontScope scope)
        {
            using var k = root.OpenSubKey(FontsKey);
            if (k is null) return;
            foreach (var n in k.GetValueNames())
            {
                if (k.GetValue(n) is not string v || v.Length == 0) continue;
                var file = System.IO.Path.IsPathRooted(v) ? v : System.IO.Path.Combine(MachineDir, v);
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }
                list.Add(new InstalledFont(n, file, scope, scope == FontScope.Machine && IsSystemFile(file), size));
            }
        }
        Scan(Registry.LocalMachine, FontScope.Machine);
        Scan(Registry.CurrentUser, FontScope.User);
        return list.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Font đi kèm Windows: file thuộc sở hữu TrustedInstaller.</summary>
    public static bool IsSystemFile(string file)
    {
        try
        {
            var owner = new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            // NT SERVICE\TrustedInstaller
            return owner?.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        }
        catch { return false; }
    }

    /// <summary>Tên font (registry) đã có chưa → để báo “đã cài”.</summary>
    public static HashSet<string> InstalledNames() =>
        ReadInstalled().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> InstalledFileNames() =>
        ReadInstalled().Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ================= Cài =================
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResource(string file);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool RemoveFontResource(string file);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool MoveFileEx(string existing, string? newName, int flags);

    /// <summary>Báo cho mọi ứng dụng đang mở biết danh sách font đã đổi.</summary>
    public static void Broadcast() => SendMessageTimeout((IntPtr)0xFFFF, 0x001D /*WM_FONTCHANGE*/, IntPtr.Zero, IntPtr.Zero, 0x0002 /*ABORTIFHUNG*/, 1000, out _);

    /// <summary>Cài một file font. allUsers: vào C:\Windows\Fonts (cần admin), không thì chỉ cho tài khoản này.</summary>
    public static OpResult Install(FontFileInfo f, bool allUsers, bool broadcast = true)
    {
        try
        {
            var dir = allUsers ? MachineDir : UserDir;
            Directory.CreateDirectory(dir);
            var dest = System.IO.Path.Combine(dir, f.FileName);
            var root = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
            if (File.Exists(dest))
            {
                if (SameFile(dest, f.Path))
                {
                    // File đã có (vd cài dở / mất registry) → chỉ cần ghi lại registry
                    using var k0 = root.CreateSubKey(FontsKey, true);
                    k0.SetValue(f.RegName, allUsers ? f.FileName : dest);
                    AddFontResource(dest);
                    if (broadcast) Broadcast();
                    return OpResult.Success($"{f.Display}: đã có sẵn.");
                }
                // Trùng tên file nhưng khác font → đặt tên mới
                var stem = System.IO.Path.GetFileNameWithoutExtension(f.FileName);
                var ext = System.IO.Path.GetExtension(f.FileName);
                var i = 1;
                do dest = System.IO.Path.Combine(dir, $"{stem}_{i++}{ext}"); while (File.Exists(dest));
            }
            if (!string.Equals(System.IO.Path.GetFullPath(f.Path), dest, StringComparison.OrdinalIgnoreCase))
                File.Copy(f.Path, dest);
            using (var k = root.CreateSubKey(FontsKey, true))
                k.SetValue(f.RegName, allUsers ? System.IO.Path.GetFileName(dest) : dest);
            AddFontResource(dest);
            if (broadcast) Broadcast();
            return OpResult.Success($"Đã cài {f.Display}.");
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail($"{f.Display}: không có quyền ghi vào thư mục Fonts."); }
        catch (Exception ex) { return OpResult.Fail($"{f.Display}: {ex.Message}"); }
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
            var ha = System.Security.Cryptography.SHA256.HashData(sa);
            var hb = System.Security.Cryptography.SHA256.HashData(sb);
            return ha.SequenceEqual(hb);
        }
        catch { return false; }
    }

    // ================= Gỡ =================
    public static OpResult Uninstall(InstalledFont f)
    {
        if (f.IsSystem) return OpResult.Fail($"{f.Name} là font hệ thống của Windows — không gỡ.");
        try
        {
            RemoveFontResource(f.File);
            var root = f.Scope == FontScope.Machine ? Registry.LocalMachine : Registry.CurrentUser;
            using (var k = root.OpenSubKey(FontsKey, true)) k?.DeleteValue(f.RegName, false);
            Broadcast();
            var restart = false;
            if (File.Exists(f.File))
            {
                try { File.Delete(f.File); }
                catch
                {
                    // Font đang được ứng dụng giữ → xoá khi khởi động lại
                    MoveFileEx(f.File, null, 4 /*MOVEFILE_DELAY_UNTIL_REBOOT*/);
                    restart = true;
                }
            }
            return OpResult.Success($"Đã gỡ {f.Name}" + (restart ? " (file xoá hẳn sau khi khởi động lại)." : "."), restart);
        }
        catch (Exception ex) { return OpResult.Fail($"Không gỡ được {f.Name}: {ex.Message}"); }
    }

    // ================= Gom file từ lựa chọn =================
    /// <summary>File font từ danh sách file/thư mục/zip người dùng chọn (zip được giải nén vào thư mục tạm).</summary>
    public static List<string> Collect(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                result.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(IsFontFile));
                foreach (var z in Directory.EnumerateFiles(p, "*.zip", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    result.AddRange(FromZip(z));
            }
            else if (File.Exists(p))
            {
                if (IsFontFile(p)) result.Add(p);
                else if (System.IO.Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)) result.AddRange(FromZip(p));
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FromZip(string zip)
    {
        var dir = System.IO.Path.Combine(Downloader.CacheDir, "fonts", System.IO.Path.GetFileNameWithoutExtension(zip) + "_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            using var z = ZipFile.OpenRead(zip);
            foreach (var e in z.Entries.Where(e => IsFontFile(e.Name) && !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)))
            {
                var dest = System.IO.Path.Combine(dir, e.Name);
                Directory.CreateDirectory(dir);
                if (!File.Exists(dest)) e.ExtractToFile(dest);
            }
        }
        catch (Exception ex) { Log.Warn($"Đọc zip font {zip}: {ex.Message}"); }
        return Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
    }

    // ================= Sao lưu font cài thêm =================
    /// <summary>Chép mọi font không phải của Windows ra thư mục (để cài lại sau khi cài lại Windows).</summary>
    public static OpResult Backup(string destDir)
    {
        var n = 0;
        long bytes = 0;
        Directory.CreateDirectory(destDir);
        foreach (var f in ReadInstalled().Where(f => !f.IsSystem && File.Exists(f.File)))
        {
            try
            {
                var dest = System.IO.Path.Combine(destDir, f.FileName);
                if (!File.Exists(dest)) { File.Copy(f.File, dest); n++; bytes += f.Size; }
            }
            catch { }
        }
        return OpResult.Success($"Đã sao lưu {n} font ({Fmt.Size(bytes)}) vào {destDir}.");
    }

    // ================= Sửa lỗi font =================
    /// <summary>Xoá bộ đệm font của Windows (font hiển thị sai / ô vuông / không thấy font mới cài).</summary>
    public static async Task<OpResult> ResetCacheAsync()
    {
        await ProcessRunner.ConsoleAsync("net.exe", "stop FontCache /y");
        await ProcessRunner.ConsoleAsync("net.exe", "stop FontCache3.0.0.0 /y");
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cache = System.IO.Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\FontCache");
        var n = 0;
        try
        {
            if (Directory.Exists(cache))
                foreach (var f in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                    try { File.Delete(f); n++; } catch { }
            var fnt = System.IO.Path.Combine(win, @"System32\FNTCACHE.DAT");
            if (File.Exists(fnt)) try { File.Delete(fnt); n++; } catch { }
        }
        finally
        {
            await ProcessRunner.ConsoleAsync("net.exe", "start FontCache");
        }
        return OpResult.Success($"Đã xoá {n} file bộ đệm font. Khởi động lại máy để Windows dựng lại bộ đệm.", true);
    }
}
