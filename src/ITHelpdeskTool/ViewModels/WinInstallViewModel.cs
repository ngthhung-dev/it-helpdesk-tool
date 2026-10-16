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

public sealed partial class WinInstallViewModel : PageViewModel
{
    public override string Title => "Cài Windows";
    private bool _loaded;
    private string? _mountedIso;
    private SetupMedia? _media;

    public ObservableCollection<ReadinessItem> Readiness { get; } = [];
    [ObservableProperty] private string _readinessText = "Đang kiểm tra…";
    [ObservableProperty] private string _currentOs = "";
    [ObservableProperty] private string _winRe = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMedia))] private string _mediaName = "";
    public bool HasMedia => MediaName.Length > 0;
    [ObservableProperty] private string _mediaText = "";
    public ObservableCollection<WimEdition> Editions { get; } = [];
    [ObservableProperty] private bool _dynamicUpdate = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk;

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = CheckAsync();
    }

    public override void OnNavigatedFrom() { if (!IsBusy) _ = ReleaseAsync(); }

    [RelayCommand]
    private async Task CheckAsync()
    {
        var os = await Task.Run(() => Wmi.First("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"));
        var display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "") as string;
        CurrentOs = os is null ? "" : $"{os.Str("Caption")} {display} · build {os.Str("BuildNumber")} · {os.Str("OSArchitecture")}";
        var list = await SetupService.Windows11ReadinessAsync();
        Readiness.Clear();
        foreach (var i in list) Readiness.Add(i);
        var fails = list.Count(i => i.Pass == false);
        ReadinessText = fails == 0 ? "Máy đạt các yêu cầu phần cứng chính của Windows 11." : $"{fails} yêu cầu chưa đạt — máy không nâng cấp lên Windows 11 được theo cách thông thường.";
        WinRe = await SetupService.WinReStatusAsync();
    }

    [RelayCommand]
    private async Task PickIsoAsync()
    {
        var dlg = new OpenFileDialog { Filter = "File ISO Windows (*.iso)|*.iso", InitialDirectory = WindowsDownloads.DownloadDir };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        BusyText = "Đang gắn ISO và đọc bộ cài…";
        try
        {
            await ReleaseAsync();
            var root = await ImageService.MountAsync(dlg.FileName);
            _mountedIso = dlg.FileName;
            await UseAsync(root, System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { DialogWindow.Info("Không dùng được ISO này", ex.Message, error: true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục / USB bộ cài Windows (có setup.exe)" };
        if (dlg.ShowDialog() != true) return;
        await ReleaseAsync();
        try { await UseAsync(dlg.FolderName, dlg.FolderName); }
        catch (Exception ex) { DialogWindow.Info("Không phải bộ cài Windows", ex.Message, error: true); }
    }

    private async Task UseAsync(string root, string name)
    {
        var m = await ImageService.ReadMediaAsync(root);
        if (!System.IO.File.Exists(m.SetupExe)) throw new InvalidOperationException("Bộ cài thiếu setup.exe.");
        _media = m;
        MediaName = name;
        MediaText = m.Summary;
        Editions.Clear();
        foreach (var e in m.Editions) Editions.Add(e);
        Result = "";
    }

    private async Task ReleaseAsync()
    {
        _media = null;
        MediaName = MediaText = "";
        Editions.Clear();
        if (_mountedIso is { } iso) { _mountedIso = null; await ImageService.DismountAsync(iso); }
    }

    private void Show(Services.SysConfig.OpResult r)
    {
        Result = r.Message;
        ResultOk = r.Ok;
        if (r.Ok) Log.Info($"Setup: {r.Message}"); else Log.Warn($"Setup: {r.Message}");
    }

    [RelayCommand]
    private async Task CompatAsync()
    {
        if (_media is not { } m) return;
        IsBusy = true;
        BusyText = "Đang kiểm tra tương thích (2–10 phút, không thay đổi gì trên máy)…";
        try { Show(await SetupService.CompatScanAsync(m, _ => { }, CancellationToken.None)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RepairUpgrade()
    {
        if (_media is not { } m) return;
        if (!DialogWindow.Confirm("Cài lại Windows giữ nguyên dữ liệu?",
                "Cài lại Windows từ bộ cài này nhưng giữ nguyên ứng dụng, dữ liệu và thiết lập — cách sửa triệt để khi SFC / DISM không sửa được.\n\n" +
                "Mất 30–90 phút, máy tự khởi động lại vài lần. Cắm sạc (laptop) và lưu công việc đang mở.\n" +
                "Bộ cài phải cùng phiên bản (Home/Pro), ngôn ngữ và kiến trúc với Windows đang chạy.", "Bắt đầu"))
            return;
        Show(SetupService.StartRepairUpgrade(m, DynamicUpdate));
    }

    [RelayCommand]
    private void OpenSetup()
    {
        if (_media is not { } m) return;
        Show(SetupService.StartSetup(m));
    }

    [RelayCommand]
    private async Task RebootRecoveryAsync()
    {
        if (!DialogWindow.Confirm("Khởi động vào Advanced Startup?",
                "Máy khởi động lại sau 10 giây vào menu khôi phục: Startup Repair, System Restore, khởi động từ USB, UEFI Firmware Settings… Lưu công việc trước khi đồng ý.", "Khởi động lại"))
            return;
        await SetupService.RebootToRecoveryAsync();
    }

    [RelayCommand]
    private void OpenRecovery() => ShellLauncher.Open("ms-settings:recovery");
}
