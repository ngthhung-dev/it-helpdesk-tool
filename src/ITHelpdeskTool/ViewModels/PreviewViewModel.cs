using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Preview;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class PreviewViewModel : PageViewModel
{
    public override string Title => "Xem trước file";
    private bool _loaded;
    private bool _loading;

    [ObservableProperty] private bool _previewHandlers;
    [ObservableProperty] private bool _thumbnails;
    [ObservableProperty] private bool _networkThumbnails;
    public ObservableCollection<PreviewSupport> Supports { get; } = [];
    [ObservableProperty] private string _supportSummary = "";

    // Kiểm tra file
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReport))] private FileReport? _report;
    public bool HasReport => Report is not null;
    [ObservableProperty] private BitmapImage? _image;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
        if (ShellViewModel.StartSubPage == "demo")
            _ = InspectAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"));
    }

    private void Load()
    {
        _loading = true;
        PreviewHandlers = PreviewService.PreviewHandlersOn;
        Thumbnails = PreviewService.ThumbnailsOn;
        NetworkThumbnails = PreviewService.NetworkThumbnailsOn;
        _loading = false;
        Supports.Clear();
        foreach (var s in PreviewService.Supports()) Supports.Add(s);
        SupportSummary = $"Xem trước được {Supports.Count(s => s.Ok)}/{Supports.Count} loại file thường gặp";
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 20) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Preview: {text}"); else Log.Warn($"Preview: {text}");
    }

    partial void OnPreviewHandlersChanged(bool value) { if (!_loading) { PreviewService.PreviewHandlersOn = value; AddLog(value ? "Đã bật xem trước trong khung Preview." : "Đã tắt xem trước trong khung Preview.", true); } }
    partial void OnThumbnailsChanged(bool value) { if (!_loading) { PreviewService.ThumbnailsOn = value; AddLog(value ? "Đã bật ảnh thu nhỏ." : "Đã tắt ảnh thu nhỏ (chỉ hiện biểu tượng).", true); } }
    partial void OnNetworkThumbnailsChanged(bool value) { if (!_loading) { PreviewService.NetworkThumbnailsOn = value; AddLog(value ? "Đã bật ảnh thu nhỏ trên thư mục mạng." : "Đã tắt ảnh thu nhỏ trên thư mục mạng.", true); } }

    [RelayCommand] private void Refresh() => Load();

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        var r = await Services.Tweaks.TweakService.RestartExplorerAsync();
        AddLog(r.Message, r.Ok);
    }

    [RelayCommand]
    private async Task RebuildThumbsAsync()
    {
        IsBusy = true;
        BusyText = "Đang làm mới bộ đệm ảnh thu nhỏ…";
        try
        {
            var r = await Task.Run(() => Services.Windows.RepairService.RebuildIconCacheAsync(_ => { }, CancellationToken.None));
            AddLog(r.Message, r.Ok);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UnblockFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Thư mục chứa file tải từ Internet không xem trước được (vd Downloads)" };
        if (dlg.ShowDialog() != true) return;
        if (!DialogWindow.Confirm("Bỏ chặn file tải từ Internet?",
                $"Gỡ đánh dấu “tải từ Internet” cho mọi file trong {dlg.FolderName}. Explorer sẽ xem trước được, nhưng Windows cũng không còn cảnh báo khi mở các file này.\n\nChỉ làm với file từ nguồn bạn tin cậy.", "Bỏ chặn", danger: true))
            return;
        IsBusy = true;
        BusyText = "Đang bỏ chặn…";
        try { var r = await PreviewService.UnblockFolderAsync(dlg.FolderName); AddLog(r.Message, r.Ok); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void InstallTool(string id)
    {
        if (System.Windows.Application.Current.MainWindow?.DataContext is ShellViewModel shell)
        {
            var nav = shell.Groups.SelectMany(g => g.Items).First(i => i.Key == "store");
            shell.NavigateCommand.Execute(nav);
            if (shell.CurrentPage is StoreViewModel s) s.Filter = id == "QL-Win.QuickLook" ? "QuickLook" : "PowerToys";
        }
    }

    // ================= Kiểm tra file =================
    [RelayCommand]
    private async Task PickFileAsync()
    {
        var dlg = new OpenFileDialog { Title = "Chọn file cần kiểm tra", Filter = "Mọi file|*.*" };
        if (dlg.ShowDialog() == true) await InspectAsync(dlg.FileName);
    }

    public async Task InspectAsync(string path)
    {
        IsBusy = true;
        BusyText = "Đang kiểm tra file…";
        try
        {
            Report = await PreviewService.InspectAsync(path);
            Image = null;
            if (Report.IsImage)
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 900;
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    Image = b;
                }
                catch { }
            }
        }
        catch (Exception ex) { AddLog($"Không đọc được file: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void OpenFile() { if (Report is { } r) ShellLauncher.Open("explorer.exe", $"\"{r.Path}\""); }
    [RelayCommand] private void ShowInFolder() { if (Report is { } r) ShellLauncher.Open("explorer.exe", $"/select,\"{r.Path}\""); }
    [RelayCommand] private void CopyHash() { if (Report is { } r) try { System.Windows.Clipboard.SetText(r.Sha256); AddLog("Đã chép SHA-256.", true); } catch { } }
    [RelayCommand] private void VirusTotal() { if (Report is { } r) ShellLauncher.Open($"https://www.virustotal.com/gui/file/{r.Sha256}"); }

    [RelayCommand]
    private async Task UnblockFileAsync()
    {
        if (Report is not { } r) return;
        if (!DialogWindow.Confirm("Bỏ chặn file này?", "Windows sẽ không còn cảnh báo khi mở file. Chỉ làm khi chắc chắn file an toàn.", "Bỏ chặn", danger: true)) return;
        var res = PreviewService.Unblock(r.Path);
        AddLog(res.Message, res.Ok);
        await InspectAsync(r.Path);
    }
}
