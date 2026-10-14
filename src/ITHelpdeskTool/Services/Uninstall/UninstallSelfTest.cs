using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.Startup;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Uninstall;

/// <summary>Cài một phần mềm giả rải rác khắp máy, gỡ, quét phần sót, dọn và kiểm tra. --selftest uninstall</summary>
public static class UninstallSelfTest
{
    private const string Key = "HDTestApp";
    private const string AppName = "HD Test App";
    private const string Pub = "HDTest Corp";

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
        static async Task Run(string file, string args)
        {
            var r = await ProcessRunner.ConsoleAsync(file, args);
            if (!r.Success) throw new InvalidOperationException($"{file} {args}: {OpResult.FirstLine(r.Text)}");
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pubDir = Path.Combine(pf, Pub);
        var loc = Path.Combine(pubDir, AppName);
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        var localPub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Pub);
        var local = Path.Combine(localPub, AppName);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
        var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "HD Test.lnk");
        var exe = Path.Combine(loc, "hdapp.exe");

        await Do("parse.names", () =>
        {
            Check(UninstallService.CleanName("HD Test App 1.2.3 (x64)") == "HD Test App", UninstallService.CleanName("HD Test App 1.2.3 (x64)"));
            Check(UninstallService.CleanName("Foo Editor v2.0 build 77") == "Foo Editor", UninstallService.CleanName("Foo Editor v2.0 build 77"));
            Check(UninstallService.CleanName("Bar Suite 2024") == "Bar Suite", UninstallService.CleanName("Bar Suite 2024"));
            var p = UninstallService.PublisherNames("HDTest Corp, Inc.");
            Check(p.Contains("HDTest Corp") && p.Contains("HDTest"), string.Join(",", p));
            Check(UninstallService.CleanPublisher("Mozilla Corporation") == "Mozilla", UninstallService.CleanPublisher("Mozilla Corporation"));
            return string.Join(" / ", p);
        });

        await Do("parse.commands", () =>
        {
            const string guid = "{12345678-ABCD-ABCD-ABCD-1234567890AB}";
            var msi = Fake("MsiExec.exe /I" + guid);
            Check(msi.IsMsi && msi.ProductCode == guid, "không nhận ra MSI");
            var (f1, a1, s1) = UninstallService.BuildCommand(msi, true);
            Check(f1 == "msiexec.exe" && a1 == $"/x {guid} /qn /norestart" && s1, $"{f1} {a1}");
            var (_, a1b, s1b) = UninstallService.BuildCommand(msi, false);
            Check(a1b == $"/x {guid}" && !s1b, a1b);

            var inno = Fake(@"""C:\Program Files\Foo\unins000.exe""");
            var (f2, a2, s2) = UninstallService.BuildCommand(inno, true);
            Check(f2 == @"C:\Program Files\Foo\unins000.exe" && a2.Contains("/VERYSILENT") && s2, $"{f2} {a2}");

            var q = Fake(@"""C:\X\un.exe"" /remove") with { QuietUninstallString = @"""C:\X\un.exe"" /remove /silent" };
            var (f3, a3, s3) = UninstallService.BuildCommand(q, true);
            Check(f3 == @"C:\X\un.exe" && a3 == "/remove /silent" && s3, $"{f3} {a3}");
            var (_, a4, s4) = UninstallService.BuildCommand(q, false);
            Check(a4 == "/remove" && !s4, a4);
            return "msi, inno, quiet string ok";
        });

        await Do("guard.protected", async () =>
        {
            // Dữ liệu registry sai: InstallLocation = Program Files, tên trùng thư mục hệ thống
            var bad = Fake("") with { Name = "Windows", Publisher = "Microsoft Corporation", InstallLocation = pf };
            var items = await UninstallService.FindLeftoversAsync(bad, []);
            var protectedHit = items.Where(i => i.Kind == LeftoverKind.Folder).Select(i => i.Path).ToList();
            Check(protectedHit.Count == 0, "đề xuất xoá thư mục hệ thống: " + string.Join(", ", protectedHit));
            return $"không đề xuất thư mục hệ thống ({items.Count} mục khác)";
        });

        try
        {
            await Do("setup.fake.app", async () =>
            {
                await CleanupAsync();
                Directory.CreateDirectory(loc);
                Directory.CreateDirectory(Path.GetDirectoryName(desktopLnk)!);
                File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), exe, true);
                File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(loc, "hdsvc.exe"), true);
                File.WriteAllBytes(Path.Combine(loc, "data.bin"), new byte[200 * 1024]);
                // Trình gỡ giả: chỉ xoá mục trong danh sách cài đặt + exe chính, để lại rác như nhiều phần mềm thật
                File.WriteAllText(Path.Combine(loc, "uninstall.cmd"),
                    $"@echo off\r\nreg delete \"HKLM\\{UninstallService.UninstallKey}\\{Key}\" /f >nul\r\ndel /f /q \"{exe}\"\r\nexit /b 0\r\n");
                using (var k = Registry.LocalMachine.CreateSubKey($@"{UninstallService.UninstallKey}\{Key}"))
                {
                    k.SetValue("DisplayName", AppName + " 1.2.3");
                    k.SetValue("DisplayVersion", "1.2.3");
                    k.SetValue("Publisher", Pub + ", Inc.");
                    k.SetValue("InstallLocation", loc);
                    k.SetValue("UninstallString", $"\"{Path.Combine(loc, "uninstall.cmd")}\"");
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("EstimatedSize", 300, RegistryValueKind.DWord);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
                Directory.CreateDirectory(roaming);
                File.WriteAllText(Path.Combine(roaming, "settings.ini"), "[x]\r\ny=1\r\n");
                Directory.CreateDirectory(local);
                File.WriteAllBytes(Path.Combine(local, "cache.bin"), new byte[100 * 1024]);
                using (var k = Registry.CurrentUser.CreateSubKey($@"Software\{Pub}\{AppName}")) k.SetValue("Theme", "dark");
                using (var k = Registry.LocalMachine.CreateSubKey($@"Software\{AppName}")) k.SetValue("Path", loc);
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) k.SetValue("HDTestTray", $"\"{exe}\" /tray");
                Directory.CreateDirectory(startMenu);
                await Run("powershell.exe", "-NoProfile -Command \"$s=New-Object -ComObject WScript.Shell; " +
                    $"$l=$s.CreateShortcut('{Path.Combine(startMenu, AppName + ".lnk")}'); $l.TargetPath='{exe}'; $l.Save(); " +
                    $"$l=$s.CreateShortcut('{desktopLnk}'); $l.TargetPath='{exe}'; $l.Save()\"");
                await Run("schtasks.exe", $"/create /tn \"HDTest\\Updater\" /tr \"\\\"{exe}\\\" /update\" /sc daily /st 12:00 /f");
                await Run("sc.exe", $"create HDTestSvc binPath= \"\\\"{Path.Combine(loc, "hdsvc.exe")}\\\"\" start= demand DisplayName= \"HD Test Service\"");
                return loc;
            });

            InstalledApp? app = null;
            await Do("list.find", () =>
            {
                var all = UninstallService.ReadApps();
                app = all.FirstOrDefault(a => a.KeyName == Key);
                Check(app is not null, "không thấy trong danh sách");
                Check(app!.Name == AppName + " 1.2.3" && app.InstallLocation == loc && app.Scope == AppScope.Machine64 && app.SizeKb == 300, app.Detail);
                Check(!app.IsMsi && app.HasUninstaller, "kiểu trình gỡ sai");
                return $"{all.Count} phần mềm · {app.Detail} · icon {(app.Icon is null ? "không" : "có")}";
            });
            if (app is null) return steps;

            await Do("uninstall.run", async () =>
            {
                var r = await UninstallService.RunUninstallerAsync(app, quiet: true);
                Check(r.Ok, r.Message);
                Check(!UninstallService.StillInstalled(app), "mục cài đặt vẫn còn");
                Check(!File.Exists(exe), "trình gỡ giả chưa chạy");
                return r.Message;
            });

            List<Leftover> found = [];
            await Do("leftovers.scan", async () =>
            {
                found = await UninstallService.FindLeftoversAsync(app);
                bool Has(LeftoverKind k, Func<string, bool> p, bool? high = null) =>
                    found.Any(l => l.Kind == k && p(l.Path) && (high is null || l.HighConfidence == high));
                bool Eq(string a, string b) => a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                var missing = new List<string>();
                void Need(string what, bool ok) { if (!ok) missing.Add(what); }
                Need("thư mục cài", Has(LeftoverKind.Folder, p => Eq(p, loc), true));
                Need("thư mục hãng (tin cậy thấp)", Has(LeftoverKind.Folder, p => Eq(p, pubDir), false));
                Need("Roaming", Has(LeftoverKind.Folder, p => Eq(p, roaming), true));
                Need("Local\\hãng\\app", Has(LeftoverKind.Folder, p => Eq(p, local), true));
                Need("Local\\hãng", Has(LeftoverKind.Folder, p => Eq(p, localPub), false));
                Need("Start Menu", Has(LeftoverKind.Shortcut, p => Eq(p, startMenu)));
                Need("Desktop", Has(LeftoverKind.Shortcut, p => Eq(p, desktopLnk)));
                Need("HKCU app", Has(LeftoverKind.RegistryKey, p => Eq(p, $@"HKEY_CURRENT_USER\Software\{Pub}\{AppName}"), true));
                Need("HKCU hãng", Has(LeftoverKind.RegistryKey, p => Eq(p, $@"HKEY_CURRENT_USER\Software\{Pub}"), false));
                Need("HKLM app", Has(LeftoverKind.RegistryKey, p => Eq(p, $@"HKEY_LOCAL_MACHINE\Software\{AppName}"), true));
                Need("khởi động", Has(LeftoverKind.StartupEntry, p => p.EndsWith("|HDTestTray")));
                Need("dịch vụ", Has(LeftoverKind.Service, p => p == "HDTestSvc"));
                Need("tác vụ", Has(LeftoverKind.Task, p => p.Equals(@"\HDTest\|Updater", StringComparison.OrdinalIgnoreCase)));
                Check(!found.Any(l => l.Kind == LeftoverKind.RegistryKey && l.Path.Contains(Key)), "mục Uninstall đã bị trình gỡ xoá nhưng vẫn báo");
                Check(missing.Count == 0, "thiếu: " + string.Join(", ", missing) + " · có: " + string.Join(" | ", found.Select(f => $"{f.Kind}:{f.Path}:{f.HighConfidence}")));
                return $"{found.Count} mục, {Fmt.Size(found.Sum(f => f.Bytes))}: " + string.Join(", ", found.GroupBy(f => f.KindText).Select(g => $"{g.Key} {g.Count()}"));
            });

            await Do("leftovers.remove", async () =>
            {
                Check(found.Count > 0, "không có gì để xoá");
                var r = await UninstallService.RemoveLeftoversAsync(app, found);
                Check(r.Ok, r.Message);
                var still = new List<string>();
                foreach (var d in new[] { loc, pubDir, roaming, local, localPub, startMenu }) if (Directory.Exists(d)) still.Add(d);
                if (File.Exists(desktopLnk)) still.Add(desktopLnk);
                if (Registry.CurrentUser.OpenSubKey($@"Software\{Pub}") is { } k1) { k1.Dispose(); still.Add("HKCU hãng"); }
                if (Registry.LocalMachine.OpenSubKey($@"Software\{AppName}") is { } k2) { k2.Dispose(); still.Add("HKLM app"); }
                using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    if (run?.GetValue("HDTestTray") is not null) still.Add("Run");
                if ((await ProcessRunner.ConsoleAsync("sc.exe", "query HDTestSvc")).Success) still.Add("dịch vụ");
                if ((await ProcessRunner.ConsoleAsync("schtasks.exe", "/query /tn \"HDTest\\Updater\"")).Success) still.Add("tác vụ");
                Check(still.Count == 0, "còn: " + string.Join(", ", still));
                var backups = Directory.Exists(UninstallService.BackupRoot)
                    ? Directory.GetDirectories(UninstallService.BackupRoot).OrderByDescending(d => d).FirstOrDefault() : null;
                var regs = backups is null ? 0 : Directory.GetFiles(backups, "*.reg").Length;
                Check(regs >= 2, $"thiếu file sao lưu registry ({regs})");
                return $"{r.Message} · {regs} file .reg";
            });

            await Do("leftovers.rescan", async () =>
            {
                var again = await UninstallService.FindLeftoversAsync(app);
                Check(again.Count == 0, "vẫn còn: " + string.Join(" | ", again.Select(a => $"{a.Kind}:{a.Path}")));
                return "sạch";
            });
        }
        finally
        {
            await CleanupAsync();
        }

        await Do("appx.list", async () =>
        {
            var list = await AppxService.ReadAsync();
            return $"{list.Count} ứng dụng Store, {list.Count(a => a.IsBloat)} cài sẵn: " + string.Join(", ", list.Where(a => a.IsBloat).Take(8).Select(a => a.Display));
        });

        return steps;

        async Task CleanupAsync()
        {
            try { await ProcessRunner.ConsoleAsync("sc.exe", "delete HDTestSvc"); } catch { }
            try { await ProcessRunner.ConsoleAsync("schtasks.exe", "/delete /tn \"HDTest\\Updater\" /f"); } catch { }
            foreach (var d in new[] { pubDir, roaming, localPub, startMenu })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            try { File.Delete(desktopLnk); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{UninstallService.UninstallKey}\{Key}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\{Pub}", false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree($@"Software\{AppName}", false); } catch { }
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                run?.DeleteValue("HDTestTray", false);
            }
            catch { }
        }
    }

    private static InstalledApp Fake(string uninstall) =>
        new("X", AppScope.Machine64, "Fake", "1.0", "Fake Pub", "", 0, uninstall, "", "", "", null);

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
