using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Win32;
using S = ITHelpdeskTool.Services.SysConfig.SysConfigService;

namespace ITHelpdeskTool.Services.SysConfig;

/// <summary>
/// Chạy thật các thao tác của trang Cấu hình hệ thống rồi đọc lại để kiểm tra (dùng trên máy test, CI).
/// ITHelpdeskTool.exe --selftest sysconfig kết_quả.json
/// KHÔNG chạy trên máy thật của người dùng: có đổi tên máy, file hosts, tài khoản test.
/// </summary>
public static class SysConfigSelfTest
{
    public sealed record Step(string Name, bool Ok, string Detail, long Ms);

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

        // ---------- Đọc ----------
        await Do("read.identity", async () => { var i = await Task.Run(S.ReadIdentity); return $"{i.ComputerName} · {i.Workgroup}{i.Domain} · pending='{i.PendingName}'"; });
        await Do("read.time", async () => { var t = await Task.Run(S.ReadTime); return $"{t.TimeZoneId} · ntp={t.NtpServer} · running={t.TimeServiceRunning} · {t.Culture}"; });
        await Do("read.adapters", async () =>
        {
            var a = await Task.Run(S.ReadAdapters);
            return string.Join(" | ", a.Select(x => $"{x.Name} conn={x.Connected} dhcp={x.Dhcp} ip={x.Ip}/{x.Mask} gw={x.Gateway} dns={string.Join(",", x.Dns)} auto={x.DnsAuto}"));
        });
        await Do("read.users", async () =>
        {
            var u = await S.ReadUsersAsync();
            Check(u.Count > 0, "không có user nào");
            Check(u.Any(x => x.IsCurrent), "không nhận ra user hiện tại");
            return string.Join(", ", u.Select(x => $"{x.Name}{(x.IsAdmin ? "*" : "")}{(x.Enabled ? "" : "(off)")}{(x.IsCurrent ? "(me)" : "")}"));
        });
        await Do("read.sharing", async () => { var s = await S.ReadSharingAsync(); return s.ToString(); });
        await Do("read.system", async () => { var s = await Task.Run(S.ReadSystem); return $"{s} · hosts={S.CountHostsEntries()}"; });

        // ---------- Kiểm tra đầu vào ----------
        await Do("validate", () =>
        {
            Check(S.ValidateComputerName("KETOAN-01") is null, "KETOAN-01 phải hợp lệ");
            Check(S.ValidateComputerName("12345") is not null, "toàn số phải lỗi");
            Check(S.ValidateComputerName("may tinh") is not null, "có dấu cách phải lỗi");
            Check(S.ValidateComputerName("ABCDEFGHIJKLMNOP") is not null, "16 ký tự phải lỗi");
            Check(S.ValidateUserName("a/b") is not null, "a/b phải lỗi");
            Check(S.ValidateUserName("nhanvien1") is null, "nhanvien1 phải hợp lệ");
            Check(S.IsValidIpv4("192.168.1.10") && !S.IsValidIpv4("192.168.1") && !S.IsValidIpv4("1.2.3.256"), "kiểm tra IP sai");
            return Task.FromResult("ok");
        });

