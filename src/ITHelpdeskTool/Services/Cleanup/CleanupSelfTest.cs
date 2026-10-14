using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.SysConfig;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Cleanup;

/// <summary>Tạo file rác giả ở các vị trí, quét rồi dọn và kiểm tra. --selftest cleanup</summary>
public static class CleanupSelfTest
{
    public static async Task<List<Step>> RunAsync()
    {
        var steps = new List<Step>();
        async Task Do(string name, Func<Task<string>> body)
        {
            var sw = Stopwatch.StartNew();
            Log.Info($"selftest: {name} …");
            try { steps.Add(new Step(name, true, await body(), sw.ElapsedMilliseconds)); }
            catch (Exception ex) { steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds)); }
        }
        static void Check(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }
        static string Need(OpResult r) => r.Ok ? r.Message : throw new InvalidOperationException(r.Message);
        static CleanupDef Def(string key) => CleanupService.Items.First(i => i.Key == key);
        static string Make(string path, int kb, bool old = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[kb * 1024]);
            if (old) File.SetLastWriteTime(path, DateTime.Now.AddDays(-3));
            return path;
        }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        await Do("scan.all", async () =>
        {
            var parts = await Task.WhenAll(CleanupService.Items.Select(d => Task.Run(() => (d.Key, R: CleanupService.Scan(d)))));
            return string.Join(" | ", parts.Select(p => $"{p.Key}: {Fmt.Size(p.R.Bytes)} ({p.R.Files})"));
        });

        // File tạm: file cũ bị xoá, file mới giữ lại, file đang mở bị bỏ qua
        await Do("temp.user.age.locked", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "hdtest_clean");
            var oldF = Make(Path.Combine(dir, "old.bin"), 512);
            var newF = Make(Path.Combine(dir, "new.bin"), 64, old: false);
            var lockF = Make(Path.Combine(dir, "locked.bin"), 32);
            var before = CleanupService.Scan(Def("temp.user"));
            Check(before.Bytes >= 512 * 1024 + 32 * 1024, $"quét chưa thấy file cũ ({before.Bytes})");
            CleanResult r;
            using (new FileStream(lockF, FileMode.Open, FileAccess.Read, FileShare.None))
                r = await CleanupService.CleanAsync(Def("temp.user"));
            Check(!File.Exists(oldF), "file cũ chưa bị xoá");
            Check(File.Exists(newF), "file mới bị xoá nhầm");
            Check(File.Exists(lockF) && r.Skipped >= 1, "file đang mở phải được bỏ qua");
            Directory.Delete(dir, true);
            return $"dọn {Fmt.Size(r.Freed)}, {r.Deleted} file, bỏ qua {r.Skipped}";
        });

        await Do("temp.windows", async () =>
        {
            var f = Make(Path.Combine(win, @"Temp\hdtest_clean\a.tmp"), 256);
            // Thư mục rỗng cũ bị xoá; thư mục vừa có thay đổi (vừa xoá file bên trong) được giữ tới lần dọn sau
            var emptyOld = Path.Combine(win, @"Temp\hdtest_empty");
            Directory.CreateDirectory(emptyOld);
            Directory.SetLastWriteTime(emptyOld, DateTime.Now.AddDays(-3));
            var r = await CleanupService.CleanAsync(Def("temp.windows"));
            Check(!File.Exists(f), "chưa xoá");
            Check(!Directory.Exists(emptyOld), "thư mục rỗng cũ chưa được dọn");
            try { Directory.Delete(Path.Combine(win, @"Temp\hdtest_clean"), true); } catch { }
            return $"dọn {Fmt.Size(r.Freed)}, bỏ qua {r.Skipped}";
        });

        await Do("wu.cache", async () =>
        {
            var f = Make(Path.Combine(win, @"SoftwareDistribution\Download\hdtest\update.cab"), 300, old: false);
            var r = await CleanupService.CleanAsync(Def("wu"));
            Check(!File.Exists(f), "chưa xoá");
            var svc = (await ProcessRunner.ConsoleAsync("sc.exe", "query wuauserv")).Output;
            return $"dọn {Fmt.Size(r.Freed)} · wuauserv sau khi dọn: {(svc.Contains("RUNNING") ? "đang chạy" : "đã dừng")}";
        });

        await Do("dumps", async () =>
        {
            var f = Make(Path.Combine(win, @"Minidump\hdtest.dmp"), 128, old: false);
            var g = Make(Path.Combine(local, @"CrashDumps\hdtest.exe.123.dmp"), 64, old: false);
            var s = CleanupService.Scan(Def("dumps"));
            Check(s.Files >= 2, "quét chưa thấy dump");
            var r = await CleanupService.CleanAsync(Def("dumps"));
            Check(!File.Exists(f) && !File.Exists(g), "chưa xoá dump");
            return $"dọn {Fmt.Size(r.Freed)}";
        });

        await Do("browsers", async () =>
        {
            var chrome = Make(Path.Combine(local, @"Google\Chrome\User Data\Profile 9\Cache\Cache_Data\f_000001"), 200, old: false);
            var edge = Make(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Code Cache\js\abc"), 100, old: false);
            var ff = Make(Path.Combine(local, @"Mozilla\Firefox\Profiles\hdtest.default\cache2\entries\X1"), 50, old: false);
            var keep = Make(Path.Combine(local, @"Google\Chrome\User Data\Profile 9\Login Data"), 4, old: false);
            var s = CleanupService.Scan(Def("browsers"));
            Check(s.Bytes >= 350 * 1024, $"quét chưa đủ ({s.Bytes})");
            var r = await CleanupService.CleanAsync(Def("browsers"));
            Check(!File.Exists(chrome) && !File.Exists(edge) && !File.Exists(ff), "còn file cache");
            Check(File.Exists(keep), "xoá nhầm file mật khẩu Login Data");
            Directory.Delete(Path.Combine(local, @"Google\Chrome\User Data\Profile 9"), true);
            Directory.Delete(Path.Combine(local, @"Mozilla\Firefox\Profiles\hdtest.default"), true);
            return $"dọn {Fmt.Size(r.Freed)} · giữ nguyên Login Data";
        });

        await Do("recycle.bin", async () =>
        {
            var f = Make(Path.Combine(Path.GetTempPath(), "hdtest_recycle.bin"), 300, old: false);
            Check(FileOps.SendToRecycleBin(f), "không đưa được vào Thùng rác");
            var (b, n) = FileOps.QueryRecycleBin();
            Check(n >= 1 && b >= 300 * 1024, $"Thùng rác: {n} mục, {b} byte");
            var r = await CleanupService.CleanAsync(Def("recycle"));
            var (b2, n2) = FileOps.QueryRecycleBin();
            Check(n2 == 0, $"vẫn còn {n2} mục");
            return $"{n} mục {Fmt.Size(b)} → trống (dọn {Fmt.Size(r.Freed)})";
        });

        await Do("windows.old", async () =>
        {
            var root = Path.Combine(Path.GetPathRoot(win)!, "Windows.old");
            Make(Path.Combine(root, @"Windows\System32\hdtest.dll"), 400, old: false);
            Make(Path.Combine(root, @"Users\x\file.txt"), 10, old: false);
            File.SetAttributes(Path.Combine(root, @"Users\x\file.txt"), FileAttributes.ReadOnly | FileAttributes.Hidden);
            var (bytes, found) = CleanupService.ScanOldInstalls();
            Check(found.Count >= 1 && bytes >= 400 * 1024, "quét chưa thấy Windows.old");
            var m = Need(await CleanupService.RemoveOldInstallsAsync());
            Check(!Directory.Exists(root), "Windows.old vẫn còn");
            return m;
        });

        await Do("scan.after", async () =>
        {
            var parts = await Task.WhenAll(CleanupService.Items.Select(d => Task.Run(() => (d.Key, R: CleanupService.Scan(d)))));
            return string.Join(" | ", parts.Select(p => $"{p.Key}: {Fmt.Size(p.R.Bytes)}"));
        });
        return steps;
    }

    public static async Task<int> RunToFileAsync(string path)
    {
        var steps = await RunAsync();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(steps, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        }));
        return steps.All(s => s.Ok) ? 0 : 2;
    }
}
