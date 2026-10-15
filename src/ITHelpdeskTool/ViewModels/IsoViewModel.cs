using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Windows;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class IsoViewModel : PageViewModel
{
    public override string Title => "Tải ISO";

    public IReadOnlyList<OfficialDownload> Downloads => WindowsDownloads.Items;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageOk = true;

    // Kiểm tra ISO
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIso))] private string _isoPath = "";
    public bool HasIso => IsoPath.Length > 0;
    [ObservableProperty] private string _isoSize = "";
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private string _hashResult = "";
    [ObservableProperty] private string _hashTone = "";
    [ObservableProperty] private int _hashProgress;
    [ObservableProperty] private string _mediaText = "";
    [ObservableProperty] private string _mountedRoot = "";
    public ObservableCollection<WimEdition> Editions { get; } = [];

    partial void OnExpectedChanged(string value) => Compare();

    private void Say(string text, bool ok) { Message = text; MessageOk = ok; if (ok) Log.Info($"ISO: {text}"); else Log.Warn($"ISO: {text}"); }

    [RelayCommand]
    private async Task GetAsync(OfficialDownload d)
    {
        if (!d.Tool) { ShellLauncher.Open(d.Url); return; }
        IsBusy = true;
        BusyText = $"Đang tải {d.Name}…";
        try
        {
            var path = await WindowsDownloads.DownloadToolAsync(d, (done, total) =>
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => BusyText = $"Đang tải {d.FileName} · {Services.Store.StoreInstaller.Progress(done, total)}"));
            Say($"Đã tải {Path.GetFileName(path)} (chữ ký Microsoft hợp lệ) vào thư mục Downloads.", true);
            ShellLauncher.Open(path);
        }
        catch (Exception ex) { Say($"Không tải được: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenDownloads() => ShellLauncher.Open(WindowsDownloads.DownloadDir);

    // ================= Kiểm tra ISO =================
    [RelayCommand]
    private async Task PickIsoAsync()
    {
        var dlg = new OpenFileDialog { Filter = "File ISO (*.iso)|*.iso", InitialDirectory = WindowsDownloads.DownloadDir };
        if (dlg.ShowDialog() != true) return;
        await UnmountAsync();
        IsoPath = dlg.FileName;
        IsoSize = Fmt.Size(new FileInfo(IsoPath).Length);
        Hash = HashResult = MediaText = "";
        HashProgress = 0;
        Editions.Clear();
        await InspectAsync();
    }

    [RelayCommand]
    private async Task HashAsync()
    {
        if (!HasIso) return;
        IsBusy = true;
        BusyText = "Đang tính SHA-256…";
        try
        {
            var prog = new Progress<int>(p => { HashProgress = p; BusyText = $"Đang tính SHA-256… {p}%"; });
            var path = IsoPath;
            Hash = await Task.Run(() => ImageService.Sha256Async(path, prog));
            Compare();
        }
        catch (Exception ex) { Say(ex.Message, false); }
        finally { IsBusy = false; }
    }

    private void Compare()
    {
        if (Hash.Length == 0) { HashResult = ""; return; }
        var exp = ImageService.NormalizeHash(Expected);
        if (exp.Length == 0) { HashResult = "Dán mã SHA-256 trên trang tải của Microsoft để so khớp."; HashTone = ""; }
        else if (exp == Hash) { HashResult = "Khớp — file ISO nguyên vẹn, đúng bản Microsoft phát hành."; HashTone = "Good"; }
        else { HashResult = "KHÔNG khớp — file bị lỗi khi tải hoặc không phải bản gốc. Không nên dùng."; HashTone = "Bad"; }
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        if (!HasIso) return;
        IsBusy = true;
        BusyText = "Đang gắn ISO và đọc bộ cài…";
        try
        {
            MountedRoot = await ImageService.MountAsync(IsoPath);
            var m = await ImageService.ReadMediaAsync(MountedRoot);
            MediaText = m.Summary + (m.Over4Gb ? " · install.wim lớn hơn 4 GB (tạo USB FAT32 sẽ tự chia nhỏ)" : "");
            Editions.Clear();
            foreach (var e in m.Editions) Editions.Add(e);
        }
        catch (Exception ex) { MediaText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenMounted() { if (MountedRoot.Length > 0) ShellLauncher.Open(MountedRoot); }

    [RelayCommand]
    private async Task UnmountAsync()
    {
        if (MountedRoot.Length == 0 || IsoPath.Length == 0) return;
        await ImageService.DismountAsync(IsoPath);
        MountedRoot = "";
    }

    public override void OnNavigatedFrom() => _ = UnmountAsync();
}