        // ---------- Tài khoản ----------
        const string u1 = "hdtest1";
        async Task<UserInfo?> Find(string n) => (await S.ReadUsersAsync()).FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        await Do("user.create", async () =>
        {
            var m = Need(await S.CreateUserAsync(u1, "Aa!23456789xyz", admin: false));
            var u = await Find(u1);
            Check(u is { Enabled: true, IsAdmin: false }, $"sau khi tạo: {u}");
            return m;
        });
        await Do("user.create.duplicate", async () =>
        {
            var r = await S.CreateUserAsync(u1, "x", false);
            Check(!r.Ok, "tạo trùng phải lỗi");
            return "lỗi đúng: " + r.Message;
        });
        await Do("user.admin.on", async () =>
        {
            var all = await S.ReadUsersAsync();
            var m = Need(await S.SetUserAdminAsync((await Find(u1))!, true, all));
            Check((await Find(u1))?.IsAdmin == true, "chưa thành admin");
            return m;
        });
        await Do("user.password", async () => Need(await S.SetPasswordAsync(u1, "N3w!Passw0rd-xyz")));
        await Do("user.password.empty", async () => Need(await S.SetPasswordAsync(u1, "")));
        await Do("user.disable", async () =>
        {
            var all = await S.ReadUsersAsync();
            var m = Need(await S.SetUserEnabledAsync((await Find(u1))!, false, all));
            Check((await Find(u1))?.Enabled == false, "chưa tắt");
            return m;
        });
        await Do("user.enable", async () =>
        {
            var all = await S.ReadUsersAsync();
            var m = Need(await S.SetUserEnabledAsync((await Find(u1))!, true, all));
            Check((await Find(u1))?.Enabled == true, "chưa bật");
            return m;
        });
        await Do("user.admin.off", async () =>
        {
            var all = await S.ReadUsersAsync();
            var m = Need(await S.SetUserAdminAsync((await Find(u1))!, false, all));
            Check((await Find(u1))?.IsAdmin == false, "vẫn là admin");
            return m;
        });
        await Do("user.guard.self", async () =>
        {
            var all = await S.ReadUsersAsync();
            var me = all.First(x => x.IsCurrent);
            var r1 = await S.DeleteUserAsync(me, all);
            var r2 = await S.SetUserEnabledAsync(me, false, all);
            Check(!r1.Ok && !r2.Ok, "phải chặn xoá/tắt chính mình");
            return $"{r1.Message} | {r2.Message}";
        });
        await Do("user.delete", async () =>
        {
            var all = await S.ReadUsersAsync();
            var m = Need(await S.DeleteUserAsync((await Find(u1))!, all));
            Check(await Find(u1) is null, "chưa xoá");
            return m;
        });
        await Do("user.create.nopassword.admin", async () =>
        {
            var m = Need(await S.CreateUserAsync("hdtest2", "", admin: true));
            var u = await Find("hdtest2");
            Check(u?.IsAdmin == true, "hdtest2 phải là admin");
            Need(await S.DeleteUserAsync(u!, await S.ReadUsersAsync()));
            return m + " (đã xoá lại)";
        });

        // ---------- Múi giờ & giờ ----------
        var tz0 = TimeZoneInfo.Local.Id;
        await Do("time.zone", async () =>
        {
            var m = Need(await S.SetTimeZoneAsync("SE Asia Standard Time"));
            TimeZoneInfo.ClearCachedData();
            Check(S.ReadTime().TimeZoneId == "SE Asia Standard Time", $"vẫn là {S.ReadTime().TimeZoneId}");
            Need(await S.SetTimeZoneAsync(tz0));
            return m + $" → trả lại {tz0}";
        });
        await Do("time.sync", async () => Need(await S.SyncTimeAsync("time.windows.com")));
        await Do("time.culture", async () =>
        {
            var c0 = S.ReadTime().Culture;
            var m = Need(await S.SetCultureAsync("vi-VN"));
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\International"))
                Check(k?.GetValue("LocaleName") as string == "vi-VN", $"LocaleName = {k?.GetValue("LocaleName")}");
            Need(await S.SetCultureAsync(c0));
            return m + $" → trả lại {c0}";
        });

        // ---------- Truy cập & chia sẻ ----------
        async Task Toggle(string name, Func<SharingInfo, bool> get, Func<bool, Task<OpResult>> set)
        {
            await Do(name, async () =>
            {
                var orig = get(await S.ReadSharingAsync());
                Need(await set(!orig));
                var after = get(await S.ReadSharingAsync());
                Check(after == !orig, $"đặt {!orig} nhưng đọc lại {after}");
                Need(await set(orig));
                Check(get(await S.ReadSharingAsync()) == orig, "không trả lại được");
                return $"{orig} → {!orig} → {orig}";
            });
        }
        await Toggle("share.rdp", s => s.RdpEnabled, S.SetRemoteDesktopAsync);
        await Toggle("share.nla", s => s.NlaRequired, S.SetNlaAsync);
        await Toggle("share.files", s => s.FileSharing, S.SetFileSharingAsync);
        await Toggle("share.discovery", s => s.NetworkDiscovery, S.SetNetworkDiscoveryAsync);

