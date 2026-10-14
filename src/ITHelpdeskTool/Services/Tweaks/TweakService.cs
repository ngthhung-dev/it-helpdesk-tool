using System.Diagnostics;
using System.Globalization;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services.Tweaks;

/// <summary>Đọc, áp dụng và hoàn tác các tweak trong <see cref="TweakCatalog"/>.</summary>
public static class TweakService
{
    public static bool IsWin11 { get; } = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Kiểu khởi động mặc định của Windows cho các dịch vụ mà tweak tắt.</summary>
    private static readonly Dictionary<string, string> ServiceDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SysMain"] = "auto",
        ["DiagTrack"] = "auto",
        ["WSearch"] = "delayed-auto",
    };

    private static RegistryKey Root(TweakHive h) => h switch
    {
        TweakHive.LocalMachine => Registry.LocalMachine,
        TweakHive.DefaultUser => Registry.Users.OpenSubKey(".DEFAULT", writable: true) ?? Registry.Users,
        _ => Registry.CurrentUser,
    };

    private static string Norm(object? v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

    private static bool Matches(TweakReg r)
    {
        using var k = Root(r.Hive).OpenSubKey(r.Path);
        if (k is null) return false;
        var v = k.GetValue(r.Name);
        // Khoá dạng "có là bật": giá trị mặc định rỗng vẫn tính là có
        if (r.DeleteKeyOnOff) return v is not null || k.GetValueNames().Contains(r.Name);
        return v is not null && Norm(v).Equals(Norm(r.On), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ServiceExists(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k?.GetValue("Start") is not null;
    }

    private static bool ServiceDisabled(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k?.GetValue("Start") is int s && s == 4;
    }

    /// <summary>Tweak có dùng được trên máy này không (đúng phiên bản Windows, dịch vụ có tồn tại).</summary>
    public static bool IsAvailable(TweakDef t) =>
        (!t.Win11Only || IsWin11) && (t.DisableService is null || ServiceExists(t.DisableService));

    public static bool IsOn(TweakDef t) =>
        t.Values.All(Matches) && (t.DisableService is null || ServiceDisabled(t.DisableService));

    public static Dictionary<string, bool> ReadAll() =>
        TweakCatalog.All.ToDictionary(t => t.Key, t => IsAvailable(t) && IsOn(t));

    private static void Write(TweakReg r, bool on)
    {
        var root = Root(r.Hive);
        if (on)
        {
            using var k = root.CreateSubKey(r.Path, writable: true);
            k.SetValue(r.Name, r.On, r.Kind);
        }
        else if (r.DeleteKeyOnOff)
        {
            root.DeleteSubKeyTree(r.Path, throwOnMissingSubKey: false);
        }
        else if (r.Off is not null)
        {
            using var k = root.CreateSubKey(r.Path, writable: true);
            k.SetValue(r.Name, r.Off, r.Kind);
        }
        else
        {
            using var k = root.OpenSubKey(r.Path, writable: true);
            k?.DeleteValue(r.Name, throwOnMissingValue: false);
        }
    }

    private static async Task<OpResult?> SetServiceAsync(string name, bool disable)
    {
        if (disable)
        {
            var r = await ProcessRunner.ConsoleAsync("sc.exe", $"config {name} start= disabled");
            if (!r.Success) return OpResult.Fail($"Không tắt được dịch vụ {name}: {OpResult.FirstLine(r.Text)}");
            await ProcessRunner.PowerShellAsync($"Stop-Service -Name {name} -Force -ErrorAction SilentlyContinue");
        }
        else
        {
            var mode = ServiceDefaults.GetValueOrDefault(name, "auto");
            var r = await ProcessRunner.ConsoleAsync("sc.exe", $"config {name} start= {mode}");
            if (!r.Success) return OpResult.Fail($"Không bật lại được dịch vụ {name}: {OpResult.FirstLine(r.Text)}");
            await ProcessRunner.PowerShellAsync($"Start-Service -Name {name} -ErrorAction SilentlyContinue");
        }
        return null;
    }

    public static async Task<OpResult> ApplyAsync(TweakDef t, bool on)
    {
        if (!IsAvailable(t)) return OpResult.Fail($"{t.Title}: không áp dụng được trên máy này.");
        try
        {
            foreach (var r in t.Values) Write(r, on);
            if (t.DisableService is { } svc && await SetServiceAsync(svc, on) is { } err) return err;

            var when = t.Effect switch
            {
                TweakEffect.Explorer => " Bấm “Khởi động lại Explorer” để thấy thay đổi.",
                TweakEffect.Relogon => " Có hiệu lực sau khi đăng xuất/đăng nhập lại.",
                TweakEffect.Restart => " Có hiệu lực sau khi khởi động lại máy.",
                _ => "",
            };
            return OpResult.Success($"{(on ? "Đã bật" : "Đã trả về mặc định")}: {t.Title}.{when}", t.Effect == TweakEffect.Restart);
        }
        catch (UnauthorizedAccessException) { return OpResult.Fail("Không có quyền ghi registry — cần chạy app bằng quyền Administrator."); }
        catch (Exception ex) { return OpResult.Fail($"{t.Title}: {ex.Message}"); }
    }

    /// <summary>Áp nhiều tweak một lượt (bỏ qua mục không dùng được hoặc đã đúng trạng thái).</summary>
    public static async Task<OpResult> ApplyManyAsync(IEnumerable<(TweakDef Def, bool On)> items)
    {
        var done = 0;
        var restart = false;
        var errors = new List<string>();
        foreach (var (t, on) in items)
        {
            if (!IsAvailable(t) || IsOn(t) == on) continue;
            var r = await ApplyAsync(t, on);
            if (r.Ok) { done++; restart |= r.NeedsRestart; }
            else errors.Add(r.Message);
        }
        if (errors.Count > 0) return OpResult.Fail($"Đã đổi {done} mục, {errors.Count} mục lỗi: {string.Join("; ", errors.Take(3))}");
        return OpResult.Success(done == 0 ? "Không có gì cần thay đổi." : $"Đã đổi {done} mục. Khởi động lại Explorer hoặc đăng xuất để thấy hết thay đổi.", restart);
    }

    /// <summary>Khởi động lại Explorer để thấy thay đổi giao diện ngay.</summary>
    public static async Task<OpResult> RestartExplorerAsync()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
            }
            // Windows thường tự bật lại Explorer; chờ một chút rồi tự bật nếu chưa có
            for (var i = 0; i < 20 && Process.GetProcessesByName("explorer").Length == 0; i++) await Task.Delay(150);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            return OpResult.Success("Đã khởi động lại Explorer.");
        }
        catch (Exception ex) { return OpResult.Fail($"Không khởi động lại được Explorer: {ex.Message}"); }
    }
}
