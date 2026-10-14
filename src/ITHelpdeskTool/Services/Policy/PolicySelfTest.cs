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
