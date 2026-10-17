using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Zalo;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class ZaloRow(ZaloProfile p) : ObservableObject
{
    public ZaloProfile P { get; } = p;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _sizeText = "";
    public int Hue => Math.Abs(P.Name.Aggregate(17, (h, c) => h * 31 + c)) % 6;
    public string Initial => P.Name.Length > 0 ? P.Name[..1].ToUpperInvariant() : "Z";
}

public sealed partial class MultiZaloViewModel : PageViewModel
{
    public override string Title => "Multi-Zalo";
    private bool _loaded;
    private DispatcherTimer? _timer;

    public ObservableCollection<ZaloRow> Profiles { get; } = [];
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private string _zaloText = "";
    [ObservableProperty] private bool _mainRunning;
    [ObservableProperty] private string _mainSize = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _baseDir = MultiZaloService.DefaultBase;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    // ===== Chụp màn hình khi mở nhiều Zalo =====
    private const string KbKey = @"HKEY_CURRENT_USER\Control Panel\Keyboard";
    [ObservableProperty] private bool _prtScnOn;

    private void ReadPrtScn()
    {
        try { PrtScnOn = Registry.GetValue(KbKey, "PrintScreenKeyForSnippingEnabled", 0) is int v && v == 1; } catch { PrtScnOn = false; }
    }

    [RelayCommand]
    private void EnablePrtScn()
    {
        try
        {
            Registry.SetValue(KbKey, "PrintScreenKeyForSnippingEnabled", 1, RegistryValueKind.DWord);
            ReadPrtScn();
            AddLog("Đã bật: bấm PrtScn để mở công cụ chụp màn hình của Windows (có thể cần đăng xuất/đăng nhập lại).", true);
        }
        catch (Exception ex) { AddLog("Không bật được phím PrtScn: " + ex.Message, false); }
    }

    [RelayCommand]
    private void OpenSnip()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch (Exception ex) { AddLog("Không mở được công cụ chụp: " + ex.Message, false); }
    }

    public override void OnNavigatedTo()
    {
        if (!_loaded) { _loaded = true; Refresh(); ReadPrtScn(); }
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
    }

    public override void OnNavigatedFrom() => _timer?.Stop();

    private void Tick(object? s, EventArgs e)
    {
        MainRunning = MultiZaloService.IsRunning(MultiZaloService.MainDataDir);
        foreach (var r in Profiles) r.Running = MultiZaloService.IsRunning(r.P.DataDir);
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 20) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"MultiZalo: {text}"); else Log.Warn($"MultiZalo: {text}");
    }

    private void Show(OpResult r) { AddLog(r.Message, r.Ok); if (!r.Ok) DialogWindow.Info("Chưa xong", r.Message, error: true); }

    [RelayCommand]
    private void Refresh()
    {
        var v = MultiZaloService.Version();
        Installed = MultiZaloService.AppExe() is not null;
        ZaloText = Installed ? $"Zalo PC {v} · {MultiZaloService.InstallDir()}" : "Máy chưa cài Zalo PC.";
        Profiles.Clear();
        foreach (var p in MultiZaloService.Load()) Profiles.Add(new ZaloRow(p));
        if (NewName.Length == 0) NewName = $"Zalo {Profiles.Count + 2}";
        Tick(null, EventArgs.Empty);
        _ = Task.Run(() =>
        {
            var main = Fmt.Size(MultiZaloService.Size(MultiZaloService.MainDataDir));
            var sizes = Profiles.Select(r => (r, Fmt.Size(MultiZaloService.Size(r.P.Folder)))).ToList();
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                MainSize = main;
                foreach (var (r, s) in sizes) r.SizeText = s;
            });
        });
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var (name, dir) = (NewName, BaseDir);
            var r = await Task.Run(() => MultiZaloService.Create(name, dir));
            Show(r);
            if (!r.Ok) return;
            NewName = "";
            Refresh();
            if (Profiles.LastOrDefault() is { } row)
            {
                Show(MultiZaloService.CreateShortcut(row.P));
                if (Installed) Show(await Task.Run(() => MultiZaloService.Start(row.P)));
            }
        }
        catch (Exception ex) { Show(OpResult.Fail(ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PickBase()
    {
        var dlg = new OpenFolderDialog { Title = "Nơi lưu dữ liệu các hồ sơ Zalo (tin nhắn, ảnh — có thể lớn, nên chọn ổ D:)" };
        if (dlg.ShowDialog() == true) BaseDir = System.IO.Path.Combine(dlg.FolderName, "MultiZalo");
    }

    [RelayCommand] private async Task StartAsync(ZaloRow r) => Show(await Task.Run(() => MultiZaloService.Start(r.P)));
    [RelayCommand] private void StartMain() => Show(MultiZaloService.StartMain());
    [RelayCommand] private void Shortcut(ZaloRow r) => Show(MultiZaloService.CreateShortcut(r.P));
    [RelayCommand] private void OpenFolder(ZaloRow r) => ShellLauncher.Open(r.P.Folder);

    [RelayCommand]
    private void Rename(ZaloRow r)
    {
        var n = DialogWindow.AskText("Đổi tên hồ sơ", "Tên mới:", r.P.Name);
        if (string.IsNullOrWhiteSpace(n)) return;
        Show(MultiZaloService.Rename(r.P, n));
        Refresh();
    }

    [RelayCommand]
    private void Delete(ZaloRow r)
    {
        if (!DialogWindow.Confirm($"Xoá hồ sơ “{r.P.Name}”?",
                $"Xoá toàn bộ dữ liệu Zalo của hồ sơ này trên máy ({r.SizeText}): tin nhắn đã lưu, ảnh, file, phiên đăng nhập.\nTài khoản Zalo và tin nhắn trên điện thoại / máy chủ Zalo không bị ảnh hưởng.", "Xoá", danger: true))
            return;
        Show(MultiZaloService.Delete(r.P));
        Refresh();
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (!DialogWindow.Confirm("Đóng tất cả Zalo?", "Đóng mọi cửa sổ Zalo đang mở (cả Zalo chính).", "Đóng")) return;
        var n = await MultiZaloService.CloseAllAsync();
        AddLog($"Đã đóng {n} tiến trình Zalo.", true);
        Tick(null, EventArgs.Empty);
    }

    [RelayCommand]
    private void InstallZalo()
    {
        if (System.Windows.Application.Current.MainWindow?.DataContext is ShellViewModel shell)
        {
            shell.NavigateCommand.Execute(shell.Groups.SelectMany(g => g.Items).First(i => i.Key == "store"));
            if (shell.CurrentPage is StoreViewModel s) s.Filter = "Zalo";
        }
    }
}
