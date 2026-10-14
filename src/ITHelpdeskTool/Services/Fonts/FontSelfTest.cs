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
