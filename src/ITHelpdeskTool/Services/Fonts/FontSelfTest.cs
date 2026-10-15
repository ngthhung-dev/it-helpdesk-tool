using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services.Fonts;

/// <summary>Đọc font hệ thống, tải font Google, cài / gỡ cho máy và cho tài khoản, zip, sao lưu. --selftest fonts</summary>
public static class FontSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();
        var win = FontService.MachineDir;

        await t.Do("read.system", async () =>
        {
            var seg = FontService.Read(Path.Combine(win, "segoeui.ttf"));
            Check(seg.Family == "Segoe UI" && seg.Vietnamese && !seg.Legacy, $"segoeui: {seg}");
            var times = FontService.Read(Path.Combine(win, "times.ttf"));
            Check(times.Vietnamese, "Times New Roman phải đủ dấu");
            var wd = Path.Combine(win, "wingding.ttf");
            var wds = File.Exists(wd) ? FontService.Read(wd) : null;
            Check(wds is null || !wds.Vietnamese, "Wingdings không thể có dấu tiếng Việt");
            var ttc = Directory.GetFiles(win, "*.ttc").FirstOrDefault();
            var ttcInfo = ttc is null ? null : FontService.Read(ttc);
            await Task.CompletedTask;
            return $"{seg.RegName} · {times.RegName} · ttc: {ttcInfo?.RegName ?? "không có"}";
        });

        await t.Do("installed.list", async () =>
        {
            var list = FontService.ReadInstalled();
            var seg = list.FirstOrDefault(f => f.FileName.Equals("segoeui.ttf", StringComparison.OrdinalIgnoreCase));
            Check(list.Count > 50, $"chỉ có {list.Count} font");
            Check(seg is { IsSystem: true }, "segoeui phải là font hệ thống");
            Check(!FontService.Uninstall(seg!).Ok, "không được gỡ font hệ thống");
            await Task.CompletedTask;
            return $"{list.Count} font, {list.Count(f => f.IsSystem)} của Windows";
        });

        List<string> files = [];
        await t.Do("google.download", async () =>
        {
            var be = GoogleFontCatalog.Fonts.First(f => f.Dir == "bevietnampro");
            var lex = GoogleFontCatalog.Fonts.First(f => f.Dir == "lexend");
            files = [.. await GoogleFontCatalog.DownloadAsync(be), .. await GoogleFontCatalog.DownloadAsync(lex)];
            var infos = files.Select(FontService.Read).ToList();
            Check(infos.All(i => i.Vietnamese), "font tải về phải đủ dấu: " + string.Join(", ", infos.Where(i => !i.Vietnamese).Select(i => i.FileName)));
            Check(infos.Any(i => i.Family == "Be Vietnam Pro" && i.Face == "Bold"), "đọc sai tên: " + string.Join(", ", infos.Select(i => $"{i.Family}/{i.Face}")));
            return string.Join(", ", infos.Select(i => i.RegName));
        });

        // Kiểm tra mọi link font Google còn sống
        await t.Do("google.catalog", async () =>
        {
            var bad = new List<string>();
            foreach (var f in GoogleFontCatalog.Fonts)
                foreach (var u in f.Urls)
                {
                    try { await Downloader.DownloadAsync(u, Path.Combine(Downloader.CacheDir, "gf-check"), "x.ttf"); }
                    catch (Exception ex) { bad.Add($"{f.Name}: {ex.Message}"); }
                }
            Check(bad.Count == 0, string.Join("; ", bad));
            return $"{GoogleFontCatalog.Fonts.Count} font, {GoogleFontCatalog.Fonts.Sum(f => f.Files.Length)} file đều tải được";
        });

        await t.Do("install.machine", async () =>
        {
            var info = FontService.Read(files.First(f => f.EndsWith("BeVietnamPro-Bold.ttf")));
            Need(FontService.Install(info, allUsers: true));
            Check(File.Exists(Path.Combine(win, "BeVietnamPro-Bold.ttf")), "file chưa vào Fonts");
            using (var k = Registry.LocalMachine.OpenSubKey(FontService.FontsKey))
                Check(k?.GetValue(info.RegName) as string == "BeVietnamPro-Bold.ttf", $"registry thiếu {info.RegName}");
            var again = FontService.Install(info, allUsers: true);
            Check(again.Ok && again.Message.Contains("đã có sẵn"), "cài lại phải báo đã có: " + again.Message);
            var inst = FontService.ReadInstalled().First(f => f.RegName == info.RegName);
            Check(!inst.IsSystem && inst.Scope == FontScope.Machine, "phân loại sai");
            // WPF thấy được font sau khi cài
            await Task.Delay(500);
            var r = FontService.Uninstall(inst);
            Check(r.Ok, r.Message);
            Check(!File.Exists(Path.Combine(win, "BeVietnamPro-Bold.ttf")) || r.NeedsRestart, "file chưa xoá");
            using (var k = Registry.LocalMachine.OpenSubKey(FontService.FontsKey))
                Check(k?.GetValue(info.RegName) is null, "registry chưa xoá");
            return $"{info.RegName} → cài, cài lại, gỡ ok";
        });

        await t.Do("install.user", async () =>
        {
            var info = FontService.Read(files.First(f => f.Contains("Lexend")));
            Need(FontService.Install(info, allUsers: false));
            var dest = Path.Combine(FontService.UserDir, info.FileName);
            Check(File.Exists(dest), "file chưa vào thư mục font của tài khoản");
            var inst = FontService.ReadInstalled().FirstOrDefault(f => f.Scope == FontScope.User && f.RegName == info.RegName);
            Check(inst is not null && inst.File == dest, "registry HKCU sai");
            var r = FontService.Uninstall(inst!);
            Check(r.Ok, r.Message);
            await Task.CompletedTask;
            return $"{info.RegName} ({info.FileName}) cho tài khoản này → gỡ ok";
        });

        await t.Do("scan.match", async () =>
        {
            // Bộ đọc nhanh phải cho kết quả giống GlyphTypeface trên toàn bộ font của Windows
            var all = Directory.GetFiles(win).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc").ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fast = all.ToDictionary(f => f, FontScan.Probe);
            var tFast = sw.ElapsedMilliseconds;
            sw.Restart();
            var bad = new List<string>();
            var famBad = 0;
            foreach (var f in all)
            {
                FontFileInfo slow;
                try { slow = FontService.Read(f); } catch { continue; }
                if (slow.Vietnamese != fast[f].Vietnamese) bad.Add($"{Path.GetFileName(f)} wpf={slow.Vietnamese} nhanh={fast[f].Vietnamese}");
                if (!f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) && slow.Family != fast[f].Family) famBad++;
            }
            var tSlow = sw.ElapsedMilliseconds;
            Check(bad.Count <= all.Count / 100, $"{bad.Count} lệch: " + string.Join("; ", bad.Take(10)));
            Check(famBad <= all.Count / 50, $"{famBad} tên họ lệch");
            Check(FontScan.Probe(Path.Combine(win, "segoeui.ttf")) is { Family: "Segoe UI", Vietnamese: true }, "segoeui sai");
            Check(FontScan.IsLegacyName(".VnTime") && FontScan.IsLegacyName("VNI-Times") && !FontScan.IsLegacyName("Arial"), "nhận diện VNI/TCVN3 sai");
            await Task.CompletedTask;
            return $"{all.Count} font: nhanh {tFast} ms, GlyphTypeface {tSlow} ms · {fast.Values.Count(v => v.Vietnamese)} đủ dấu · lệch {bad.Count} ({string.Join(", ", bad.Take(3))}) · tên lệch {famBad}";
        });

        await t.Do("bulk.many", async () =>
        {
            // Giả lập máy có rất nhiều font cài thêm: 3000 bản sao cho tài khoản này, rồi gỡ hàng loạt
            const int N = 3000;
            var src = files.First(f => f.EndsWith("BeVietnamPro-Bold.ttf"));
            Directory.CreateDirectory(FontService.UserDir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var k = Registry.CurrentUser.CreateSubKey(FontService.FontsKey, true))
                for (var i = 0; i < N; i++)
                {
                    var dest = Path.Combine(FontService.UserDir, $"ithdtest{i:0000}.ttf");
                    File.Copy(src, dest, true);
                    k.SetValue($"ITHD Test {i:0000} (TrueType)", dest);
                }
            var tMake = sw.ElapsedMilliseconds;
            sw.Restart();
            var list = FontService.ReadInstalled();
            var tRead = sw.ElapsedMilliseconds;
            sw.Restart();
            var map = FontScan.ScanMany(list);
            var tScan = sw.ElapsedMilliseconds;
            sw.Restart();
            FontScan.ScanMany(list);
            var tCached = sw.ElapsedMilliseconds;
            var mine = list.Where(f => f.RegName.StartsWith("ITHD Test ")).ToList();
            Check(mine.Count == N, $"đọc được {mine.Count}/{N}");
            Check(mine.All(f => !f.IsSystem && f.Scope == FontScope.User), "phân loại sai");
            Check(mine.All(f => map.TryGetValue(f.File, out var r) && r.Vietnamese && r.Family == "Be Vietnam Pro"), "phân tích sai");
            Check(tCached < tScan || tScan < 200, $"bộ đệm không nhanh hơn ({tCached} vs {tScan} ms)");
            sw.Restart();
            var fail = FontService.UninstallMany(mine).Count(r => !r.Result.Ok);
            var tDel = sw.ElapsedMilliseconds;
            Check(fail == 0, $"{fail} font gỡ lỗi");
            Check(FontService.ReadInstalled(false).All(f => !f.RegName.StartsWith("ITHD Test ")), "còn sót registry");
            Check(!Directory.EnumerateFiles(FontService.UserDir, "ithdtest*").Any(), "còn sót file");
            await Task.CompletedTask;
            return $"{list.Count} font (thêm {N}): tạo {tMake} ms · đọc danh sách {tRead} ms · phân tích {tScan} ms · lần 2 (bộ đệm) {tCached} ms · gỡ {N} font {tDel} ms";
        });

        await t.Do("collect.zip", async () =>
        {
            var zip = Path.Combine(Path.GetTempPath(), "ithd-fonts.zip");
            File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var f in files.Take(3)) z.CreateEntryFromFile(f, "fonts/" + Path.GetFileName(f));
                z.CreateEntry("readme.txt");
                z.CreateEntryFromFile(files[0], "__MACOSX/._x.ttf");
            }
            var got = FontService.Collect([zip]);
            File.Delete(zip);
            Check(got.Count == 3, $"zip: {got.Count} file");
            var folder = FontService.Collect([Path.GetDirectoryName(files[0])!]);
            Check(folder.Count >= 7, $"thư mục: {folder.Count}");
            await Task.CompletedTask;
            return $"zip 3 font (bỏ qua __MACOSX, readme) · thư mục {folder.Count} font";
        });

        await t.Do("backup", async () =>
        {
            var dest = Path.Combine(Path.GetTempPath(), "ithd-font-backup");
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            var r = FontService.Backup(dest);
            await Task.CompletedTask;
            return r.Message;
        });

        await t.Do("reset.cache", async () =>
        {
            var r = await FontService.ResetCacheAsync();
            var q = await ProcessRunner.ConsoleAsync("sc.exe", "query FontCache");
            Check(q.Output.Contains("RUNNING"), "FontCache chưa chạy lại");
            return r.Message;
        });

        return await t.ToFileAsync(path);
    }
}
