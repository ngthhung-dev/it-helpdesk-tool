using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Windows;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class RepairToolRow(RepairTool tool) : ObservableObject
{
    public RepairTool Tool { get; } = tool;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _tone = "";
    [ObservableProperty] private bool _isRunning;
}

public sealed record RepairGroup(string Name, List<RepairToolRow> Tools);

public sealed partial class RepairViewModel : PageViewModel
{
    public override string Title => "Sửa lỗi Windows";
    private bool _loaded;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _log = new();

    public List<RepairToolRow> All { get; } = RepairService.Tools.Select(t => new RepairToolRow(t)).ToList();
    public List<RepairGroup> Groups { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private string _logText = "";

    // Tình trạng
    [ObservableProperty] private string _osText = "";
    [ObservableProperty] private string _rebootText = "";
    [ObservableProperty] private bool _rebootPending;
    [ObservableProperty] private string _shutdownText = "Đang đọc…";
    [ObservableProperty] private string _crashText = "";
    public ObservableCollection<CrashDump> Crashes { get; } = [];

    // Nguồn DISM
    [ObservableProperty] private string _sourceText = "Windows Update (mặc định)";
    private string? _mountedIso;

    public RepairViewModel()
    {
        // Xếp A→Z trong từng nhóm; riêng "File hệ thống" giữ đúng trình tự nên chạy (điểm khôi phục → DISM → SFC)
        Groups = All.GroupBy(t => t.Tool.Group)
            .Select(g => new RepairGroup(g.Key, g.Key == "File hệ thống" ? g.ToList() : g.OrderByVi(t => t.Tool.Title).ToList())).ToList();
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadStatusAsync();
    }

    public override void OnNavigatedFrom() { }

    [RelayCommand]
    private async Task LoadStatusAsync()
    {
        var os = Wmi.First("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
        OsText = os is null ? "" : $"{os.Str("Caption")} · build {os.Str("BuildNumber")}";
        RebootPending = RepairService.PendingReboot();
        RebootText = RebootPending ? "Máy đang chờ khởi động lại để hoàn tất cập nhật — nên khởi động lại trước khi sửa." : "Không có cập nhật đang chờ khởi động lại.";
        var dirty = await RepairService.IsDirtyAsync();
        var shut = await RepairService.UnexpectedShutdownsAsync();
        ShutdownText = (shut == 0 ? "Không có lần tắt máy đột ngột nào trong 30 ngày." : $"{shut} lần tắt máy đột ngột / mất điện / treo trong 30 ngày.")
                       + (dirty ? " Ổ hệ thống đang chờ quét lỗi khi khởi động." : "");
        var crashes = await Task.Run(RepairService.RecentCrashes);
        Crashes.Clear();
        foreach (var c in crashes.Take(5)) Crashes.Add(c);
        CrashText = crashes.Count == 0 ? "Không có màn hình xanh nào được ghi lại." : $"{crashes.Count} lần màn hình xanh, gần nhất {Fmt.Stamp(crashes[0].Time)}.";
    }

    private void Append(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
        LogText = _log.ToString();
    }

    private async Task<OpResult> RunToolAsync(RepairToolRow row, CancellationToken ct)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        row.IsRunning = true;
        row.Result = "";
        Current = row.Tool.Title;
        Progress = 0;
        Indeterminate = true;
        Append($"══════ {row.Tool.Title} · {DateTime.Now:HH:mm:ss} ══════");
        OpResult r;
        try
        {
            r = await Task.Run(() => row.Tool.Run(
                l => dispatcher.BeginInvoke(() => Append(l)),
                p => dispatcher.BeginInvoke(() => { Indeterminate = false; Progress = p; }), ct), ct);
        }
        catch (OperationCanceledException) { r = OpResult.Fail("Đã dừng."); }
        catch (Exception ex) { r = OpResult.Fail(ex.Message); }
        row.IsRunning = false;
        row.Result = r.Message;
        row.Tone = r.Ok ? "Good" : "Bad";
        Append((r.Ok ? "✔ " : "✘ ") + r.Message);
        Append("");
        if (r.Ok) Log.Info($"Repair: {r.Message}"); else Log.Warn($"Repair: {r.Message}");
        return r;
    }

    [RelayCommand]
    private async Task RunAsync(RepairToolRow row)
    {
        if (IsBusy) return;
        if (row.Tool.Confirm.Length > 0 && !DialogWindow.Confirm(row.Tool.Title + "?", row.Tool.Confirm, "Chạy")) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var r = await RunToolAsync(row, _cts.Token);
            if (r.NeedsRestart || row.Tool.NeedsRestart && r.Ok) AskRestart();
        }
        finally { IsBusy = false; Current = ""; }
    }

