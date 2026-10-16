using System.IO;
using System.Runtime.InteropServices;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Disk;

/// <summary>
/// Thư mục có thể chuyển. Known Folder (Desktop, Documents…) đổi vị trí bằng API của Windows.
/// Dữ liệu ứng dụng (<see cref="AppPath"/> khác null) — Zalo, CapCut, Canva… — được chuyển rồi để lại một junction ở chỗ cũ,
/// ứng dụng vẫn đọc / ghi đúng đường dẫn cũ.
/// </summary>
public sealed record UserFolder(string Key, string Name, Guid Id, string DefaultName, string Glyph,
    Func<string>? AppPath = null, string[]? Processes = null, string Note = "")
{
    public bool IsApp => AppPath is not null;
}

/// <summary>Chuyển thư mục người dùng (Desktop, Documents, Downloads...) sang ổ khác bằng API Known Folder của Windows.</summary>
public static class UserFolderService
{
    public static readonly IReadOnlyList<UserFolder> Folders =
    [
        new("desktop", "Desktop", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), "Desktop", ""),
        new("documents", "Documents", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), "Documents", ""),
        new("downloads", "Downloads", new Guid("374DE290-123F-4565-9164-39C4925E467B"), "Downloads", ""),
        new("pictures", "Pictures", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"), "Pictures", ""),
        new("videos", "Videos", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), "Videos", ""),
        new("music", "Music", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"), "Music", ""),
    ];

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Dữ liệu ứng dụng hay phình to trên ổ C:.</summary>
    public static readonly IReadOnlyList<UserFolder> AppFolders =
    [
        new("app.capcut", "CapCut", Guid.Empty, "CapCut", "\uE714", () => Path.Combine(Local, "CapCut", "User Data"), ["CapCut", "VEDetector"],
            "Dự án, bộ nhớ đệm, hiệu ứng tải về"),
        new("app.canva", "Canva", Guid.Empty, "Canva", "\uE790", () => Path.Combine(Roaming, "Canva"), ["Canva"],
            "Bộ nhớ đệm, phông, dữ liệu đăng nhập của ứng dụng Canva"),
        new("app.zalo", "Zalo", Guid.Empty, "ZaloData", "\uE8BD", () => Path.Combine(Roaming, "ZaloData"), ["Zalo", "ZaloCall"],
            "Tin nhắn, ảnh, file đã nhận của Zalo PC (Zalo chính)"),
    ];

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll")] private static extern int SHSetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static string GetPath(UserFolder f)
    {
        if (f.IsApp)
        {
            var orig = f.AppPath!();
            return LinkTarget(orig) ?? orig;
        }
        const uint KF_FLAG_DONT_VERIFY = 0x4000;
        if (SHGetKnownFolderPath(f.Id, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var p) != 0) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string DefaultPath(UserFolder f) =>
        f.IsApp ? f.AppPath!() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), f.DefaultName);

    public static bool IsDefault(UserFolder f) => f.IsApp ? LinkTarget(f.AppPath!()) is null : GetPath(f).TrimEnd('\\').Equals(DefaultPath(f), StringComparison.OrdinalIgnoreCase);

    /// <summary>Đích của junction / symlink, null nếu là thư mục thường hoặc không có.</summary>
    public static string? LinkTarget(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists || !di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var t = di.LinkTarget;
            if (t is null) return null;
            if (t.StartsWith(@"\??\", StringComparison.Ordinal)) t = t[4..];
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dir)!, t)).TrimEnd('\\');
        }
        catch { return null; }
    }

    /// <summary>Dữ liệu của ứng dụng có trên máy chưa (đã cài và mở ít nhất một lần).</summary>
    public static bool AppDataExists(UserFolder f) => f.IsApp && Directory.Exists(f.AppPath!());

    public static List<System.Diagnostics.Process> RunningApps(UserFolder f) =>
        (f.Processes ?? []).SelectMany(System.Diagnostics.Process.GetProcessesByName).ToList();

    public static async Task CloseAppsAsync(UserFolder f)
    {
        var ps = RunningApps(f);
        foreach (var p in ps) try { p.CloseMainWindow(); } catch { }
        await Task.Delay(2500).ConfigureAwait(false);
        foreach (var p in RunningApps(f)) try { p.Kill(true); } catch { }
        await Task.Delay(800).ConfigureAwait(false);
    }

    /// <summary>Thư mục đang được OneDrive sao lưu (Known Folder Move).</summary>
    public static bool InOneDrive(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 3 } od && path.StartsWith(od.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static (long Bytes, int Files) Measure(string dir)
    {
        long b = 0;
        var n = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { b += f.Length; n++; } catch { }
            }
        }
        catch { }
        return (b, n);
    }

    /// <summary>Ổ cố định không phải ổ Windows — gợi ý làm nơi chứa.</summary>
    public static List<string> SuggestedBases()
    {
        var sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        var user = Environment.UserName;
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.Equals(sys, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.AvailableFreeSpace)
            .Select(d => Path.Combine(d.Name, "Users", user)).ToList();
    }

    /// <summary>
    /// Chuyển một thư mục: kiểm tra, chép (robocopy /MOVE, giữ thuộc tính + thời gian, bỏ qua junction), rồi trỏ Known Folder sang chỗ mới.
    /// File đang mở không chuyển được sẽ được giữ lại ở chỗ cũ và báo số lượng.
    /// </summary>
    public static async Task<OpResult> MoveAsync(UserFolder f, string target, Action<string> status, CancellationToken ct)
    {
        if (f.IsApp) return await MoveAppAsync(f, target, status, ct).ConfigureAwait(false);
        var src = GetPath(f).TrimEnd('\\');
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (src.Equals(target, StringComparison.OrdinalIgnoreCase)) return OpResult.Fail($"{f.Name} đã ở {target}.");
        if (target.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) return OpResult.Fail("Không chuyển thư mục vào bên trong chính nó.");
        if (Path.GetPathRoot(target)!.TrimEnd('\\').Length > 0 && target.Length <= 3) return OpResult.Fail("Không chuyển thẳng vào gốc ổ đĩa — chọn một thư mục con.");
        if (Folders.Any(o => o.Key != f.Key && GetPath(o).TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase)))
            return OpResult.Fail($"{target} đang là vị trí của một thư mục khác.");
        if (Folders.FirstOrDefault(o => o.Key != f.Key && GetPath(o).StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) is { } inner)
            return OpResult.Fail($"{inner.Name} đang nằm bên trong {f.Name} — chuyển {inner.Name} ra ngoài trước.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any(e => !Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            return OpResult.Fail($"Thư mục đích {target} đã có dữ liệu — chọn thư mục trống để tránh trộn lẫn.");

        var (bytes, files) = Directory.Exists(src) ? Measure(src) : (0, 0);
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (!drive.IsReady) return OpResult.Fail($"Ổ {drive.Name} chưa sẵn sàng.");
        var sameDrive = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if (!sameDrive && drive.AvailableFreeSpace < bytes + 512L * 1024 * 1024)
            return OpResult.Fail($"Ổ {drive.Name} không đủ chỗ: cần {Fmt.Size(bytes)}, còn {Fmt.Size(drive.AvailableFreeSpace)}.");

        Directory.CreateDirectory(target);
        if (Directory.Exists(src) && files > 0 || Directory.Exists(src) && Directory.EnumerateFileSystemEntries(src).Any())
        {
            status($"Đang chuyển {files:N0} file ({Fmt.Size(bytes)})…");
            var done = 0;
            var r = await ProcessRunner.RunAsync("robocopy.exe",
                $"\"{src}\" \"{target}\" /E /MOVE /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /MT:8 /NP /NDL /NJH /NJS /BYTES",
                line => { if (line.Trim().Length > 0 && ++done % 25 == 0) status($"Đang chuyển {f.Name}: {Math.Min(done, files):N0}/{files:N0} file…"); },
                ct, ProcessRunner.Oem);
            // robocopy: 0-7 thành công (có thể kèm file bỏ qua), ≥ 8 lỗi
            if (r.ExitCode >= 8 && !Directory.EnumerateFileSystemEntries(target).Any())
                return OpResult.Fail($"Không chép được {f.Name} (robocopy mã {r.ExitCode}): {OpResult.FirstLine(r.Text)}");
        }

        // Trỏ Known Folder sang chỗ mới (Windows tự cập nhật registry User Shell Folders)
        var hr = SHSetKnownFolderPath(f.Id, 0, IntPtr.Zero, target);
        if (hr != 0) return OpResult.Fail($"Đã chép nhưng không đổi được vị trí {f.Name} (HRESULT 0x{hr:X8}).");
        SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);

        var left = Directory.Exists(src) ? Measure(src).Files : 0;
        if (Directory.Exists(src) && left == 0)
            try { Directory.Delete(src, true); } catch { }
        return OpResult.Success($"Đã chuyển {f.Name} → {target} ({files:N0} file, {Fmt.Size(bytes)})" +
                                (left > 0 ? $". Còn {left} file đang mở chưa chuyển, vẫn ở {src} — đóng ứng dụng rồi chép tay." : "."));
    }

    // ================= Dữ liệu ứng dụng (junction) =================
    private static Task<ProcessResult> Robo(string src, string dst, Action<string> status, string name, int files, CancellationToken ct)
    {
        var done = 0;
        return ProcessRunner.RunAsync("robocopy.exe",
            $"\"{src}\" \"{dst}\" /E /MOVE /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /MT:8 /NP /NDL /NJH /NJS /BYTES",
            line => { if (line.Trim().Length > 0 && ++done % 25 == 0) status($"Đang chuyển {name}: {Math.Min(done, files):N0}/{files:N0} file…"); },
            ct, ProcessRunner.Oem);
    }

    private static async Task<bool> MakeJunctionAsync(string link, string target)
    {
        var r = await ProcessRunner.ConsoleAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"").ConfigureAwait(false);
        return r.Success && LinkTarget(link) is not null;
    }

    /// <summary>
    /// Chuyển dữ liệu ứng dụng sang <paramref name="target"/> rồi đặt junction ở chỗ cũ. Trả về mặc định khi
    /// <paramref name="target"/> trùng chỗ cũ: gỡ junction, chép dữ liệu về.
    /// Không bao giờ xoá dữ liệu khi chép chưa trọn: còn file chưa chuyển được thì chép ngược lại và báo lỗi.
    /// </summary>
    private static async Task<OpResult> MoveAppAsync(UserFolder f, string target, Action<string> status, CancellationToken ct)
    {
        var orig = f.AppPath!().TrimEnd('\\');
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (!Directory.Exists(orig)) return OpResult.Fail($"Chưa có dữ liệu {f.Name} trên máy (chưa cài hoặc chưa mở lần nào).");
        if (RunningApps(f).Count > 0) return OpResult.Fail($"{f.Name} đang mở — đóng {f.Name} trước khi chuyển.");
        var cur = LinkTarget(orig);
        var src = cur ?? orig;
        var toDefault = target.Equals(orig, StringComparison.OrdinalIgnoreCase);
        if (toDefault && cur is null) return OpResult.Fail($"{f.Name} đang ở vị trí mặc định.");
        if (!toDefault && src.Equals(target, StringComparison.OrdinalIgnoreCase)) return OpResult.Fail($"{f.Name} đã ở {target}.");
        if (target.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase) || target.StartsWith(orig + "\\", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("Không chuyển thư mục vào bên trong chính nó.");
        if (target.Length <= 3) return OpResult.Fail("Không chuyển thẳng vào gốc ổ đĩa — chọn một thư mục con.");
        if (!toDefault && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return OpResult.Fail($"Thư mục đích {target} đã có dữ liệu — chọn thư mục trống để tránh trộn lẫn.");
        if (!Directory.Exists(src)) return OpResult.Fail($"Không thấy dữ liệu {f.Name} tại {src}.");

        var (bytes, files) = Measure(src);
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (!drive.IsReady) return OpResult.Fail($"Ổ {drive.Name} chưa sẵn sàng.");
        var sameDrive = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
        if (!sameDrive && drive.AvailableFreeSpace < bytes + 512L * 1024 * 1024)
            return OpResult.Fail($"Ổ {drive.Name} không đủ chỗ: cần {Fmt.Size(bytes)}, còn {Fmt.Size(drive.AvailableFreeSpace)}.");

        if (cur is not null)
        {
            // Đang là junction: gỡ junction (chỉ gỡ liên kết, không đụng dữ liệu) trước
            try { new DirectoryInfo(orig).Delete(false); } catch (Exception ex) { return OpResult.Fail($"Không gỡ được liên kết cũ: {ex.Message}"); }
        }
        Directory.CreateDirectory(target);
        status($"Đang chuyển {f.Name}: {files:N0} file ({Fmt.Size(bytes)})…");
        var r = await Robo(src, target, status, f.Name, files, ct).ConfigureAwait(false);
        var left = Directory.Exists(src) ? Measure(src).Files : 0;
        if (r.ExitCode >= 8 || left > 0)
        {
            // Chưa chuyển trọn: đưa mọi thứ về chỗ cũ, đặt lại như trước
            status($"Chưa chuyển trọn {f.Name} — đang trả dữ liệu về chỗ cũ…");
            await Robo(target, src, status, f.Name, files, ct).ConfigureAwait(false);
            if (cur is not null)
            {
                try { if (Directory.Exists(orig) && LinkTarget(orig) is null && !Directory.EnumerateFileSystemEntries(orig).Any()) Directory.Delete(orig); } catch { }
                if (!Directory.Exists(orig)) await MakeJunctionAsync(orig, src).ConfigureAwait(false);
            }
            return OpResult.Fail($"Không chuyển hết được {f.Name} ({left} file đang bị khoá, robocopy mã {r.ExitCode}). Dữ liệu vẫn ở {src} — đóng hẳn {f.Name} (cả biểu tượng ở khay hệ thống) rồi thử lại.");
        }
        if (toDefault)
        {
            try { if (Directory.Exists(src)) Directory.Delete(src, true); } catch { }
            return OpResult.Success($"Đã trả {f.Name} về {orig} ({files:N0} file, {Fmt.Size(bytes)}).");
        }
        // Xoá thư mục cũ (đã trống) / thư mục đích cũ, đặt junction
        try { if (Directory.Exists(src) && !src.Equals(orig, StringComparison.OrdinalIgnoreCase)) Directory.Delete(src, true); } catch { }
        try { if (Directory.Exists(orig) && LinkTarget(orig) is null) Directory.Delete(orig, true); } catch (Exception ex)
        {
            return OpResult.Fail($"Đã chép {f.Name} sang {target} nhưng không xoá được thư mục cũ ({ex.Message}). Ứng dụng vẫn dùng chỗ cũ.");
        }
        if (!await MakeJunctionAsync(orig, target).ConfigureAwait(false))
        {
            // Không tạo được liên kết: trả dữ liệu về để ứng dụng chạy bình thường
            Directory.CreateDirectory(orig);
            await Robo(target, orig, status, f.Name, files, ct).ConfigureAwait(false);
            return OpResult.Fail($"Không tạo được liên kết {orig} → {target}; đã trả dữ liệu {f.Name} về chỗ cũ.");
        }
        return OpResult.Success($"Đã chuyển {f.Name} → {target} ({files:N0} file, {Fmt.Size(bytes)}). {f.Name} vẫn mở dữ liệu bình thường qua liên kết ở chỗ cũ.");
    }
}
