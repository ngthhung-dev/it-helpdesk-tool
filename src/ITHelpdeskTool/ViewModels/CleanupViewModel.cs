using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Cleanup;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class CleanupItem(CleanupDef def) : ObservableObject
{
    public CleanupDef Def { get; } = def;
    public string Title => Def.Title;
    public string Description => Def.Description;
    public string? Note => Def.Note;

    [ObservableProperty] private bool _isChecked = def.DefaultChecked;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] private long _bytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText))] private int _files;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private Tone _resultTone = Tone.Muted;

    public string SizeText => IsScanning ? "Đang quét…" : Bytes == 0 ? "Sạch" : $"{Fmt.Size(Bytes)} · {Fmt.Num(Files)} file";
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(SizeText));
}

public sealed partial class CleanupViewModel : PageViewModel
{
    public override string Title => "Dọn rác";
    private bool _loaded;

    public ObservableCollection<CleanupItem> Items { get; } = new(CleanupService.Items.Select(d => new CleanupItem(d)));
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _driveText = "";
    [ObservableProperty] private double _driveUsedPercent;
    [ObservableProperty] private string _oldInstallText = "Đang kiểm tra…";
    [ObservableProperty] private bool _hasOldInstall;
    [ObservableProperty] private bool _resetBase;
    [ObservableProperty] private string _browserWarning = "";

    public CleanupViewModel()
    {
        foreach (var i in Items)
            i.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanupItem.IsChecked) or nameof(CleanupItem.Bytes)) UpdateTotal(); };
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = ScanAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Cleanup: {text}"); else Log.Warn($"Cleanup: {text}");
    }

    private void UpdateTotal()
    {
        var sel = Items.Where(i => i.IsChecked).ToList();
        TotalText = $"Đã chọn {sel.Count} mục · có thể giải phóng khoảng {Fmt.Size(sel.Sum(i => i.Bytes))}";
    }

    private void UpdateDrive()
    {
        var (free, total) = CleanupService.SystemDriveSpace();
        DriveUsedPercent = total == 0 ? 0 : (total - free) * 100.0 / total;
        DriveText = $"Ổ hệ thống còn trống {Fmt.Size(free)} / {Fmt.Size(total)}";
        BrowserWarning = CleanupService.BrowsersRunning(out var names) ? $"Đang mở {names} — tắt trình duyệt trước để dọn cache được nhiều hơn." : "";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Đang quét…";
        UpdateDrive();
        try
        {
            foreach (var i in Items) { i.IsScanning = true; i.ResultText = ""; }
            await Task.WhenAll(Items.Select(async i =>
            {
                var r = await Task.Run(() => CleanupService.Scan(i.Def));
                i.Bytes = r.Bytes;
                i.Files = r.Files;
                i.IsScanning = false;
            }));
            var (oldBytes, found) = await Task.Run(CleanupService.ScanOldInstalls);
            HasOldInstall = found.Count > 0;
            OldInstallText = found.Count == 0
                ? "Không có bản Windows cũ (Windows.old) hay file nâng cấp còn sót."
                : $"{string.Join(", ", found.Select(System.IO.Path.GetFileName))} · {Fmt.Size(oldBytes)}";
        }
        finally { IsBusy = false; }
        UpdateTotal();
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        var sel = Items.Where(i => i.IsChecked).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn mục nào", "Tích chọn các mục muốn dọn."); return; }
        if (sel.Any(i => i.Def.Kind == CleanKind.RecycleBin && i.Bytes > 0) &&
            !DialogWindow.Confirm("Xoá vĩnh viễn Thùng rác?", "Mọi file trong Thùng rác sẽ bị xoá hẳn, không khôi phục được.", "Dọn", danger: true))
            return;
        if (IsBusy) return;
        IsBusy = true;
        var sw = Stopwatch.StartNew();
        long total = 0;
        try
        {
            foreach (var i in sel)
            {
                BusyText = $"Đang dọn: {i.Title}…";
                var r = await CleanupService.CleanAsync(i.Def);
                total += r.Freed;
                i.ResultText = r.Skipped > 0
                    ? $"Đã dọn {Fmt.Size(r.Freed)} · bỏ qua {r.Skipped} file đang dùng"
                    : $"Đã dọn {Fmt.Size(r.Freed)}";
                i.ResultTone = r.Freed > 0 ? Tone.Good : Tone.Muted;
                var after = await Task.Run(() => CleanupService.Scan(i.Def));
                i.Bytes = after.Bytes;
                i.Files = after.Files;
            }
        }
        finally { IsBusy = false; }
        UpdateDrive();
        AddLog($"Dọn {sel.Count} mục, giải phóng {Fmt.Size(total)} trong {sw.Elapsed.TotalSeconds:0} giây.", true);
    }

    [RelayCommand]
    private void SelectAll(string which)
    {
        foreach (var i in Items)
            i.IsChecked = which switch { "all" => true, "none" => false, _ => i.Def.DefaultChecked };
    }

    // ================= Dọn sâu =================
    [RelayCommand]
    private async Task RemoveOldInstallAsync()
    {
        if (!DialogWindow.Confirm("Xoá bản Windows cũ?",
                "Sau khi xoá sẽ KHÔNG thể quay lại phiên bản Windows trước (Go back). Chỉ xoá khi máy đã chạy ổn với bản hiện tại. Có thể mất vài phút.",
                "Xoá", danger: true))
            return;
        IsBusy = true;
        BusyText = "Đang xoá bản Windows cũ…";
        OpResult r;
        try { r = await CleanupService.RemoveOldInstallsAsync(t => BusyText = t); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa xoá hết", r.Message, error: true);
        await ScanAsync();
    }

    [RelayCommand]
    private async Task ComponentCleanupAsync()
    {
        if (!DialogWindow.Confirm("Dọn kho thành phần Windows (WinSxS)?",
                "DISM sẽ xoá các bản cũ của file hệ thống đã được cập nhật thay thế. Mất khoảng 5–30 phút, máy vẫn dùng bình thường." +
                (ResetBase ? "\n\nĐã chọn “ResetBase”: sau khi dọn sẽ không gỡ được các bản cập nhật đã cài." : ""),
                "Bắt đầu"))
            return;
        IsBusy = true;
        var sw = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => BusyText = $"DISM đang dọn WinSxS… {sw.Elapsed:mm\\:ss}";
        timer.Start();
        OpResult r;
        try { r = await CleanupService.ComponentCleanupAsync(ResetBase); }
        finally { timer.Stop(); IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("DISM", r.Message, error: true);
        UpdateDrive();
    }

    [RelayCommand]
    private void OpenDiskCleanup() => ShellLauncher.Open("cleanmgr.exe");

    [RelayCommand]
    private void OpenStorageSettings() => ShellLauncher.Open("ms-settings:storagesense");
}
