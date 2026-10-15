using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Disk;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class VolumeRow(VolumeInfo v) : ObservableObject
{
    public VolumeInfo V { get; } = v;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk = true;
}

public sealed class FolderRow(FolderSize f, long max)
{
    public FolderSize F { get; } = f;
    public double Percent => max > 0 ? 100.0 * F.Bytes / max : 0;
    public string SizeText => Fmt.Size(F.Bytes);
    public string Detail => F.IsDir ? $"{F.Files:N0} file" : "file";
}

public sealed partial class DiskViewModel : PageViewModel
{
    public override string Title => "Công cụ ổ đĩa";
    private bool _loaded;
    private CancellationTokenSource? _cts;

    public ObservableCollection<PhysicalDiskInfo> Disks { get; } = [];
    public ObservableCollection<VolumeRow> Volumes { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _hasProgress;
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    // Phân tích dung lượng
    [ObservableProperty] private string _analyzePath = "";
    [ObservableProperty] private string _analyzeSummary = "";
    public ObservableCollection<FolderRow> Folders { get; } = [];

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 30) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Disk: {text}"); else Log.Warn($"Disk: {text}");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var dt = DiskService.DisksAsync();
            var vt = DiskService.VolumesAsync();
            var disks = await dt;
            Disks.Clear();
            foreach (var d in disks) Disks.Add(d);
            var vols = await vt;
            Volumes.Clear();
            foreach (var v in vols) Volumes.Add(new VolumeRow(v));
            if (AnalyzePath.Length == 0 && ShellViewModel.StartSubPage == "analyze")
                await AnalyzeAsync(Path.GetPathRoot(Environment.SystemDirectory)!);
        }
        catch (Exception ex) { AddLog($"Không đọc được ổ đĩa: {ex.Message}", false); }
    }

    private async Task Run(VolumeRow row, string busy, Func<Action<string>, Action<int>, CancellationToken, Task<OpResult>> f, bool refresh = false)
    {
        IsBusy = true;
        BusyText = busy;
        Progress = 0;
        HasProgress = false;
        _cts = new CancellationTokenSource();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            var r = await Task.Run(() => f(_ => { }, p => dispatcher.BeginInvoke(() => { HasProgress = true; Progress = p; BusyText = $"{busy} {p}%"; }), _cts.Token));
            row.Result = r.Message;
            row.ResultOk = r.Ok;
            AddLog(r.Message, r.Ok);
        }
        catch (OperationCanceledException) { row.Result = "Đã dừng."; row.ResultOk = false; }
        catch (Exception ex) { row.Result = ex.Message; row.ResultOk = false; AddLog(ex.Message, false); }
        finally { IsBusy = false; HasProgress = false; }
        if (refresh) await RefreshAsync();
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private Task OptimizeAsync(VolumeRow r) => Run(r, $"Đang tối ưu ổ {r.V.Letter}:…", (l, p, ct) => DiskService.OptimizeAsync(r.V, l, p, ct));

    [RelayCommand]
    private Task CheckAsync(VolumeRow r) => Run(r, $"Đang kiểm tra lỗi ổ {r.V.Letter}:…", (l, p, ct) => DiskService.CheckAsync(r.V, l, p, ct));

    [RelayCommand]
    private Task SpeedAsync(VolumeRow r)
    {
        if (r.V.Free < 2L * 1024 * 1024 * 1024) { DialogWindow.Info("Ổ gần đầy", "Cần trống ít nhất 2 GB để đo tốc độ."); return Task.CompletedTask; }
        return Run(r, $"Đang đo tốc độ ổ {r.V.Letter}: (ghi rồi đọc 1 GB)…", async (l, p, ct) =>
        {
            var (w, rd) = await DiskService.SpeedTestAsync(r.V, 1024, p, ct);
            return OpResult.Success($"Ổ {r.V.Letter}: đọc {rd:N0} MB/s · ghi {w:N0} MB/s (tuần tự)");
        });
    }

    [RelayCommand]
    private Task RenameAsync(VolumeRow r)
    {
        var name = DialogWindow.AskText($"Đổi tên ổ {r.V.Letter}:", "Tên mới (tối đa 32 ký tự với NTFS, 11 với FAT32):", r.V.Label);
        if (name is null) return Task.CompletedTask;
        return Run(r, "Đang đổi tên…", (_, _, _) => DiskService.SetLabelAsync(r.V, name), refresh: true);
    }

    [RelayCommand]
    private Task ChangeLetterAsync(VolumeRow r)
    {
        if (r.V.IsProtected) { DialogWindow.Info("Không đổi được", "Không đổi ký tự ổ hệ thống."); return Task.CompletedTask; }
        var free = DiskService.FreeLetters();
        var s = DialogWindow.AskText($"Đổi ký tự ổ {r.V.Letter}:", $"Ký tự mới (còn trống: {string.Join(" ", free)}).\nPhần mềm / lối tắt đang trỏ tới {r.V.Letter}: sẽ không tìm thấy file nữa.", free.FirstOrDefault().ToString());
        if (string.IsNullOrWhiteSpace(s)) return Task.CompletedTask;
        var c = char.ToUpperInvariant(s.Trim()[0]);
        if (!free.Contains(c)) { DialogWindow.Info("Ký tự không hợp lệ", $"{c}: đang được dùng hoặc không hợp lệ."); return Task.CompletedTask; }
        return Run(r, "Đang đổi ký tự ổ…", (_, _, _) => DiskService.SetLetterAsync(r.V, c), refresh: true);
    }

    [RelayCommand]
    private Task ExtendAsync(VolumeRow r)
    {
        if (!DialogWindow.Confirm($"Mở rộng ổ {r.V.Letter}:?", $"Gộp {Fmt.Size(r.V.MaxSize - r.V.Size)} dung lượng trống liền kề vào ổ {r.V.Letter}:. Dữ liệu giữ nguyên.", "Mở rộng"))
            return Task.CompletedTask;
        return Run(r, "Đang mở rộng ổ…", (_, _, _) => DiskService.ExtendAsync(r.V), refresh: true);
    }

    [RelayCommand]
    private Task FormatAsync(VolumeRow r)
    {
        if (r.V.IsProtected) { DialogWindow.Info("Không định dạng được", "Đây là ổ hệ thống."); return Task.CompletedTask; }
        if (!DialogWindow.Confirm($"XOÁ TOÀN BỘ dữ liệu trên ổ {r.V.Letter}:?",
                $"{r.V.Display} · {r.V.SpaceText}\n\nĐịnh dạng nhanh NTFS — mọi file trên ổ này sẽ mất. Kiểm tra kỹ trước khi đồng ý.", "Định dạng", danger: true))
            return Task.CompletedTask;
        var label = DialogWindow.AskText("Tên ổ sau khi định dạng", "Tên ổ:", r.V.Label.Length > 0 ? r.V.Label : "Data");
        if (label is null) return Task.CompletedTask;
        return Run(r, $"Đang định dạng ổ {r.V.Letter}:…", (_, _, _) => DiskService.FormatAsync(r.V, "NTFS", label), refresh: true);
    }

    [RelayCommand]
    private void Open(VolumeRow r) { if (r.V.Root.Length > 0) ShellLauncher.Open(r.V.Root); }

    [RelayCommand] private void OpenDiskMgmt() => ShellLauncher.Open("diskmgmt.msc");

    // ================= Phân tích dung lượng =================
    [RelayCommand]
    private async Task AnalyzeAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        IsBusy = true;
        BusyText = $"Đang tính dung lượng {path}…";
        _cts = new CancellationTokenSource();
        try
        {
            var list = await Task.Run(() => DiskService.Analyze(path, _cts.Token));
            AnalyzePath = path;
            Folders.Clear();
            var max = list.FirstOrDefault()?.Bytes ?? 0;
            foreach (var f in list.Take(60)) Folders.Add(new FolderRow(f, max));
            AnalyzeSummary = $"{Fmt.Size(list.Sum(f => f.Bytes))} trong {list.Count} mục" + (list.Count > 60 ? " (hiện 60 mục lớn nhất)" : "");
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task AnalyzeVolumeAsync(VolumeRow r) => AnalyzeAsync(r.V.Root);

    [RelayCommand]
    private Task DrillAsync(FolderRow f) => f.F.IsDir ? AnalyzeAsync(f.F.Path) : Task.CompletedTask;

    [RelayCommand]
    private Task UpAsync()
    {
        var parent = Path.GetDirectoryName(AnalyzePath.TrimEnd('\\'));
        return string.IsNullOrEmpty(parent) || AnalyzePath.Length <= 3 ? Task.CompletedTask : AnalyzeAsync(parent.Length == 2 ? parent + "\\" : parent);
    }

    [RelayCommand]
    private void OpenFolder(FolderRow? f)
    {
        var p = f?.F.Path ?? AnalyzePath;
        if (File.Exists(p)) ShellLauncher.Open("explorer.exe", $"/select,\"{p}\"");
        else if (Directory.Exists(p)) ShellLauncher.Open(p);
    }
}