    /// <summary>Sửa tự động: điểm khôi phục → DISM → SFC.</summary>
    [RelayCommand]
    private async Task AutoRepairAsync()
    {
        if (IsBusy) return;
        if (!DialogWindow.Confirm("Sửa file hệ thống tự động?",
                "Lần lượt: tạo điểm khôi phục → DISM RestoreHealth → SFC /scannow. Mất khoảng 20–45 phút, vẫn dùng máy được trong lúc chạy.", "Bắt đầu"))
            return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var restart = false;
        try
        {
            foreach (var key in new[] { "restorepoint", "dism", "sfc" })
            {
                if (_cts.IsCancellationRequested) break;
                var r = await RunToolAsync(All.First(t => t.Tool.Key == key), _cts.Token);
                restart |= r.NeedsRestart;
                if (!r.Ok && key == "dism") Append("DISM chưa sửa được — vẫn chạy tiếp SFC.");
            }
        }
        finally { IsBusy = false; Current = ""; }
        if (restart) AskRestart();
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ClearLog() { _log.Clear(); LogText = ""; }

    private static void AskRestart()
    {
        if (DialogWindow.Confirm("Khởi động lại máy?", "Cần khởi động lại để hoàn tất. Lưu công việc đang mở trước khi đồng ý.", "Khởi động lại"))
            _ = ProcessRunner.ConsoleAsync("shutdown.exe", "/r /t 5 /c \"IT Helpdesk Tool: khởi động lại để hoàn tất sửa lỗi\"");
    }

    // ================= Nguồn sửa DISM =================
    [RelayCommand]
    private async Task PickSourceAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Bộ cài Windows (*.iso;*.wim;*.esd)|*.iso;*.wim;*.esd", Title = "Chọn ISO / install.wim cùng phiên bản Windows đang dùng" };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        Current = "Đang đọc bộ cài…";
        try
        {
            await ClearSourceAsync();
            string image;
            if (dlg.FileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            {
                var root = await ImageService.MountAsync(dlg.FileName);
                _mountedIso = dlg.FileName;
                image = (await ImageService.ReadMediaAsync(root)).ImagePath;
            }
            else image = dlg.FileName;
            var eds = await ImageService.EditionsAsync(image);
            var cur = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "") as string ?? "");
            // Chọn phiên bản trùng với máy (Professional ↔ "Windows 11 Pro")
            var match = eds.FirstOrDefault(e => cur.Length > 0 && e.Name.Replace(" ", "").Contains(cur.Replace("Professional", "Pro"), StringComparison.OrdinalIgnoreCase)) ?? eds.First();
            var kind = image.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? "esd" : "wim";
            RepairService.DismSource = $"{kind}:{image}:{match.Index}";
            SourceText = $"{match.Name} · build {match.Version} ({System.IO.Path.GetFileName(dlg.FileName)})";
        }
        catch (Exception ex) { DialogWindow.Info("Không dùng được bộ cài này", ex.Message, error: true); }
        finally { IsBusy = false; Current = ""; }
    }

    [RelayCommand]
    private async Task ClearSourceAsync()
    {
        RepairService.DismSource = "";
        SourceText = "Windows Update (mặc định)";
        if (_mountedIso is { } iso) { _mountedIso = null; await ImageService.DismountAsync(iso); }
    }

    // ================= Công cụ có sẵn của Windows =================
    [RelayCommand]
    private void OpenTool(string which)
    {
        switch (which)
        {
            case "rstrui": ShellLauncher.Open("rstrui.exe"); break;
            case "reliability": ShellLauncher.Open("perfmon.exe", "/rel"); break;
            case "events": ShellLauncher.Open("eventvwr.msc"); break;
            case "troubleshoot": ShellLauncher.Open("ms-settings:troubleshoot"); break;
            case "recovery": ShellLauncher.Open("ms-settings:recovery"); break;
            case "minidump": ShellLauncher.Open(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump")); break;
            case "cbs": ShellLauncher.Open("notepad.exe", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log")); break;
        }
    }
}
