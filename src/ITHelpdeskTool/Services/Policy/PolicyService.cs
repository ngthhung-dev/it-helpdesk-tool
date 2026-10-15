using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Policy;

/// <summary>Tài khoản có thể nhận policy kiểu "người dùng".</summary>
public sealed record PolicyUser(string Name, string Sid, bool IsAdmin, bool IsCurrent, bool HasProfile)
{
    public string Display => Name + (IsCurrent ? " (đang dùng)" : "") + (IsAdmin ? " · quản trị" : "") + (HasProfile ? "" : " · chưa có hồ sơ");
}

public sealed record PasswordPolicy(int MinLength, int MaxAgeDays, int LockoutThreshold, int LockoutMinutes);

/// <summary>
/// Đọc/ghi policy bằng registry. Policy người dùng ghi vào hive của đúng tài khoản đó
/// (HKU\SID nếu đang đăng nhập, hoặc nạp tạm NTUSER.DAT nếu không).
/// </summary>
public static class PolicyService
{
    // ================= Phiên bản Windows =================
    public static bool IsHomeEdition()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var ed = k?.GetValue("EditionID") as string ?? "";
        return ed.StartsWith("Core", StringComparison.OrdinalIgnoreCase);   // Core, CoreSingleLanguage, CoreN...
    }

    // ================= Tài khoản =================
    public static async Task<List<PolicyUser>> ReadUsersAsync()
    {
        var users = await SysConfigService.ReadUsersAsync();
        return users.Where(u => !u.IsSystem)
            .Select(u => new PolicyUser(u.Name, u.Sid, u.IsAdmin, u.IsCurrent, ProfilePath(u.Sid) is not null))
            .OrderBy(u => u.IsAdmin).ThenBy(u => u.Name)
            .ToList();
    }

    private static string? ProfilePath(string sid)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        var path = k?.GetValue("ProfileImagePath") as string;
        if (path is null) return null;
        path = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(Path.Combine(path, "NTUSER.DAT")) ? path : null;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateProfile(string pszUserSid, string pszUserName, StringBuilder pszProfilePath, uint cchProfilePath);

    /// <summary>Tạo sẵn hồ sơ (C:\Users\tên) cho tài khoản chưa đăng nhập lần nào, để ghi được policy.</summary>
    public static OpResult EnsureProfile(PolicyUser u)
    {
        if (ProfilePath(u.Sid) is not null) return OpResult.Success("Đã có hồ sơ.");
        var sb = new StringBuilder(260);
        var hr = CreateProfile(u.Sid, u.Name, sb, (uint)sb.Capacity);
        return hr == 0 || ProfilePath(u.Sid) is not null
            ? OpResult.Success($"Đã tạo hồ sơ cho {u.Name} tại {sb}.")
            : OpResult.Fail($"Không tạo được hồ sơ cho {u.Name} (mã 0x{hr:X8}).");
    }

    /// <summary>
    /// Mở hive registry của người dùng. Đang đăng nhập → HKU\SID; chưa → nạp tạm NTUSER.DAT
    /// rồi gỡ ra khi Dispose (dùng reg.exe vì cần quyền Backup/Restore mà admin đã có).
    /// </summary>
    private sealed class UserHive : IDisposable
    {
        public RegistryKey Root { get; }
        private readonly string? _tempName;

        private UserHive(RegistryKey root, string? temp) { Root = root; _tempName = temp; }

        public static UserHive Open(string sid)
        {
            var loaded = Registry.Users.OpenSubKey(sid, writable: true);
            if (loaded is not null) return new UserHive(loaded, null);

            var profile = ProfilePath(sid) ?? throw new InvalidOperationException(
                "Tài khoản này chưa đăng nhập lần nào nên chưa có hồ sơ. Bấm \"Tạo hồ sơ\" hoặc đăng nhập tài khoản đó một lần.");
            var temp = "HD_" + sid.Replace("-", "_");
            var r = ProcessRunner.ConsoleAsync("reg.exe", $"load \"HKU\\{temp}\" \"{Path.Combine(profile, "NTUSER.DAT")}\"").GetAwaiter().GetResult();
            if (!r.Success) throw new InvalidOperationException($"Không nạp được hồ sơ người dùng: {OpResult.FirstLine(r.Text)} (tài khoản có thể đang đăng nhập ở phiên khác).");
            return new UserHive(Registry.Users.OpenSubKey(temp, writable: true)!, temp);
        }

        public void Dispose()
        {
            Root.Dispose();
            if (_tempName is null) return;
            // Phải đóng hết handle trước khi gỡ hive
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var i = 0; i < 5; i++)
            {
                var r = ProcessRunner.ConsoleAsync("reg.exe", $"unload \"HKU\\{_tempName}\"").GetAwaiter().GetResult();
                if (r.Success) return;
                Thread.Sleep(300);
            }
            Log.Warn($"Không gỡ được hive tạm HKU\\{_tempName}");
        }
    }

    // ================= Đọc trạng thái =================
    private static bool Matches(RegistryKey root, RegSet s)
    {
        using var k = root.OpenSubKey(s.Path);
        var v = k?.GetValue(s.Name);
        if (v is null) return false;
        var want = s.On();
        return string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), Convert.ToString(want, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Đọc trạng thái mọi policy. Policy người dùng đọc theo <paramref name="userSid"/>.</summary>
    public static Dictionary<string, bool> ReadStates(string? userSid)
    {
        var map = new Dictionary<string, bool>();
        foreach (var p in PolicyCatalog.All.Where(p => p.Scope == PolicyScope.Machine))
            map[p.Key] = p.Values.All(v => Matches(Registry.LocalMachine, v));

        if (userSid is not null && PolicyCatalog.All.Any(p => p.Scope == PolicyScope.User))
        {
            try
            {
                using var hive = UserHive.Open(userSid);
                foreach (var p in PolicyCatalog.All.Where(p => p.Scope == PolicyScope.User))
                    map[p.Key] = p.Values.All(v => Matches(hive.Root, v));
            }
            catch (Exception ex) { Log.Warn($"Đọc policy người dùng lỗi: {ex.Message}"); }
        }
        return map;
    }

    // ================= Ghi =================
    private static void Write(RegistryKey root, RegSet s, bool on)
    {
        if (on)
        {
            using var k = root.CreateSubKey(s.Path, writable: true);
            k.SetValue(s.Name, s.On(), s.Kind);
        }
        else if (s.Off is not null)
        {
            using var k = root.CreateSubKey(s.Path, writable: true);
            k.SetValue(s.Name, s.Off, s.Kind);
        }
        else
        {
            using var k = root.OpenSubKey(s.Path, writable: true);
            k?.DeleteValue(s.Name, throwOnMissingValue: false);
        }
    }

    public static OpResult Apply(PolicyDef p, bool on, PolicyUser? user)
    {
        try
        {
            if (p.Scope == PolicyScope.User)
            {
                if (user is null) return OpResult.Fail("Chưa chọn tài khoản để áp dụng.");
                using var hive = UserHive.Open(user.Sid);
                foreach (var v in p.Values) Write(hive.Root, v, on);
            }
            else
            {
                foreach (var v in p.Values) Write(Registry.LocalMachine, v, on);
            }

            var who = p.Scope == PolicyScope.User ? $" cho {user!.Name}" : "";
            var when = p.Effect switch
            {
                PolicyEffect.Relogon => " Có hiệu lực sau khi đăng xuất/đăng nhập lại.",
                PolicyEffect.Restart => " Có hiệu lực sau khi khởi động lại.",
                _ => "",
            };
            return OpResult.Success($"{(on ? "Đã bật" : "Đã tắt")}: {p.Title}{who}.{when}", p.Effect == PolicyEffect.Restart);
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Không có quyền ghi registry — hãy chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    /// <summary>Tắt mọi policy do app quản lý (cho máy và cho tài khoản đang chọn).</summary>
    public static OpResult ResetAll(PolicyUser? user)
    {
        var n = 0;
        var errors = new List<string>();
        foreach (var p in PolicyCatalog.All)
        {
            if (p.Scope == PolicyScope.User && user is null) continue;
            var r = Apply(p, false, user);
            if (r.Ok) n++; else errors.Add($"{p.Title}: {r.Message}");
        }
        return errors.Count == 0
            ? OpResult.Success($"Đã đưa {n} policy về mặc định của Windows" + (user is null ? "." : $" (phần người dùng: {user.Name})."))
            : OpResult.Fail($"Có {errors.Count} mục lỗi: {string.Join("; ", errors.Take(3))}");
    }

    // ================= Mật khẩu & khoá tài khoản =================
    /// <summary>Đọc bằng secedit (không phụ thuộc ngôn ngữ Windows như "net accounts").</summary>
    public static async Task<PasswordPolicy> ReadPasswordPolicyAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"hd_secpol_{Guid.NewGuid():N}.inf");
        try
        {
            var r = await ProcessRunner.ConsoleAsync("secedit.exe", $"/export /cfg \"{file}\" /areas SECURITYPOLICY /quiet");
            if (!File.Exists(file)) throw new InvalidOperationException($"secedit lỗi: {OpResult.FirstLine(r.Text)}");
            var map = File.ReadAllLines(file, Encoding.Unicode)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            int I(string k) => map.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : 0;
            var maxAge = I("MaximumPasswordAge");
            return new PasswordPolicy(I("MinimumPasswordLength"), maxAge is < 0 or >= 999 ? 0 : maxAge,
                I("LockoutBadCount"), I("LockoutDuration"));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public static async Task<OpResult> SetPasswordPolicyAsync(int minLength, int maxAgeDays, int lockoutThreshold, int lockoutMinutes)
    {
        if (minLength is < 0 or > 14) return OpResult.Fail("Độ dài tối thiểu từ 0 đến 14 ký tự.");
        if (maxAgeDays is < 0 or > 998) return OpResult.Fail("Số ngày hết hạn từ 0 (không hết hạn) đến 998.");
        if (lockoutThreshold is < 0 or > 999) return OpResult.Fail("Số lần nhập sai từ 0 (không khoá) đến 999.");
        if (lockoutMinutes is < 1 or > 99999) return OpResult.Fail("Thời gian khoá từ 1 phút trở lên.");

        var age = maxAgeDays == 0 ? "unlimited" : maxAgeDays.ToString();
        var r = await ProcessRunner.ConsoleAsync("net.exe", $"accounts /minpwlen:{minLength} /maxpwage:{age} /lockoutthreshold:{lockoutThreshold}");
        if (!r.Success) return OpResult.Fail($"Không đặt được chính sách mật khẩu: {OpResult.FirstLine(r.Text)}");
        if (lockoutThreshold > 0)
        {
            var w = await ProcessRunner.ConsoleAsync("net.exe", $"accounts /lockoutduration:{lockoutMinutes} /lockoutwindow:{lockoutMinutes}");
            if (!w.Success) return OpResult.Fail($"Đã đặt mật khẩu nhưng lỗi thời gian khoá: {OpResult.FirstLine(w.Text)}");
        }
        return OpResult.Success($"Mật khẩu tối thiểu {minLength} ký tự, " +
                                (maxAgeDays == 0 ? "không bao giờ hết hạn" : $"hết hạn sau {maxAgeDays} ngày") + ", " +
                                (lockoutThreshold == 0 ? "không khoá khi nhập sai." : $"khoá {lockoutMinutes} phút sau {lockoutThreshold} lần nhập sai."));
    }

    // ================= Công cụ =================
    public static async Task<OpResult> GpUpdateAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var r = await ProcessRunner.ConsoleAsync("gpupdate.exe", "/force /wait:120", cts.Token);
        return OpResult.From(r, "Đã cập nhật Group Policy (gpupdate /force).", "gpupdate lỗi");
    }

    /// <summary>
    /// Báo cáo policy đang áp dụng. Dùng gpresult /h nếu Windows hỗ trợ (Pro trở lên); bản Home không có báo cáo HTML của gpresult
    /// → app tự tạo báo cáo: policy của app đang bật + toàn bộ giá trị trong các nhánh Policies của registry (máy và người dùng).
    /// </summary>
    public static async Task<(OpResult Result, string? Path)> GpResultAsync(string? userSid = null, bool forceOwn = false)
    {
        var stamp = $"{Environment.MachineName}_{DateTime.Now:yyyyMMdd-HHmmss}";
        string? why = null;
        if (!forceOwn)
        {
            var path = Path.Combine(Path.GetTempPath(), $"GPResult_{stamp}.html");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var r = await ProcessRunner.ConsoleAsync("gpresult.exe", $"/h \"{path}\" /f", cts.Token);
            if (File.Exists(path)) return (OpResult.Success("Đã tạo báo cáo Group Policy (gpresult)."), path);
            why = OpResult.FirstLine(r.Text);
            Log.Info($"gpresult /h không dùng được ({why}) — tự tạo báo cáo");
        }
        try
        {
            var own = Path.Combine(Path.GetTempPath(), $"PolicyReport_{stamp}.html");
            var gpr = await ProcessRunner.ConsoleAsync("gpresult.exe", "/r");
            await File.WriteAllTextAsync(own, await Task.Run(() => BuildReport(userSid, gpr.Success ? gpr.Output : null)), Encoding.UTF8);
            return (OpResult.Success(why is null ? "Đã tạo báo cáo policy." : "Đã tạo báo cáo policy (Windows này không có báo cáo HTML của gpresult nên app tự tổng hợp)."), own);
        }
        catch (Exception ex) { return (OpResult.Fail($"Không tạo được báo cáo: {ex.Message}"), null); }
    }

    private static readonly string[] PolicyRoots =
    [
        @"SOFTWARE\Policies",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
        @"SOFTWARE\WOW6432Node\Policies",
    ];

    private static string BuildReport(string? userSid, string? gpresultText)
    {
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        userSid ??= me.User?.Value;
        var states = ReadStates(userSid);
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="vi"><head><meta charset="utf-8"><title>Báo cáo policy</title><style>
            body{font-family:Segoe UI,Arial,sans-serif;margin:32px;color:#1f2937;background:#f8fafc}
            h1{font-size:24px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:2px solid #e5e7eb;padding-bottom:4px}
            h3{font-size:14px;margin:16px 0 6px;color:#374151}.muted{color:#6b7280;font-size:13px}
            table{border-collapse:collapse;width:100%;font-size:13px;background:#fff}td,th{border:1px solid #e5e7eb;padding:5px 8px;text-align:left;vertical-align:top}
            th{background:#f1f5f9}code{font-family:Consolas,monospace;font-size:12px;word-break:break-all}.on{color:#059669;font-weight:600}
            pre{background:#fff;border:1px solid #e5e7eb;padding:12px;font-size:12px;white-space:pre-wrap}
            </style></head><body>
            """);
        sb.Append($"<h1>Báo cáo policy đang áp dụng</h1><div class='muted'>Máy {H(Environment.MachineName)} · {H(RuntimeInformation.OSDescription)} · " +
                  $"tạo lúc {DateTime.Now:HH:mm dd/MM/yyyy} bởi IT Helpdesk Tool</div>");

        // 1) Policy của app đang bật
        sb.Append("<h2>Policy đang bật (theo danh mục của app)</h2>");
        var on = PolicyCatalog.All.Where(p => states.GetValueOrDefault(p.Key)).ToList();
        if (on.Count == 0) sb.Append("<p class='muted'>Không có policy nào trong danh mục đang bật.</p>");
        else
        {
            sb.Append("<table><tr><th>Nhóm</th><th>Policy</th><th>Phạm vi</th><th>Mô tả</th></tr>");
            foreach (var p in on)
                sb.Append($"<tr><td>{H(p.Group)}</td><td class='on'>{H(p.Title)}</td><td>{(p.Scope == PolicyScope.Machine ? "Cả máy" : "Người dùng")}</td><td>{H(p.Description)}</td></tr>");
            sb.Append("</table>");
        }

        // 2) Toàn bộ giá trị policy trong registry (kể cả do phần mềm khác / domain đặt)
        void Dump(RegistryKey root, string label)
        {
            sb.Append($"<h3>{H(label)}</h3>");
            var rows = new List<(string Key, string Name, string Value)>();
            foreach (var r in PolicyRoots)
            {
                using var k = root.OpenSubKey(r);
                if (k is not null) Collect(k, root.Name + "\\" + r, rows, 0);
            }
            if (rows.Count == 0) { sb.Append("<p class='muted'>Không có giá trị nào.</p>"); return; }
            sb.Append("<table><tr><th>Khoá</th><th>Tên</th><th>Giá trị</th></tr>");
            foreach (var (key, name, value) in rows)
                sb.Append($"<tr><td><code>{H(key)}</code></td><td>{H(name)}</td><td><code>{H(value)}</code></td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<h2>Giá trị policy trong registry</h2><div class='muted'>Gồm cả policy do phần mềm khác, Intune hay domain đặt.</div>");
        Dump(Registry.LocalMachine, "Máy (HKLM)");
        try
        {
            if (userSid is not null)
            {
                using var hive = UserHive.Open(userSid);
                Dump(hive.Root, $"Người dùng ({userSid})");
            }
        }
        catch (Exception ex) { sb.Append($"<p class='muted'>Không đọc được policy người dùng: {H(ex.Message)}</p>"); }

        // 3) gpresult /r (dạng chữ) nếu có
        if (!string.IsNullOrWhiteSpace(gpresultText))
            sb.Append("<h2>gpresult /r</h2><pre>").Append(H(gpresultText)).Append("</pre>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Collect(RegistryKey k, string path, List<(string, string, string)> rows, int depth)
    {
        if (depth > 12 || rows.Count > 5000) return;
        foreach (var n in k.GetValueNames())
        {
            object? v;
            try { v = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { continue; }
            var text = v switch
            {
                byte[] b => BitConverter.ToString(b.Take(64).ToArray()) + (b.Length > 64 ? "…" : ""),
                string[] a => string.Join(" | ", a),
                int i => $"{i} (0x{i:X})",
                long l => l.ToString(CultureInfo.InvariantCulture),
                null => "",
                _ => v.ToString() ?? "",
            };
            rows.Add((path, n.Length == 0 ? "(Mặc định)" : n, text));
        }
        foreach (var sub in k.GetSubKeyNames())
        {
            try { using var c = k.OpenSubKey(sub); if (c is not null) Collect(c, path + "\\" + sub, rows, depth + 1); } catch { }
        }
    }

    public static bool HasGpedit() => File.Exists(Path.Combine(Environment.SystemDirectory, "gpedit.msc"));

    /// <summary>Mô tả lỗi Win32 (dùng khi CreateProfile trả mã lỗi).</summary>
    internal static string Win32Message(int code) => new Win32Exception(code).Message;
}
