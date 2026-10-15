using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Windows;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class UsbViewModel : PageViewModel
{
    public override string Title => "Tạo USB boot";
    private bool _loaded;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _log = new();
    private string? _mountedIso;

    public ObservableCollection<UsbDisk> Disks { get; } = [];
    [ObservableProperty] private UsbDisk? _disk;
    [ObservableProperty] private string _disksText = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSource))] private string _sourceRoot = "";
    public bool HasSource => SourceRoot.Length > 0;
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private string _sourceText = "";

    [ObservableProperty] private bool _gpt = true;
    [ObservableProperty] private bool _mbr;
    [ObservableProperty] private string _label = "WINSETUP";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk;

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    public override void OnNavigatedFrom() { if (!IsBusy) _ = ReleaseIsoAsync(); }

    private void Append(string s) { _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {s}"); LogText = _log.ToString(); }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var list = await UsbService.ListAsync();
            var keep = Disk?.Number;
            Disks.Clear();
            foreach (var d in list) Disks.Add(d);
            Disk = Disks.FirstOrDefault(d => d.Number == keep) ?? Disks.FirstOrDefault();
            DisksText = list.Count == 0 ? "Chưa thấy USB nào — cắm USB (8 GB trở lên) rồi bấm “Làm mới”." : $"{list.Count} USB";
        }
        catch (Exception ex) { DisksText = $"Không đọc được danh sách ổ: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task PickIsoAsync()
    {
        var dlg = new OpenFileDialog { Filter = "File ISO Windows (*.iso)|*.iso", InitialDirectory = WindowsDownloads.DownloadDir };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        Status = "Đang gắn ISO…";
        try
        {
            await ReleaseIsoAsync();
            var root = await ImageService.MountAsync(dlg.FileName);
            _mountedIso = dlg.FileName;
            await UseSourceAsync(root, System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { DialogWindow.Info("Không dùng được ISO này", ex.Message, error: true); }
        finally { IsBusy = false; Status = ""; }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục bộ cài Windows (có setup.exe và thư mục sources)" };
        if (dlg.ShowDialog() != true) return;
        await ReleaseIsoAsync();
        try { await UseSourceAsync(dlg.FolderName, dlg.FolderName); }
        catch (Exception ex) { DialogWindow.Info("Không phải bộ cài Windows", ex.Message, error: true); }
    }

    private async Task UseSourceAsync(string root, string name)
    {
        var m = await ImageService.ReadMediaAsync(root);
        SourceRoot = root;
        SourceName = name;
        SourceText = m.Summary + $" · cần {Fmt.Size(UsbService.RequiredBytes(root))} trên USB";
        Label = m.Editions.FirstOrDefault()?.Version is { Length: > 0 } v && Version.TryParse(v, out var ver) && ver.Build >= 22000 ? "WIN11" : "WIN10";
    }

    private async Task ReleaseIsoAsync()
    {
        if (_mountedIso is { } iso) { _mountedIso = null; await ImageService.DismountAsync(iso); }
        SourceRoot = "";
        SourceText = SourceName = "";
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (Disk is not { } d) { DialogWindow.Info("Chưa chọn USB", "Cắm USB và chọn trong danh sách."); return; }
        if (!HasSource) { DialogWindow.Info("Chưa chọn bộ cài", "Chọn file ISO Windows (hoặc thư mục bộ cài)."); return; }
        if (!DialogWindow.Confirm($"XOÁ TOÀN BỘ dữ liệu trên {d.Name}?",
                $"{d.Display}\n\nMọi phân vùng và dữ liệu trên USB này sẽ bị xoá vĩnh viễn để tạo USB cài Windows.\nKiểm tra kỹ đúng USB trước khi đồng ý.",
                "Xoá và tạo USB", danger: true))
            return;
        IsBusy = true;
        Result = "";
        Progress = 0;
        _cts = new CancellationTokenSource();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        var (num, root, gpt, label) = (d.Number, SourceRoot, Gpt, Label);
        try
        {
            Status = "Đang tạo USB…";
            var r = await Task.Run(() => UsbService.WriteAsync(num, root, gpt, label,
                s => dispatcher.BeginInvoke(() => Append(s)),
                p => dispatcher.BeginInvoke(() => { Progress = p; Status = $"Đang tạo USB… {p}%"; }), _cts.Token));
            Result = r.Message;
            ResultOk = r.Ok;
            Append((r.Ok ? "✔ " : "✘ ") + r.Message);
            if (r.Ok) Log.Info($"USB: {r.Message}"); else Log.Warn($"USB: {r.Message}");
        }
        catch (OperationCanceledException) { Result = "Đã dừng — USB chưa dùng được, hãy tạo lại."; ResultOk = false; }
        catch (Exception ex) { Result = ex.Message; ResultOk = false; Append("✘ " + ex.Message); }
        finally { IsBusy = false; Status = ""; await RefreshAsync(); }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();
}
