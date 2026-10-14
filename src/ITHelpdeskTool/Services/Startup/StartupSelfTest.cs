using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Startup;

/// <summary>
/// Tạo mục khởi động test ở mọi nguồn, bật/tắt/xoá rồi đọc lại; thử tác vụ, dịch vụ, menu boot.
/// ITHelpdeskTool.exe --selftest startup kết_quả.json   (chỉ chạy trên máy test/CI)
/// </summary>
public static class StartupSelfTest
{
    public static async Task<List<Step>> RunAsync()
    {
        var steps = new List<Step>();
        async Task Do(string name, Func<Task<string>> body)
        {
            var sw = Stopwatch.StartNew();
            try { steps.Add(new Step(name, true, await body(), sw.ElapsedMilliseconds)); }
            catch (Exception ex) { steps.Add(new Step(name, false, ex.Message, sw.ElapsedMilliseconds)); }
        }
        static void Check(bool cond, string msg) { if (!cond) throw new InvalidOperationException(msg); }
        static string Need(OpResult r) => r.Ok ? r.Message : throw new InvalidOperationException(r.Message);
        static StartupEntry? Find(string raw, StartupSource src) =>
            StartupService.ReadAll().FirstOrDefault(e => e.RawName == raw && e.Source == src);

        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var sysDir = Environment.SystemDirectory;

        // ---------- Đọc ----------
        await Do("read.apps", () => Task.Run(() =>
        {
            var all = StartupService.ReadAll();
            return $"{all.Count} mục: " + string.Join(" | ", all.Select(e =>
                $"{e.Name} [{e.Source}] on={e.Enabled} exists={e.FileExists} pub={e.Publisher} icon={(e.Icon is null ? "no" : "yes")}"));
        }));

        await Do("parse.command", () => Task.Run(() =>
        {
            var cases = new (string Cmd, string Expect)[]
            {
                ($"\"{notepad}\" /min", notepad),
                ($"{notepad} --flag", notepad),
                (@"%windir%\system32\notepad.exe", notepad),
                (@"C:\Program Files\Some App\app.exe -silent", @"C:\Program Files\Some App\app.exe"),
                ("rundll32.exe shell32.dll,Control_RunDLL", Path.Combine(sysDir, "shell32.dll")),
                ("notepad.exe", notepad),
            };
            var bad = cases.Where(c => !StartupService.ExtractExe(c.Cmd).Equals(c.Expect, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Cmd} → {StartupService.ExtractExe(c.Cmd)}").ToList();
            Check(bad.Count == 0, string.Join(" ; ", bad));
            return $"{cases.Length} mẫu đúng";
        }));

        // ---------- Registry: tài khoản này ----------
        await Do("user.add", () => Task.Run(() =>
        {
            var m = Need(StartupService.Add("HDTestUser", notepad, "/x", allUsers: false));
            var e = Find("HDTestUser", StartupSource.RunUser);
            Check(e is { Enabled: true, FileExists: true }, $"đọc lại: {e}");
            Check(e!.Publisher.Contains("Microsoft"), $"publisher = {e.Publisher}");
            return $"{m} · cmd={e.Command}";
        }));
        await Do("user.add.duplicate", () => Task.Run(() =>
        {
            var r = StartupService.Add("HDTestUser", notepad, "", false);
            Check(!r.Ok, "thêm trùng phải lỗi");
            var r2 = StartupService.Add("HDMissing", @"C:\khong\co\file.exe", "", false);
            Check(!r2.Ok, "file không tồn tại phải lỗi");
            return $"{r.Message} | {r2.Message}";
        }));
        await Do("user.toggle", () => Task.Run(() =>
        {
            Need(StartupService.SetEnabled(Find("HDTestUser", StartupSource.RunUser)!, false));
            Check(Find("HDTestUser", StartupSource.RunUser)?.Enabled == false, "chưa tắt");
            using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
                Check(k?.GetValue("HDTestUser") is byte[] { Length: 12 } b && b[0] == 3, "giá trị StartupApproved sai");
            Need(StartupService.SetEnabled(Find("HDTestUser", StartupSource.RunUser)!, true));
            Check(Find("HDTestUser", StartupSource.RunUser)?.Enabled == true, "chưa bật lại");
            return "on → off (byte 03) → on";
        }));

        // ---------- Registry: mọi tài khoản (64 và 32-bit) ----------
        await Do("machine.add.toggle", () => Task.Run(() =>
        {
            Need(StartupService.Add("HDTestMachine", notepad, "", allUsers: true));
            Need(StartupService.SetEnabled(Find("HDTestMachine", StartupSource.RunMachine)!, false));
            Check(Find("HDTestMachine", StartupSource.RunMachine)?.Enabled == false, "chưa tắt");
            Need(StartupService.SetEnabled(Find("HDTestMachine", StartupSource.RunMachine)!, true));
            return "HKLM Run: thêm → tắt → bật";
        }));
        await Do("machine32.read.toggle", () => Task.Run(() =>
        {
            var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SysWOW64\notepad.exe");
            using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                       .CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true))
                k.SetValue("HDTest32", $"\"{wow}\"");
            var e = Find("HDTest32", StartupSource.RunMachine32);
            Check(e is { Enabled: true }, "không thấy mục 32-bit");
            Need(StartupService.SetEnabled(e!, false));
            using (var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"))
                Check(a?.GetValue("HDTest32") is byte[] b && b[0] == 3, "Run32 approved sai");
            Check(Find("HDTest32", StartupSource.RunMachine32)?.Enabled == false, "chưa tắt");
            return $"WOW6432Node: {e!.FilePath} → tắt OK";
        }));

        // ---------- Thư mục Startup ----------
        await Do("folder.user.bat", () => Task.Run(() =>
        {
            Directory.CreateDirectory(StartupService.UserFolder);
            var f = Path.Combine(StartupService.UserFolder, "HDTest.bat");
            File.WriteAllText(f, "@echo off\r\n");
            var e = Find("HDTest.bat", StartupSource.FolderUser);
            Check(e is { Enabled: true, Name: "HDTest" }, $"đọc lại: {e}");
            Need(StartupService.SetEnabled(e!, false));
            Check(Find("HDTest.bat", StartupSource.FolderUser)?.Enabled == false, "chưa tắt");
            return "file .bat trong shell:startup → tắt OK";
        }));
        await Do("folder.common.lnk", () => Task.Run(() =>
        {
            Directory.CreateDirectory(StartupService.CommonFolder);
            var lnk = Path.Combine(StartupService.CommonFolder, "HDTest Notepad.lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnk);
            sc.TargetPath = notepad;
            sc.Save();
            var e = Find("HDTest Notepad.lnk", StartupSource.FolderCommon);
            Check(e is not null, "không thấy shortcut");
            Check(e!.FilePath.Equals(notepad, StringComparison.OrdinalIgnoreCase), $"đích shortcut = {e.FilePath}");
            Check(e.Publisher.Contains("Microsoft"), $"publisher = {e.Publisher}");
            return $"shortcut → {e.FilePath} · {e.Publisher}";
        }));

        // ---------- Xoá (có sao lưu) ----------
        foreach (var (raw, src) in new[]
                 {
                     ("HDTestUser", StartupSource.RunUser), ("HDTestMachine", StartupSource.RunMachine),
                     ("HDTest32", StartupSource.RunMachine32), ("HDTest.bat", StartupSource.FolderUser),
                     ("HDTest Notepad.lnk", StartupSource.FolderCommon),
                 })
        {
            await Do($"delete.{src}", () => Task.Run(() =>
            {
                var e = Find(raw, src) ?? throw new InvalidOperationException("không thấy để xoá");
                var before = Directory.Exists(StartupService.BackupFolder) ? Directory.GetFiles(StartupService.BackupFolder) : [];
                var m = Need(StartupService.Delete(e));
                Check(Find(raw, src) is null, "vẫn còn sau khi xoá");
                var added = Directory.GetFiles(StartupService.BackupFolder).Except(before).ToList();
                Check(added.Count == 1, $"có {added.Count} file sao lưu mới");
                var file = added[0];
                Check(file.EndsWith(".reg") ? File.ReadAllText(file).Contains(raw) : file.EndsWith(raw), $"sao lưu sai: {Path.GetFileName(file)}");
                return $"{m}";
            }));
        }

        // ---------- Tác vụ lập lịch ----------
        await Do("task.create", async () =>
        {
            var r = await ProcessRunner.PowerShellAsync(
                "$a = New-ScheduledTaskAction -Execute 'notepad.exe'; $t = New-ScheduledTaskTrigger -AtLogOn; " +
                "Register-ScheduledTask -TaskName 'HDTestTask' -TaskPath '\\HDTest\\' -Action $a -Trigger $t -Force | Out-Null");
            Check(r.Success, r.Text);
            var t = (await AutoRunService.ReadTasksAsync()).FirstOrDefault(x => x.Name == "HDTestTask");
            Check(t is { Enabled: true }, "không thấy tác vụ test");
            return $"{t!.FullName} · {t.Trigger} · {t.Action}";
        });
        await Do("task.toggle", async () =>
        {
            var t = (await AutoRunService.ReadTasksAsync()).First(x => x.Name == "HDTestTask");
            Need(await AutoRunService.SetTaskEnabledAsync(t, false));
            Check((await AutoRunService.ReadTasksAsync()).First(x => x.Name == "HDTestTask").Enabled == false, "chưa tắt");
            Need(await AutoRunService.SetTaskEnabledAsync(t, true));
            Check((await AutoRunService.ReadTasksAsync()).First(x => x.Name == "HDTestTask").Enabled, "chưa bật lại");
            return "on → off → on";
        });
        await Do("task.list", async () =>
        {
            var all = await AutoRunService.ReadTasksAsync();
            Check(all.All(t => !t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)), "lẫn tác vụ của Microsoft");
            return string.Join(" | ", all.Select(t => $"{t.FullName} on={t.Enabled}"));
        });

        // ---------- Dịch vụ hãng thứ ba (dùng chính file exe của app, publisher ngthhung) ----------
        var exe = Environment.ProcessPath ?? "";
        await Do("service.create", async () =>
        {
            var r = await ProcessRunner.ConsoleAsync("sc.exe", $"create HDTestSvc binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"HD Test Service\"");
            Check(r.Success, r.Text);
            var s = (await Task.Run(AutoRunService.ReadServices)).FirstOrDefault(x => x.Name == "HDTestSvc");
            Check(s is { Auto: true }, $"không thấy dịch vụ test (exe {exe})");
            return $"{s!.DisplayName} · {s.Publisher} · {s.StateText}";
        });
        await Do("service.toggle", async () =>
        {
            var s = (await Task.Run(AutoRunService.ReadServices)).First(x => x.Name == "HDTestSvc");
            Need(await AutoRunService.SetServiceAutoAsync(s, false));
            Check((await Task.Run(AutoRunService.ReadServices)).First(x => x.Name == "HDTestSvc").Auto == false, "chưa sang Manual");
            Need(await AutoRunService.SetServiceAutoAsync(s, true));
            Check((await Task.Run(AutoRunService.ReadServices)).First(x => x.Name == "HDTestSvc").Auto, "chưa về Auto");
            return "Auto → Manual → Auto";
        });
        await Do("service.list", async () =>
        {
            var all = await Task.Run(AutoRunService.ReadServices);
            Check(all.All(s => !s.Publisher.Contains("Microsoft")), "lẫn dịch vụ Microsoft");
            return $"{all.Count} dịch vụ: " + string.Join(" | ", all.Take(12).Select(s => $"{s.DisplayName} ({s.Publisher})"));
        });

        // ---------- Menu khởi động ----------
        await Do("boot.read", () => Task.Run(() =>
        {
            var c = BootService.ReadConfig();
            Check(c.Entries.Count > 0, "không đọc được mục nào");
            Check(c.Entries.Any(e => e.IsCurrent), "không nhận ra mục đang chạy");
            return $"uefi={c.IsUefi} timeout={c.TimeoutSeconds} safe='{c.SafeBoot}' · " +
                   string.Join(" | ", c.Entries.Select(e => $"{e.Description}{(e.IsDefault ? " *" : "")}{(e.IsCurrent ? " (cur)" : "")}"));
        }));
        await Do("boot.timeout", async () =>
        {
            var orig = BootService.ReadConfig().TimeoutSeconds;
            var test = orig == 7 ? 8 : 7;
            Need(await BootService.SetTimeoutAsync(test));
            var mid = BootService.ReadConfig().TimeoutSeconds;
            Need(await BootService.SetTimeoutAsync(orig));
            Check(mid == test, $"đặt {test} nhưng đọc lại {mid}");
            return $"{orig} → {mid} → {BootService.ReadConfig().TimeoutSeconds}";
        });
        await Do("boot.default", async () =>
        {
            var def = BootService.ReadConfig().Entries.First(e => e.IsDefault);
            Need(await BootService.SetDefaultAsync(def));
            Check(BootService.ReadConfig().Entries.First(e => e.IsDefault).Id == def.Id, "mặc định bị đổi");
            return def.Description;
        });
        await Do("boot.safemode", async () =>
        {
            Need(await BootService.SetSafeBootAsync("network"));
            var on = BootService.ReadConfig().SafeBoot;
            Need(await BootService.SetSafeBootAsync(""));
            var off = BootService.ReadConfig().SafeBoot;
            Check(on == "network" && off == "", $"đọc lại: bật='{on}' tắt='{off}'");
            Need(await BootService.SetSafeBootAsync(""));   // tắt lần nữa khi vốn đã tắt vẫn phải OK
            return "network → bình thường";
        });
        await Do("boot.times", async () =>
        {
            var t = await BootService.ReadBootTimesAsync();
            return t.Count == 0 ? "không có số liệu (bình thường trên Windows Server)" : string.Join(", ", t.Select(x => $"{x.Seconds:0.0}s"));
        });
        await Do("schedule.cancel", () => Task.Run(() =>
        {
            Need(BootService.Schedule(restart: true, minutes: 120));
            var m = Need(BootService.CancelSchedule());
            var again = BootService.CancelSchedule();
            Check(!again.Ok, "huỷ lần hai phải báo không có hẹn giờ");
            return m;
        }));

        // ---------- Dọn ----------
        await Do("cleanup", async () =>
        {
            await ProcessRunner.PowerShellAsync("Unregister-ScheduledTask -TaskName 'HDTestTask' -TaskPath '\\HDTest\\' -Confirm:$false");
            await ProcessRunner.ConsoleAsync("sc.exe", "delete HDTestSvc");
            var left = StartupService.ReadAll().Where(e => e.RawName.StartsWith("HDTest")).Select(e => e.RawName).ToList();
            Check(left.Count == 0, "còn sót: " + string.Join(", ", left));
            return "đã dọn task, service";
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
