using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;
using Microsoft.Win32;
using static ITHelpdeskTool.Services.ProcessRunner;

namespace ITHelpdeskTool.Services.Startup;

public sealed record TaskEntry(string Name, string Path, bool Enabled, string State, string Trigger, string Action, string Author)
{
    public string FullName => Path.TrimEnd('\\') + "\\" + Name;
    public string Detail => string.Join(" · ", new[] { Trigger, Author }.Where(x => x.Length > 0));
}

public sealed record ServiceEntry(string Name, string DisplayName, bool Auto, bool Delayed, string State, string Path, string Publisher)
{
    public bool Running => State == "Running";
    public string StateText => (Running ? "Đang chạy" : "Đã dừng") + " · " + (Auto ? (Delayed ? "Tự động (trễ)" : "Tự động") : "Thủ công");
}

/// <summary>Tác vụ lập lịch chạy lúc đăng nhập/khởi động và dịch vụ của hãng thứ ba.</summary>
public static class AutoRunService
{
    // ================= Tác vụ lập lịch =================
    public static async Task<List<TaskEntry>> ReadTasksAsync()
    {
        const string script = """
            $types = 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger'
            Get-ScheduledTask | Where-Object {
              $_.TaskPath -notlike '\Microsoft\*' -and @($_.Triggers | Where-Object { $_.CimClass.CimClassName -in $types }).Count -gt 0
            } | ForEach-Object {
              $t = @($_.Triggers | Where-Object { $_.CimClass.CimClassName -in $types } | ForEach-Object {
                if ($_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger') { 'khi khởi động máy' }
                elseif ($_.UserId) { "khi $($_.UserId) đăng nhập" } else { 'khi bất kỳ ai đăng nhập' } }) | Select-Object -Unique
              [pscustomobject]@{
                Name = $_.TaskName; Path = $_.TaskPath; State = [string]$_.State
                Trigger = ($t -join ', '); Author = [string]$_.Author
                Action = (@($_.Actions | Where-Object { $_.Execute } | ForEach-Object { ("$($_.Execute) $($_.Arguments)").Trim() }) -join '; ')
              }
            } | ConvertTo-Json -Compress
            """;
        var r = await PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var list = new List<TaskEntry>();
        var json = r.Output.Trim();
        if (json.Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var state = S("State");
            list.Add(new TaskEntry(S("Name"), S("Path"), state != "Disabled", state, S("Trigger"), S("Action"), S("Author")));
        }
        return list.OrderByDescending(t => t.Enabled).ThenBy(t => t.Name).ToList();
    }

    public static async Task<OpResult> SetTaskEnabledAsync(TaskEntry t, bool enabled)
    {
        var r = await PowerShellAsync($"{(enabled ? "Enable" : "Disable")}-ScheduledTask -TaskName {PsQuote(t.Name)} -TaskPath {PsQuote(t.Path)} | Out-Null");
        return OpResult.From(r, $"{(enabled ? "Đã bật" : "Đã tắt")} tác vụ {t.Name}.", "Không đổi được tác vụ");
    }

    // ================= Dịch vụ hãng thứ ba =================
    public static List<ServiceEntry> ReadServices()
    {
        var list = new List<ServiceEntry>();
        foreach (var s in Wmi.TryQuery("SELECT Name, DisplayName, StartMode, State, PathName FROM Win32_Service WHERE StartMode = 'Auto' OR StartMode = 'Manual'"))
        {
            var path = StartupService.ExtractExe(s.Str("PathName"));
            if (path.Length == 0 || !File.Exists(path)) continue;
            // Bỏ svchost và mọi file của Microsoft — chỉ giữ dịch vụ do phần mềm khác cài
            if (Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string publisher;
            try { publisher = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? ""; } catch { publisher = ""; }
            if (publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                && publisher.Length == 0) continue;   // file hệ thống không có thông tin hãng

            var name = s.Str("Name");
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            var delayed = k?.GetValue("DelayedAutostart") is int d && d == 1;
            list.Add(new ServiceEntry(name, s.Str("DisplayName"), s.Str("StartMode") == "Auto", delayed, s.Str("State"), path, publisher));
        }
        return list.OrderByDescending(x => x.Auto).ThenBy(x => x.DisplayName).ToList();
    }

    /// <summary>Tự động ↔ Thủ công (không chọn Disabled để phần mềm vẫn tự bật dịch vụ khi cần).</summary>
    public static async Task<OpResult> SetServiceAutoAsync(ServiceEntry s, bool auto)
    {
        var r = await ConsoleAsync("sc.exe", $"config \"{s.Name}\" start= {(auto ? "auto" : "demand")}");
        return OpResult.From(r, auto ? $"{s.DisplayName}: tự chạy cùng Windows." : $"{s.DisplayName}: chuyển sang chạy khi cần (Manual).",
            "Không đổi được dịch vụ");
    }
}
