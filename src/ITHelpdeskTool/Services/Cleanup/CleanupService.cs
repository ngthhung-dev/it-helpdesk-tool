using System.Diagnostics;
using System.IO;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Cleanup;

/// <summary>Một vị trí cần dọn: thư mục (xoá nội dung bên trong, theo mẫu tên) hoặc một file đơn.</summary>
public sealed record CleanTarget(string Path, string Pattern = "*", bool Recursive = true, int MinAgeHours = 0);

public enum CleanKind { Files, RecycleBin }

public sealed record CleanupDef(
    string Key, string Title, string Description, Func<IEnumerable<CleanTarget>> Targets,
    bool DefaultChecked = true, CleanKind Kind = CleanKind.Files, string[]? StopServices = null, string? Note = null);

public sealed record ScanResult(long Bytes, int Files);

public sealed record CleanResult(long Freed, int Deleted, int Skipped);

/// <summary>Quét dung lượng và dọn các loại file rác. Chỉ xoá file, không động tới dữ liệu người dùng.</summary>
public static class CleanupService
{
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string SysDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    /// <summary>Thư mục profile của các trình duyệt nhân Chromium (Default, Profile 1, ...).</summary>
    private static IEnumerable<string> ChromiumProfiles()
    {
        string[] roots =
        [
            Path.Combine(Local, @"Google\Chrome\User Data"),
            Path.Combine(Local, @"Microsoft\Edge\User Data"),
            Path.Combine(Local, @"CocCoc\Browser\User Data"),
            Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(Local, @"Opera Software\Opera Stable"),
        ];
        foreach (var root in roots.Where(Directory.Exists))
        {
            if (root.EndsWith("Opera Stable")) { yield return root; continue; }
            foreach (var d in SafeDirs(root))
            {
                var n = System.IO.Path.GetFileName(d);
                if (n == "Default" || n.StartsWith("Profile ")) yield return d;
            }
        }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); } catch { return []; }
    }

    public static IReadOnlyList<CleanupDef> Items { get; } =
    [
        new("temp.user", "File tạm của người dùng", "Thư mục %TEMP% — chỉ xoá file cũ hơn 1 ngày để không ảnh hưởng chương trình đang chạy.",
            () => [new(System.IO.Path.GetTempPath(), MinAgeHours: 24)]),
        new("temp.windows", "File tạm của Windows", @"C:\Windows\Temp — file cũ hơn 1 ngày.",
            () => [new(System.IO.Path.Combine(Win, "Temp"), MinAgeHours: 24)]),
        new("wu", "Bộ nhớ đệm Windows Update", "Bản cập nhật đã tải về và đã cài xong. Dọn cũng hay dùng khi Windows Update bị kẹt.",
            () => [new(System.IO.Path.Combine(Win, @"SoftwareDistribution\Download"))], StopServices: ["wuauserv", "bits"]),
        new("do", "Bộ nhớ đệm Delivery Optimization", "File cập nhật Windows chia sẻ qua mạng LAN/Internet.",
            () => [new(System.IO.Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))],
            StopServices: ["DoSvc"]),
        new("wer", "Báo cáo lỗi Windows", "Báo cáo sự cố ứng dụng đã gửi/chưa gửi cho Microsoft.",
            () =>
            [
                new(System.IO.Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive")),
                new(System.IO.Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue")),
                new(System.IO.Path.Combine(ProgData, @"Microsoft\Windows\WER\Temp")),
                new(System.IO.Path.Combine(Local, @"Microsoft\Windows\WER")),
            ]),
        new("dumps", "File dump khi lỗi màn hình xanh / ứng dụng crash", "Chỉ cần khi đang điều tra lỗi màn hình xanh — có thể rất lớn (bằng dung lượng RAM).",
            () =>
            [
                new(System.IO.Path.Combine(Win, "Minidump")),
                new(Win, "MEMORY.DMP", Recursive: false),
                new(System.IO.Path.Combine(Win, "LiveKernelReports"), "*.dmp"),
                new(System.IO.Path.Combine(Local, "CrashDumps")),
            ]),
        new("logs", "Nhật ký cài đặt cũ (CBS, DISM)", "Log của Windows Update/DISM cũ hơn 7 ngày.",
            () =>
            [
                new(System.IO.Path.Combine(Win, @"Logs\CBS"), "*.log", MinAgeHours: 168),
                new(System.IO.Path.Combine(Win, @"Logs\CBS"), "*.cab", MinAgeHours: 168),
                new(System.IO.Path.Combine(Win, @"Logs\DISM"), "*.log", MinAgeHours: 168),
            ]),
        new("shader", "Bộ nhớ đệm DirectX / shader card đồ hoạ", "Game và trình duyệt tự tạo lại khi cần.",
            () =>
            [
                new(System.IO.Path.Combine(Local, "D3DSCache")),
                new(System.IO.Path.Combine(Local, @"NVIDIA\DXCache")),
                new(System.IO.Path.Combine(Local, @"NVIDIA\GLCache")),
                new(System.IO.Path.Combine(Local, @"AMD\DxCache")),
                new(System.IO.Path.Combine(Local, @"AMD\DxcCache")),
            ]),
        new("browsers", "Bộ nhớ đệm trình duyệt", "Chrome, Edge, Cốc Cốc, Brave, Opera, Firefox. Không xoá mật khẩu, lịch sử, cookie đăng nhập.",
            () =>
            {
                var list = new List<CleanTarget>();
                foreach (var p in ChromiumProfiles())
                    foreach (var sub in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", "DawnCache", "DawnGraphiteCache" })
                        list.Add(new(System.IO.Path.Combine(p, sub)));
                var ff = System.IO.Path.Combine(Local, @"Mozilla\Firefox\Profiles");
                foreach (var p in SafeDirs(ff)) list.Add(new(System.IO.Path.Combine(p, "cache2")));
                return list;
            }, Note: "Nên tắt trình duyệt trước khi dọn — file đang dùng sẽ bị bỏ qua."),
        new("thumbs", "Bộ nhớ đệm ảnh thu nhỏ & icon", "Dùng khi ảnh thu nhỏ/icon hiển thị sai. Windows tự tạo lại (lần đầu mở thư mục sẽ chậm hơn).",
            () =>
            [
                new(System.IO.Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false),
                new(System.IO.Path.Combine(Local, @"Microsoft\Windows\Explorer"), "iconcache_*.db", Recursive: false),
            ], DefaultChecked: false, Note: "Nhiều file đang bị Explorer giữ — khởi động lại máy rồi dọn sẽ sạch hơn."),
        new("prefetch", "Prefetch", "Windows dùng để mở ứng dụng nhanh hơn; dọn xong lần mở đầu sẽ chậm một chút.",
            () => [new(System.IO.Path.Combine(Win, "Prefetch"), "*.pf", Recursive: false)], DefaultChecked: false),
        new("recycle", "Thùng rác", "Xoá vĩnh viễn mọi thứ trong Thùng rác.", () => [], Kind: CleanKind.RecycleBin),
    ];

    // ================= Quét =================
    private static readonly EnumerationOptions DirEnum = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,   // không đi vào junction/symlink
    };

    private static IEnumerable<FileInfo> FilesOf(CleanTarget t)
    {
        if (File.Exists(t.Path)) { yield return new FileInfo(t.Path); yield break; }
        if (!Directory.Exists(t.Path)) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = t.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(t.Path).EnumerateFiles(t.Pattern, opts); }
        catch { yield break; }
        var cutoff = DateTime.Now.AddHours(-t.MinAgeHours);
        using var e = files.GetEnumerator();
        while (true)
        {
            FileInfo f;
            try { if (!e.MoveNext()) break; f = e.Current; }
            catch { break; }
            if (t.MinAgeHours > 0)
            {
                try { if (f.LastWriteTime > cutoff) continue; } catch { continue; }
            }
            yield return f;
        }
    }

    public static ScanResult Scan(CleanupDef d)
    {
        if (d.Kind == CleanKind.RecycleBin)
        {
            var (b, n) = FileOps.QueryRecycleBin();
            return new ScanResult(b, (int)n);
        }
        long bytes = 0;
        var count = 0;
        foreach (var t in d.Targets())
            foreach (var f in FilesOf(t))
            {
                try { bytes += f.Length; count++; } catch { }
            }
        return new ScanResult(bytes, count);
    }

    // ================= Dọn =================
    public static async Task<CleanResult> CleanAsync(CleanupDef d)
    {
        if (d.Kind == CleanKind.RecycleBin)
        {
            var (b, n) = FileOps.QueryRecycleBin();
            return FileOps.EmptyRecycleBin() ? new CleanResult(b, (int)n, 0) : new CleanResult(0, 0, (int)n);
        }

        foreach (var s in d.StopServices ?? []) await ProcessRunner.ConsoleAsync("net.exe", $"stop {s} /y");
        try
        {
            return await Task.Run(() =>
            {
                long freed = 0;
                int deleted = 0, skipped = 0;
                foreach (var t in d.Targets())
                {
                    foreach (var f in FilesOf(t).ToList())
                    {
                        try
                        {
                            var len = f.Length;
                            if (f.IsReadOnly) f.IsReadOnly = false;
                            f.Delete();
                            freed += len;
                            deleted++;
                        }
                        catch { skipped++; }
                    }
                    if (Directory.Exists(t.Path) && t.Recursive && t.Pattern == "*") RemoveEmptyDirs(t.Path, t.MinAgeHours);
                }
                return new CleanResult(freed, deleted, skipped);
            });
        }
        finally
        {
            foreach (var s in (d.StopServices ?? []).Reverse()) await ProcessRunner.ConsoleAsync("net.exe", $"start {s}");
        }
    }

    /// <summary>Xoá các thư mục con đã rỗng (không xoá chính thư mục gốc).</summary>
    private static void RemoveEmptyDirs(string root, int minAgeHours)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root, "*", DirEnum).ToList(); } catch { return; }
        foreach (var d in dirs)
        {
            var info = new DirectoryInfo(d);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            RemoveEmptyDirs(d, minAgeHours);
            try
            {
                if (minAgeHours > 0 && info.LastWriteTime > DateTime.Now.AddHours(-minAgeHours)) continue;
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch { }
        }
    }

    public static bool BrowsersRunning(out string names)
    {
        var map = new Dictionary<string, string> { ["chrome"] = "Chrome", ["msedge"] = "Edge", ["browser"] = "Cốc Cốc", ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera" };
        var running = map.Where(kv => Process.GetProcessesByName(kv.Key).Length > 0).Select(kv => kv.Value).ToList();
        names = string.Join(", ", running);
        return running.Count > 0;
    }

    // ================= Dọn sâu =================
    /// <summary>Các thư mục cài Windows cũ / file nâng cấp còn sót (Windows.old, $Windows.~BT, ...).</summary>
    public static IReadOnlyList<string> OldInstallFolders =>
        new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS", "ESD", "$WinREAgent" }
            .Select(n => System.IO.Path.Combine(SysDrive, n)).ToList();

    public static long FolderSize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    public static (long Bytes, List<string> Found) ScanOldInstalls()
    {
        var found = OldInstallFolders.Where(Directory.Exists).ToList();
        return (found.Sum(FolderSize), found);
    }

    /// <summary>
    /// Xoá bản Windows cũ: lấy quyền sở hữu cho nhóm Administrators bằng icacls (theo SID, không hỏi,
    /// không phụ thuộc ngôn ngữ Windows), cấp toàn quyền rồi xoá. Mỗi bước có giới hạn thời gian.
    /// </summary>
    public static async Task<OpResult> RemoveOldInstallsAsync(Action<string>? progress = null)
    {
        var (before, found) = ScanOldInstalls();
        if (found.Count == 0) return OpResult.Success("Không có bản Windows cũ nào để xoá.");

        foreach (var dir in found)
        {
            var name = System.IO.Path.GetFileName(dir);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                progress?.Invoke($"Đang lấy quyền sở hữu {name}…");
                await ProcessRunner.ConsoleAsync("icacls.exe", $"\"{dir}\" /setowner *S-1-5-32-544 /T /C /L /Q", cts.Token);
                progress?.Invoke($"Đang cấp quyền {name}…");
                await ProcessRunner.ConsoleAsync("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-544:F /T /C /L /Q", cts.Token);
                progress?.Invoke($"Đang xoá {name}…");
                await Task.Run(() => ForceDelete(dir), cts.Token);
                if (Directory.Exists(dir)) await ProcessRunner.ConsoleAsync("cmd.exe", $"/c rd /s /q \"{dir}\"", cts.Token);
            }
            catch (OperationCanceledException) { Log.Warn($"Xoá {dir} quá 30 phút, dừng lại"); }
        }

        var (after, left) = ScanOldInstalls();
        var freed = Math.Max(0, before - after);
        return left.Count == 0
            ? OpResult.Success($"Đã xoá bản Windows cũ, giải phóng {Fmt.Size(freed)}.")
            : OpResult.Fail($"Đã giải phóng {Fmt.Size(freed)} nhưng còn sót: {string.Join(", ", left)} (thử khởi động lại máy rồi chạy lại, hoặc dùng Disk Cleanup của Windows).");
    }

    /// <summary>Xoá thư mục kể cả file chỉ đọc/ẩn/hệ thống; không đi vào junction.</summary>
    private static void ForceDelete(string dir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
                try { f.Attributes = FileAttributes.Normal; } catch { }
        }
        catch { }
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { Log.Warn($"Xoá {dir}: {ex.Message}"); }
    }

    /// <summary>Dọn kho thành phần Windows (WinSxS) bằng DISM — mất 5–30 phút.</summary>
    public static async Task<OpResult> ComponentCleanupAsync(bool resetBase, CancellationToken ct = default)
    {
        var free0 = new DriveInfo(SysDrive).AvailableFreeSpace;
        var r = await ProcessRunner.RunAsync("dism.exe",
            "/Online /Cleanup-Image /StartComponentCleanup" + (resetBase ? " /ResetBase" : ""), ct: ct, encoding: ProcessRunner.Oem);
        var freed = new DriveInfo(SysDrive).AvailableFreeSpace - free0;
        return r.Success
            ? OpResult.Success($"Đã dọn kho thành phần Windows (WinSxS)" + (freed > 0 ? $", giải phóng khoảng {Fmt.Size(freed)}." : "."))
            : OpResult.Fail($"DISM lỗi (mã {r.ExitCode}): {OpResult.FirstLine(r.Text)}");
    }

    public static (long Free, long Total) SystemDriveSpace()
    {
        var d = new DriveInfo(SysDrive);
        return (d.AvailableFreeSpace, d.TotalSize);
    }
}
