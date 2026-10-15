using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.SysConfig.SysConfigSelfTest;

namespace ITHelpdeskTool.Services.Policy;

/// <summary>
/// Bật/tắt thật từng policy rồi đọc lại (chạy trên máy test/CI).
/// ITHelpdeskTool.exe --selftest policy kết_quả.json
/// </summary>
public static class PolicySelfTest
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
        static bool NoTempHive() => !Registry.Users.GetSubKeyNames().Any(n => n.StartsWith("HD_", StringComparison.OrdinalIgnoreCase));

        await Do("info", async () =>
        {
            var users = await PolicyService.ReadUsersAsync();
            return $"home={PolicyService.IsHomeEdition()} gpedit={PolicyService.HasGpedit()} · users: " +
                   string.Join(", ", users.Select(u => u.Display)) + $" · catalog={PolicyCatalog.All.Count}";
        });

        // ---------- Policy cho máy: bật → đọc lại → tắt → đọc lại → trả như cũ ----------
        foreach (var p in PolicyCatalog.All.Where(p => p.Scope == PolicyScope.Machine))
        {
            await Do($"machine.{p.Key}", () => Task.Run(() =>
            {
                var orig = PolicyService.ReadStates(null)[p.Key];
                Need(PolicyService.Apply(p, true, null));
                Check(PolicyService.ReadStates(null)[p.Key], "bật xong đọc lại vẫn tắt");
                Need(PolicyService.Apply(p, false, null));
                Check(!PolicyService.ReadStates(null)[p.Key], "tắt xong đọc lại vẫn bật");
                if (orig) PolicyService.Apply(p, true, null);
                return $"{orig} → on → off{(orig ? " → on" : "")}";
            }));
        }

        // Bật "Chặn cài đặt ứng dụng" thì admin vẫn cài được qua winget (trang App Store của app)
        await Do("noinstall.winget", async () =>
        {
            var p = PolicyCatalog.All.First(x => x.Key == "user.noinstall");
            if (Store.WingetService.Locate() is null) return "không có winget — bỏ qua";
            Need(PolicyService.Apply(p, true, null));
            try
            {
                var r = await Store.WingetService.InstallAsync("Notepad++.Notepad++", "Notepad++");
                Check(r.Ok, "winget không cài được khi bật chặn: " + r.Message);
                var u = await Store.WingetService.UninstallAsync("Notepad++.Notepad++", "Notepad++");
                return $"{r.Message} · {u.Message}";
            }
            finally { PolicyService.Apply(p, false, null); }
        });

        // Giá trị động (giữ phiên bản Windows) phải ghi đúng bản đang chạy
        await Do("machine.pinversion.values", () => Task.Run(() =>
        {
            var p = PolicyCatalog.All.First(x => x.Key == "wu.pinversion");
            Need(PolicyService.Apply(p, true, null));
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            var s = $"{k?.GetValue("ProductVersion")} / {k?.GetValue("TargetReleaseVersionInfo")}";
            PolicyService.Apply(p, false, null);
            Check(s.Contains("Windows 1"), $"ProductVersion sai: {s}");
            return s;
        }));

        // USB bị chặn rồi bỏ chặn phải trả Start = 3 (không xoá giá trị)
        await Do("machine.usbstor.restore", () => Task.Run(() =>
        {
            var p = PolicyCatalog.All.First(x => x.Key == "dev.usbblock");
            Need(PolicyService.Apply(p, true, null));
            Need(PolicyService.Apply(p, false, null));
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR");
            Check(k?.GetValue("Start") is int v && v == 3, $"Start = {k?.GetValue("Start")}");
            return "Start = 3";
        }));

        // ---------- Policy người dùng: tài khoản khác, chưa đăng nhập bao giờ ----------
        const string tu = "hdpol1";
        PolicyUser? target = null;
        await Do("user.prepare", async () =>
        {
            var c = await SysConfigService.CreateUserAsync(tu, "Aa!23456789xyz", admin: false);
            Check(c.Ok || c.Message.Contains("tồn tại"), c.Message);
            target = (await PolicyService.ReadUsersAsync()).First(u => u.Name == tu);
            var before = target.HasProfile;
            var r = PolicyService.EnsureProfile(target);
            target = (await PolicyService.ReadUsersAsync()).First(u => u.Name == tu);
            Check(target.HasProfile, $"chưa có hồ sơ: {r.Message}");
            return $"có hồ sơ trước đó={before} · {r.Message}";
        });

        foreach (var p in PolicyCatalog.All.Where(p => p.Scope == PolicyScope.User))
        {
            await Do($"user.{p.Key}", () => Task.Run(() =>
            {
                Check(target is not null, "chưa chuẩn bị được tài khoản test");
                Need(PolicyService.Apply(p, true, target));
                Check(PolicyService.ReadStates(target!.Sid)[p.Key], "bật cho hdpol1 xong đọc lại vẫn tắt");
                Need(PolicyService.Apply(p, false, target));
                Check(!PolicyService.ReadStates(target.Sid)[p.Key], "tắt xong đọc lại vẫn bật");
                Check(NoTempHive(), "hive tạm chưa được gỡ");
                return "on → off (hive nạp/gỡ tạm OK)";
            }));
        }

        // Áp cho hdpol1 không được ảnh hưởng tài khoản đang dùng
        await Do("user.isolation", () => Task.Run(() =>
        {
            Check(target is not null, "chưa có tài khoản test");
            var p = PolicyCatalog.All.First(x => x.Key == "user.taskmgr");
            Need(PolicyService.Apply(p, true, target));
            using var me = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var leaked = me?.GetValue("DisableTaskMgr") is int v && v == 1;
            PolicyService.Apply(p, false, target);
            Check(!leaked, "policy của hdpol1 bị ghi nhầm sang tài khoản đang dùng");
            return "không lan sang tài khoản đang dùng";
        }));

        // Tài khoản đang đăng nhập (hive đã nạp sẵn ở HKU\SID)
        await Do("user.current", async () =>
        {
            var me = (await PolicyService.ReadUsersAsync()).First(u => u.IsCurrent);
            var p = PolicyCatalog.All.First(x => x.Key == "user.wallpaper");
            Need(PolicyService.Apply(p, true, me));
            using (var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"))
                Check(k?.GetValue("NoChangingWallPaper") is int v && v == 1, "không thấy trong HKCU");
            Need(PolicyService.Apply(p, false, me));
            return $"{me.Name}: on → off qua HKU\\SID";
        });

        // ---------- Gỡ tất cả ----------
        await Do("resetall", () => Task.Run(() =>
        {
            foreach (var k in new[] { "wu.noreboot", "dev.noautorun", "app.copilot", "user.run" })
                Need(PolicyService.Apply(PolicyCatalog.All.First(x => x.Key == k), true, target));
            var m = Need(PolicyService.ResetAll(target));
            var states = PolicyService.ReadStates(target!.Sid);
            var still = states.Where(s => s.Value).Select(s => s.Key).ToList();
            Check(still.Count == 0, $"còn bật: {string.Join(", ", still)}");
            return m;
        }));

        // ---------- Mật khẩu ----------
        await Do("password", async () =>
        {
            var o = await PolicyService.ReadPasswordPolicyAsync();
            Need(await PolicyService.SetPasswordPolicyAsync(8, 90, 5, 15));
            var n = await PolicyService.ReadPasswordPolicyAsync();
            Check(n.MinLength == 8 && n.MaxAgeDays == 90 && n.LockoutThreshold == 5 && n.LockoutMinutes == 15, $"đọc lại: {n}");
            Need(await PolicyService.SetPasswordPolicyAsync(o.MinLength, o.MaxAgeDays, o.LockoutThreshold, o.LockoutMinutes > 0 ? o.LockoutMinutes : 30));
            var back = await PolicyService.ReadPasswordPolicyAsync();
            return $"gốc {o} → {n} → {back}";
        });
        await Do("password.validation", async () =>
        {
            var r = await PolicyService.SetPasswordPolicyAsync(20, 0, 0, 15);
            Check(!r.Ok, "độ dài 20 phải bị chặn");
            return r.Message;
        });

        // ---------- Công cụ ----------
        await Do("gpupdate", async () => Need(await PolicyService.GpUpdateAsync()));
        await Do("gpresult", async () =>
        {
            var (r, path) = await PolicyService.GpResultAsync();
            Check(r.Ok && path is not null && new FileInfo(path).Length > 1000, r.Message);
            return $"{r.Message} ({new FileInfo(path!).Length / 1024} KB)";
        });

        // ===== Chỉ cho dùng USB được duyệt (thử bằng 2 ổ ảo VHDX coi như USB) =====
        await Do("usb.whitelist", async () =>
        {
            UsbWhitelistService.AllowVirtual = true;
            var dir = Path.Combine(Path.GetTempPath(), "ithd-usbwl");
            Directory.CreateDirectory(dir);
            var a = Path.Combine(dir, "a.vhdx"); var b = Path.Combine(dir, "b.vhdx"); var c = Path.Combine(dir, "c.vhdx");
            foreach (var f in new[] { a, b, c }) if (File.Exists(f)) File.Delete(f);
            async Task<ProcessResult> Diskpart(string script, int seconds = 120)
            {
                var dp = Path.Combine(dir, "dp.txt");
                await File.WriteAllTextAsync(dp, script);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                try { return await ProcessRunner.ConsoleAsync("diskpart.exe", $"/s \"{dp}\"", cts.Token); }
                catch (OperationCanceledException) { return new ProcessResult(-1, "", "hết giờ"); }
            }
            var log = new List<string>();
            try
            {
                await Diskpart($"create vdisk file=\"{a}\" maximum=256 type=expandable\r\nselect vdisk file=\"{a}\"\r\nattach vdisk\r\ncreate partition primary\r\nformat fs=ntfs quick label=USBOK\r\nassign\r\n");
                await Diskpart($"create vdisk file=\"{b}\" maximum=256 type=expandable\r\n");
                await Task.Delay(3000);
                var before = await UsbWhitelistService.PresentAsync();
                Check(before.Count == 1, $"phải thấy 1 ổ ảo, thấy {before.Count}");
                var okDisk = before[0];
                log.Add($"ổ A {okDisk.InstanceId} {okDisk.Letters}");

                Need(await UsbWhitelistService.EnableAsync([okDisk.InstanceId]));
                Check(UsbWhitelistService.IsEnabled(), "chưa bật");
                Check(UsbWhitelistService.AllowedIds().Contains(okDisk.InstanceId, StringComparer.OrdinalIgnoreCase), "thiếu ổ A trong danh sách phép");

                // Cắm ổ lạ B → không được nhận
                var r = await Diskpart($"select vdisk file=\"{b}\"\r\nattach vdisk\r\n", 90);
                await Task.Delay(8000);
                var during = await UsbWhitelistService.PresentAsync();
                log.Add($"khi bật: {during.Count} ổ ({string.Join(", ", during.Select(d => d.InstanceId))}) · attach B: {OpResult.FirstLine(r.Text)}");
                log.Add($"bị chặn: {string.Join(", ", await UsbWhitelistService.BlockedDiskIdsAsync())}");
                Check(during.Count == 1 && during[0].InstanceId.Equals(okDisk.InstanceId, StringComparison.OrdinalIgnoreCase), "ổ lạ B vẫn được nhận");

                // Ổ A vẫn ghi được
                var letter = okDisk.Letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Check(letter is not null, "ổ A không có ký tự");
                var tf = Path.Combine(letter! + "\\", "ok.txt");
                await File.WriteAllTextAsync(tf, "ok");
                Check(File.ReadAllText(tf) == "ok", "không ghi được vào ổ A");

                // Tắt → ổ B được nhận lại
                Need(await UsbWhitelistService.DisableAsync());
                Check(!UsbWhitelistService.IsEnabled() && UsbWhitelistService.AllowedIds().Count == 0, "chưa tắt sạch");
                // Cắm ổ mới C (ổ B bị kẹt ở trạng thái mở dở sau khi diskpart chờ quá giờ — chỉ là chuyện của ổ ảo)
                var re = await Diskpart($"create vdisk file=\"{c}\" maximum=256 type=expandable\r\nselect vdisk file=\"{c}\"\r\nattach vdisk\r\n", 90);
                log.Add("gắn C: " + string.Join(" / ", re.Text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(2)));
                using (var rk = Registry.LocalMachine.OpenSubKey(UsbWhitelistService.Key))
                    log.Add("reg sau tắt: " + (rk is null ? "không có" : string.Join(",", rk.GetValueNames())));
                var after = new List<UsbStorage>();
                for (var i = 0; i < 20 && !after.Any(x => !x.InstanceId.Equals(okDisk.InstanceId, StringComparison.OrdinalIgnoreCase)); i++)
                {
                    await Task.Delay(1500);
                    if (i == 6) await ProcessRunner.ConsoleAsync("pnputil.exe", "/scan-devices");
                    after = await UsbWhitelistService.PresentAsync();
                }
                log.Add($"sau khi tắt: {after.Count} ổ");
                var newOk = after.Any(x => !x.InstanceId.Equals(okDisk.InstanceId, StringComparison.OrdinalIgnoreCase));
                if (!newOk)
                {
                    var dbg = await ProcessRunner.PowerShellAsync("Get-PnpDevice | Where-Object { $_.InstanceId -like '*VIRTUAL_DISK*' -or $_.InstanceId -like 'SCSI\\DISK*' } | ForEach-Object { \"$($_.InstanceId) [$($_.Class)] $($_.Status) present=$($_.Present)\" }; Get-DiskImage -ImagePath $env:ITHD_B | Format-List Attached,Number | Out-String",
                        env: new Dictionary<string, string> { ["ITHD_B"] = b });
                    log.Add("pnp: " + dbg.Text.Replace("\r", "").Replace("\n", " | "));
                }
                Check(newOk, $"tắt rồi mà ổ mới cắm vẫn không được nhận ({after.Count} ổ) · {string.Join(" · ", log)}");
                return string.Join(" · ", log);
            }
            finally
            {
                if (UsbWhitelistService.IsEnabled()) await UsbWhitelistService.DisableAsync();
                await Diskpart($"select vdisk file=\"{a}\"\r\ndetach vdisk\r\nselect vdisk file=\"{b}\"\r\ndetach vdisk\r\nselect vdisk file=\"{c}\"\r\ndetach vdisk\r\n", 60);
                try { Directory.Delete(dir, true); } catch { }
                UsbWhitelistService.AllowVirtual = false;
            }
        });

        // Dọn tài khoản test
        await Do("cleanup", async () =>
        {
            var all = await SysConfigService.ReadUsersAsync();
            var u = all.FirstOrDefault(x => x.Name == tu);
            return u is null ? "không có gì để dọn" : Need(await SysConfigService.DeleteUserAsync(u, all));
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
