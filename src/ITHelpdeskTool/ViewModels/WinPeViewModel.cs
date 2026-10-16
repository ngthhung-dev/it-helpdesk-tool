using System.Collections.ObjectModel;
using System.IO;
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

public sealed partial class WinPeViewModel : PageViewModel
{
    public override string Title => "WinPE";
    private bool _loaded;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _log = new();

    [ObservableProperty] private bool _hasAdk;
    [ObservableProperty] private string _adkText = "Đang kiểm tra…";

    public List<Pick<PeComponent>> Components { get; } = WinPeService.Components.OrderByVi(c => c.Name).Select(c => new Pick<PeComponent>(c, c.DefaultOn)).ToList();
    [ObservableProperty] private string _workDir = @"C:\WinPE_amd64";
    [ObservableProperty] private string _driversDir = "";
    [ObservableProperty] private string _toolsDir = "";
    [ObservableProperty] private string _startCommands = "";
    [ObservableProperty] private bool _toIso = true;
    [ObservableProperty] private bool _toUsb;
    [ObservableProperty] private string _isoPath = Path.Combine(WindowsDownloads.DownloadDir, "WinPE_amd64.iso");
    public ObservableCollection<UsbDisk> Disks { get; } = [];
    [ObservableProperty] private UsbDisk? _disk;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk;

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = CheckAdkAsync();
        _ = RefreshUsbAsync();
    }

    private void CheckAdk()
    {
        HasAdk = WinPeService.HasAdk && WinPeService.HasPeAddon;
        AdkText = HasAdk ? $"Windows ADK {WinPeService.AdkVersion()} + WinPE add-on đã cài."
            : WinPeService.HasAdk ? "Đã có Windows ADK nhưng thiếu WinPE add-on."
            : "Chưa có Windows ADK — bộ công cụ miễn phí của Microsoft để tạo WinPE (tải khoảng 1–2 GB).";
    }

    private async Task CheckAdkAsync()
    {
        var (has, addon, ver) = await Task.Run(() => (WinPeService.HasAdk, WinPeService.HasPeAddon, WinPeService.HasAdk ? WinPeService.AdkVersion() : ""));
        HasAdk = has && addon;
        AdkText = HasAdk ? $"Windows ADK {ver} + WinPE add-on đã cài."
            : has ? "Đã có Windows ADK nhưng thiếu WinPE add-on."
            : "Chưa có Windows ADK — bộ công cụ miễn phí của Microsoft để tạo WinPE (tải khoảng 1–2 GB).";
    }

    private void Append(string s) { _log.AppendLine(s); if (_log.Length > 200_000) _log.Remove(0, 50_000); LogText = _log.ToString(); }

    [RelayCommand]
    private async Task RefreshUsbAsync()
    {
        try
        {
            var list = await UsbService.ListAsync();
            Disks.Clear();
            foreach (var d in list) Disks.Add(d);
            Disk ??= Disks.FirstOrDefault();
        }
        catch { }
    }

    private async Task RunAsync(string title, Func<Action<string>, Action<string, int>, CancellationToken, Task<OpResult>> f)
    {
        IsBusy = true;
        Result = "";
        _cts = new CancellationTokenSource();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        Append($"══════ {title} · {DateTime.Now:HH:mm:ss} ══════");
        try
        {
            var r = await Task.Run(() => f(
                s => dispatcher.BeginInvoke(() => Append(s)),
                (s, p) => dispatcher.BeginInvoke(() => { Status = s; Progress = p; }), _cts.Token));
            Result = r.Message;
            ResultOk = r.Ok;
            Append((r.Ok ? "✔ " : "✘ ") + r.Message);
            if (r.Ok) Log.Info($"WinPE: {r.Message}"); else Log.Warn($"WinPE: {r.Message}");
        }
        catch (OperationCanceledException) { Result = "Đã dừng."; ResultOk = false; }
        catch (Exception ex) { Result = ex.Message; ResultOk = false; Append("✘ " + ex.Message); }
        finally { IsBusy = false; Status = ""; CheckAdk(); }
    }

    [RelayCommand]
    private Task InstallAdkAsync() => RunAsync("Cài Windows ADK", (log, step, ct) => { step("Đang cài Windows ADK + WinPE add-on…", 0); return WinPeService.InstallAdkAsync(log, ct); });

    [RelayCommand]
    private void PickDrivers() { var d = new OpenFolderDialog { Title = "Thư mục driver (.inf) — vd driver ổ cứng RAID / NVMe, card mạng" }; if (d.ShowDialog() == true) DriversDir = d.FolderName; }

    [RelayCommand]
    private void PickTools() { var d = new OpenFolderDialog { Title = "Thư mục công cụ portable chép vào WinPE (X:\\Tools)" }; if (d.ShowDialog() == true) ToolsDir = d.FolderName; }

    [RelayCommand]
    private void PickIso() { var d = new SaveFileDialog { Filter = "File ISO (*.iso)|*.iso", FileName = Path.GetFileName(IsoPath) }; if (d.ShowDialog() == true) IsoPath = d.FileName; }

    [RelayCommand]
    private void ClearDir(string which) { if (which == "drivers") DriversDir = ""; else ToolsDir = ""; }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (!HasAdk) { DialogWindow.Info("Chưa có Windows ADK", "Bấm “Cài Windows ADK” trước."); return; }
        if (ToUsb && Disk is null) { DialogWindow.Info("Chưa chọn USB", "Cắm USB và bấm “Làm mới”."); return; }
        if (ToUsb && !DialogWindow.Confirm($"XOÁ TOÀN BỘ dữ liệu trên {Disk!.Name}?", $"{Disk.Display}\n\nUSB sẽ được định dạng lại để chứa WinPE.", "Tạo WinPE", danger: true))
            return;
        var o = new PeOptions
        {
            WorkDir = WorkDir, Components = Components.Where(c => c.IsChecked).Select(c => c.Item).ToList(),
            DriversDir = DriversDir, ToolsDir = ToolsDir, StartCommands = StartCommands,
        };
        var (toIso, iso, disk) = (ToIso, IsoPath, Disk);
        await RunAsync("Tạo WinPE", async (log, step, ct) =>
        {
            var r = await WinPeService.BuildAsync(o, log, step, ct);
            if (!r.Ok) return r;
            step(toIso ? "Tạo file ISO" : "Ghi ra USB", 94);
            var m = toIso ? await WinPeService.MakeIsoAsync(o.WorkDir, iso, log, ct) : await WinPeService.MakeUsbAsync(o.WorkDir, disk!, log, ct);
            step("Xong", 100);
            return m.Ok ? OpResult.Success(m.Message + (toIso ? " Ghi ISO ra USB bằng Rufus / Ventoy (có trong App Store), hoặc khởi động máy ảo từ ISO." : "")) : m;
        });
        if (ResultOk && ToIso && File.Exists(IsoPath)) ShellLauncher.Open("explorer.exe", $"/select,\"{IsoPath}\"");
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void OpenAdkPage() => ShellLauncher.Open("https://learn.microsoft.com/windows-hardware/get-started/adk-install");
}