        // ---------- Hệ thống ----------
        await Do("system.hibernate", async () =>
        {
            var orig = S.ReadSystem().HibernateOn;
            var r = await S.SetHibernateAsync(!orig);
            if (!r.Ok) return "máy không hỗ trợ (đúng với VM): " + r.Message;
            var after = S.ReadSystem().HibernateOn;
            await S.SetHibernateAsync(orig);
            return $"{orig} → {after} → {S.ReadSystem().HibernateOn}";
        });
        await Do("system.faststartup.guard", async () =>
        {
            if (S.ReadSystem().HibernateOn) return "bỏ qua (hibernate đang bật)";
            var r = await S.SetFastStartupAsync(true);
            Check(!r.Ok, "phải chặn bật Fast Startup khi Hibernate tắt");
            return "chặn đúng: " + r.Message;
        });
        await Do("system.restorepoint", async () =>
        {
            var r = await S.CreateRestorePointAsync("selftest");
            Check(S.ReadSystem().RestoreSupported ? r.Ok : !r.Ok, $"kết quả không như mong đợi: {r.Message}");
            return r.Message;
        });
        await Do("system.hosts", async () =>
        {
            File.AppendAllText(S.HostsPath, "\r\n127.0.0.1 example-blocked.test\r\n");
            Check(S.CountHostsEntries() > 0, "chưa ghi thêm được dòng test");
            var m = Need(await S.ResetHostsAsync());
            Check(S.CountHostsEntries() == 0, $"còn {S.CountHostsEntries()} dòng");
            Check(Directory.GetFiles(Path.GetDirectoryName(S.HostsPath)!, "hosts.bak-*").Length > 0, "không thấy bản sao lưu");
            return m;
        });

        // ---------- Mạng ----------
        await Do("net.flushdns", async () => Need(await S.FlushDnsAsync()));
        await Do("net.dns.set-and-restore", async () =>
        {
            var a = S.ReadAdapters().FirstOrDefault(x => x.Connected && x.Ip.Length > 0)
                    ?? throw new InvalidOperationException("không có card đang kết nối");
            var dns = a.Dns.FirstOrDefault() ?? "8.8.8.8";
            Need(await S.SetDnsAsync(a.Name, dns, ""));
            var mid = S.ReadAdapters().First(x => x.Name == a.Name);
            Check(!mid.DnsAuto && mid.Dns.FirstOrDefault() == dns, $"sau khi đặt: auto={mid.DnsAuto} dns={string.Join(",", mid.Dns)}");
            if (a.DnsAuto) Need(await S.SetDnsAsync(a.Name, "", ""));
            var end = S.ReadAdapters().First(x => x.Name == a.Name);
            Check(end.DnsAuto == a.DnsAuto, "không trả lại được chế độ DNS");
            return $"{a.Name}: auto={a.DnsAuto} → tĩnh {dns} → auto={end.DnsAuto}";
        });
        await Do("net.static.validation", async () =>
        {
            var r = await S.SetStaticAsync("không-tồn-tại", "300.1.1.1", "255.255.255.0", "", "", "");
            Check(!r.Ok, "IP sai phải bị chặn");
            return r.Message;
        });

        // ---------- Tên máy & nhóm (làm cuối vì cần khởi động lại) ----------
        await Do("identity.workgroup", async () =>
        {
            var m = Need(await S.SetWorkgroupAsync("HDTESTGROUP"));
            var wg = S.ReadIdentity().Workgroup;
            Need(await S.SetWorkgroupAsync("WORKGROUP"));
            return $"{m} (đọc lại ngay: {wg})";
        });
        await Do("identity.rename", async () =>
        {
            var r = await S.RenameComputerAsync("HDTEST-PC");
            var pending = S.ReadIdentity().PendingName;
            Check(r.Ok && r.NeedsRestart && pending == "HDTEST-PC", $"{r.Message} · pending='{pending}'");
            return $"{r.Message} · pending={pending}";
        });

        return steps;
    }

    public static async Task<int> RunToFileAsync(string path)
    {
        var steps = await RunAsync();
        var json = JsonSerializer.Serialize(steps, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        });
        await File.WriteAllTextAsync(path, json);
        return steps.All(s => s.Ok) ? 0 : 2;
    }
}
