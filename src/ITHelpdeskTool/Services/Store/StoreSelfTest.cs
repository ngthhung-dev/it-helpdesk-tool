using System.Collections.Concurrent;
using System.IO;
using ITHelpdeskTool.Services.Uninstall;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services.Store;

/// <summary>Kiểm tra winget, kho phần mềm, cài/gỡ thật, bộ cài offline. --selftest store</summary>
public static class StoreSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();

        await t.Do("parse.table", async () =>
        {
            const string upgrade = """
                   -
                   \
                Name                               Id                           Version      Available    Source
                -----------------------------------------------------------------------------------------------
                Microsoft Visual C++ 2015-2022 Re… Microsoft.VCRedist.2015+.x64 14.38.33130  14.42.34433  winget
                7-Zip 23.01 (x64)                  7zip.7zip                    23.01        24.09        winget
                Some Long Package Name             Vendor.SomeVeryLongPackageI… 1.0          2.0          winget
                3 upgrades available.

                The following packages have an upgrade available, but require explicit targeting for upgrade:
                Name      Id              Version Available Source
                ----------------------------------------------------
                Discord   Discord.Discord 1.0.9   1.0.10    winget
                """;
            var rows = WingetService.ParseTable(upgrade);
            Check(rows.Count == 4, $"đọc được {rows.Count} dòng: {string.Join(" | ", rows.Select(r => r.Id))}");
            Check(rows[0].Id == "Microsoft.VCRedist.2015+.x64" && rows[0].Version == "14.38.33130" && rows[0].Available == "14.42.34433", rows[0].ToString());
            Check(rows[1].Name == "7-Zip 23.01 (x64)" && rows[1].Source == "winget", rows[1].ToString());
            Check(rows[2].Truncated, "không nhận ra Id bị cắt");
            Check(rows.Count(r => r.Id == "Discord.Discord") == 1, "thiếu bảng thứ 2");
            const string search = """
                Name       Id                  Version  Match          Source
                ------------------------------------------------------------------
                Notepad++  Notepad++.Notepad++ 8.7.1    Tag: notepad   winget
                """;
            var s = WingetService.ParseTable(search);
            Check(s.Count == 1 && s[0].Available == "" && s[0].Version == "8.7.1", s.FirstOrDefault()?.ToString() ?? "rỗng");
            await Task.CompletedTask;
            return "upgrade (2 bảng, id bị cắt) + search ok";
        });

        await t.Do("winget.locate", async () =>
        {
            var p = WingetService.Locate();
            if (p is null)
            {
                var r = await WingetService.RepairAsync();
                Check(r.Ok, "không có winget và không cài được: " + r.Message);
                p = WingetService.Locate();
            }
            return $"{p} · {await WingetService.VersionAsync()}";
        });

        Dictionary<string, string> installed = [];
        await t.Do("winget.installed", async () =>
        {
            installed = await WingetService.InstalledAsync();
            Check(installed.Count > 0, "danh sách rỗng");
            return $"{installed.Count} gói: " + string.Join(", ", installed.Keys.Take(6));
        });

        await t.Do("winget.upgrades", async () =>
        {
            var rows = await WingetService.UpgradesAsync(installed);
            Check(rows.All(r => r.Id.Length > 0 && r.Available.Length > 0), "dòng thiếu Id/bản mới");
            return $"{rows.Count} gói có bản mới: " + string.Join(", ", rows.Take(5).Select(r => $"{r.Id} {r.Version}→{r.Available}"));
        });

        await t.Do("winget.search", async () =>
        {
            var rows = await WingetService.SearchAsync("notepad++");
            Check(rows.Any(r => r.Id == "Notepad++.Notepad++"), "không thấy Notepad++: " + string.Join(", ", rows.Take(5).Select(r => r.Id)));
            return $"{rows.Count} kết quả";
        });

        // Mọi Id winget trong kho phải tồn tại
        await t.Do("catalog.ids", async () =>
        {
            var w = WingetService.Locate()!;
            var bad = new ConcurrentBag<string>();
            var ids = StoreCatalog.Apps.Where(a => a.Kind == StoreKind.Winget).Select(a => a.Id).ToList();
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (id, ct) =>
            {
                // Lỗi nội bộ / mạng của winget khi gọi nhiều cùng lúc → thử lại; chỉ tính là sai khi vẫn hỏng sau 3 lần
                ProcessResult? r = null;
                for (var i = 0; i < 3; i++)
                {
                    r = await ProcessRunner.RunAsync(w, $"show --id \"{id}\" --exact --source {WingetService.SourceOf(id)} --accept-source-agreements --disable-interactivity", ct: ct);
                    if (r.ExitCode == 0) break;
                    await Task.Delay(2000 * (i + 1), ct);
                }
                if (r!.ExitCode != 0) bad.Add($"{id} ({WingetService.Explain(r.ExitCode)})");
            });
            Check(bad.IsEmpty, "không có trên winget: " + string.Join(", ", bad));
            var dup = StoreCatalog.Apps.GroupBy(a => a.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Check(dup.Count == 0, "trùng: " + string.Join(", ", dup));
            var presetMissing = PostInstallService.Presets.SelectMany(p => p.Ids).Where(id => StoreCatalog.Find(id) is null).Distinct().ToList();
            Check(presetMissing.Count == 0, "preset có Id lạ: " + string.Join(", ", presetMissing));
            // Link GitHub (bản mới nhất) của các mục tải trực tiếp phải tìm được file
            var gh = new List<string>();
            foreach (var a in StoreCatalog.Apps.Where(a => a.Url.StartsWith("github:")))
            {
                try { gh.Add($"{a.Name}: {System.IO.Path.GetFileName(new Uri(await Downloader.ResolveAsync(a.Url)).LocalPath)}"); }
                catch (Exception ex) { bad.Add($"{a.Name} ({ex.Message})"); }
            }
            Check(bad.IsEmpty, "link GitHub lỗi: " + string.Join(", ", bad));
            // Trong mỗi nhóm phải xếp theo tên A→Z
            var vi = StringComparer.Create(new System.Globalization.CultureInfo("vi-VN"), true);
            var unsorted = StoreCatalog.Apps.GroupBy(a => a.Category)
                .Where(g => !g.Select(a => a.Name.TrimStart('.', ' ')).SequenceEqual(g.Select(a => a.Name.TrimStart('.', ' ')).OrderBy(n => n, vi)))
                .Select(g => g.Key).ToList();
            Check(unsorted.Count == 0, "chưa xếp A→Z: " + string.Join(", ", unsorted));
            return $"{ids.Count} Id (winget + Microsoft Store) đều có · {StoreCatalog.Apps.Count} phần mềm, mỗi nhóm xếp A→Z · {string.Join(", ", gh)}";
        });

        // Cài / gỡ thật một gói nhỏ qua winget (gói máy test chưa có sẵn)
        const string TestId = "WinDirStat.WinDirStat";
        await t.Do("winget.install.small", async () =>
        {
            var app = StoreCatalog.Find(TestId)!;
            if ((await WingetService.InstalledAsync()).ContainsKey(TestId)) await StoreInstaller.UninstallAsync(app);
            var lines = new List<string>();
            var r = await StoreInstaller.InstallAsync(app, s => lines.Add(s));
            Check(r.Ok, r.Message + " · " + string.Join(" / ", lines.Distinct().TakeLast(4)));
            var inst = await WingetService.InstalledAsync();
            Check(inst.ContainsKey(TestId), $"export không thấy {TestId} sau khi cài");
            return $"{r.Message} · trạng thái: {string.Join(" / ", lines.Distinct().Take(5))}";
        });

        await t.Do("winget.uninstall.small", async () =>
        {
            var r = await StoreInstaller.UninstallAsync(StoreCatalog.Find(TestId)!);
            Check(r.Ok, r.Message);
            var inst = await WingetService.InstalledAsync();
            Check(!inst.ContainsKey(TestId), $"vẫn còn {TestId}");
            return r.Message;
        });

        await t.Do("winget.install.notfound", async () =>
        {
            var r = await WingetService.InstallAsync("ITHD.Khong.Ton.Tai", "Gói giả");
            Check(!r.Ok && r.Message.Contains("không tìm thấy"), r.Message);
            return r.Message;
        });

        // Mục không qua winget
        foreach (var id in new[] { "unikey", "evkey" })
        {
            await t.Do($"portable.{id}", async () =>
            {
                var app = StoreCatalog.Find(id)!;
                var r = await StoreInstaller.InstallAsync(app);
                Check(r.Ok, r.Message);
                Check(StoreInstaller.IsInstalledLocally(app, []), "chưa nhận ra đã cài");
                var u = await StoreInstaller.UninstallAsync(app);
                Check(u.Ok && !StoreInstaller.IsInstalledLocally(app, []), "gỡ chưa sạch: " + u.Message);
                return r.Message;
            });
        }

        await t.Do("download.zalo", async () =>
        {
            var app = StoreCatalog.Find("zalo")!;
            var url = await Downloader.ResolveAsync(app.Url);
            var file = await Downloader.DownloadAsync(url, Path.Combine(Downloader.CacheDir, "selftest"));
            var size = new FileInfo(file).Length;
            var type = InstallerRunner.Detect(file);
            try { File.Delete(file); } catch { }
            Check(size > 5 * 1024 * 1024 && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), $"file lạ: {Path.GetFileName(file)} {size}");
            return $"{Path.GetFileName(file)} {Fmt.Size(size)} · bộ cài {type.Label} · cài im lặng: {(type.CanSilent ? "có" : "không")}";
        });

        // Bộ cài offline: tải bộ cài Notepad++ (NSIS) rồi cài im lặng như người dùng chọn từ thư mục
        await t.Do("offline.nsis", async () =>
        {
            var dir = Path.Combine(Downloader.CacheDir, "selftest-offline");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var url = await Downloader.ResolveAsync(@"github:notepad-plus-plus/notepad-plus-plus|Installer\.x64\.exe$");
            await Downloader.DownloadAsync(url, dir);
            var files = PostInstallService.ScanFolder(dir);
            Check(files.Count == 1, $"quét thư mục thấy {files.Count} file");
            var type = InstallerRunner.Detect(files[0]);
            Check(type.Kind == "nsis", $"nhận nhầm kiểu bộ cài: {type.Label}");
            var r = await InstallerRunner.RunAsync(files[0], "Notepad++", silent: true);
            Check(r.Ok, r.Message);
            var app = UninstallService.ReadApps().FirstOrDefault(a => a.Name.StartsWith("Notepad++"));
            Check(app is not null, "không thấy Notepad++ trong danh sách đã cài");
            var u = await UninstallService.RunUninstallerAsync(app!, quiet: true);
            for (var i = 0; i < 30 && UninstallService.StillInstalled(app!); i++) await Task.Delay(1000);
            Check(!UninstallService.StillInstalled(app!), "gỡ chưa xong");
            return $"{Path.GetFileName(files[0])} · {type.Label} · {r.Message} · gỡ: {u.Message}";
        });

        await t.Do("netfx3.state", async () =>
        {
            var s = await PostInstallService.NetFx3EnabledAsync();
            Check(s is not null, "không đọc được trạng thái");
            return s == true ? "đã bật" : "chưa bật";
        });

        await t.Do("postinstall.list", async () =>
        {
            var f = Path.Combine(Path.GetTempPath(), "ithd-list.json");
            PostInstallService.Save(f, new PostInstallList { Apps = ["Google.Chrome", "zalo"], NetFx3 = true });
            var l = PostInstallService.Load(f);
            File.Delete(f);
            Check(l.Apps.SequenceEqual(["Google.Chrome", "zalo"]) && l.NetFx3 && !l.UpgradeAll, "đọc lại sai");
            await Task.CompletedTask;
            return "lưu / mở ok";
        });

        return await t.ToFileAsync(path);
    }
}
