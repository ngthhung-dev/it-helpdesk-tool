using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Outlook;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class DataFileRow(OutlookDataFile f) : ObservableObject
{
    public OutlookDataFile F { get; } = f;
    [ObservableProperty] private bool _isChecked = !f.IsOst;
}

public sealed partial class OutlookViewModel : PageViewModel
{
    public override string Title => "Dữ liệu Outlook";
    private bool _loaded;

    public ObservableCollection<DataFileRow> Files { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [];
    [ObservableProperty] private string _outlookText = "";
    [ObservableProperty] private bool _hasOutlook;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _defaultProfile = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private int _progress;
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

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
        if (ok) Log.Info($"Outlook: {text}"); else Log.Warn($"Outlook: {text}");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var exe = OutlookService.OutlookExe();
        HasOutlook = exe is not null;
        OutlookText = exe is null ? "Máy chưa cài Outlook (classic) — vẫn tìm và sao lưu được file PST/OST có sẵn."
            : $"Outlook: {System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion}" + (OutlookService.IsRunning() ? " · đang mở" : "");
        var list = await Task.Run(() => OutlookService.FindDataFiles());
        Files.Clear();
        foreach (var f in list) Files.Add(new DataFileRow(f));
        Summary = list.Count == 0 ? "Không tìm thấy file PST / OST ở các vị trí mặc định." : $"{list.Count} file · {Fmt.Size(list.Sum(f => f.Size))}";
        var (profiles, def) = OutlookService.Profiles();
        Profiles.Clear();
        foreach (var p in profiles) Profiles.Add(p);
        DefaultProfile = def;
    }

    private async Task Run(string busy, Func<Task<OpResult>> f, bool refresh = true)
    {
        IsBusy = true;
        BusyText = busy;
        try
        {
            var r = await f();
            AddLog(r.Message, r.Ok);
            if (!r.Ok) DialogWindow.Info("Chưa xong", r.Message, error: true);
        }
        catch (Exception ex) { AddLog(ex.Message, false); }
        finally { IsBusy = false; Progress = 0; }
        if (refresh) await RefreshAsync();
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Tìm thêm file PST / OST trong thư mục (vd ổ D:)" };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        BusyText = "Đang tìm file PST / OST…";
        try
        {
            var more = await Task.Run(() => OutlookService.FindDataFiles([dlg.FolderName]));
            foreach (var f in more.Where(m => Files.All(x => !x.F.Path.Equals(m.Path, StringComparison.OrdinalIgnoreCase)))) Files.Add(new DataFileRow(f));
            Summary = $"{Files.Count} file · {Fmt.Size(Files.Sum(f => f.F.Size))}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var sel = Files.Where(f => f.IsChecked).Select(f => f.F).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn file", "Tích các file muốn sao lưu."); return; }
        if (OutlookService.IsRunning())
        {
            if (!DialogWindow.Confirm("Đóng Outlook?", "Outlook đang mở và khoá file dữ liệu. Đóng Outlook để sao lưu?", "Đóng Outlook")) return;
            await OutlookService.CloseAsync();
        }
        var dlg = new OpenFolderDialog { Title = $"Nơi lưu bản sao ({Fmt.Size(sel.Sum(f => f.Size))}) — nên chọn ổ khác / ổ cứng ngoài" };
        if (dlg.ShowDialog() != true) return;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        await Run("Đang sao lưu…", async () =>
        {
            var r = await OutlookService.BackupAsync(sel, dlg.FolderName, p => dispatcher.BeginInvoke(() => { Progress = p; BusyText = $"Đang sao lưu… {p}%"; }), CancellationToken.None);
            if (r.Ok) ShellLauncher.Open(dlg.FolderName);
            return r;
        }, refresh: false);
    }

    [RelayCommand]
    private Task RebuildOstAsync(DataFileRow r) =>
        DialogWindow.Confirm($"Dựng lại {r.F.Name}?",
            "Dùng khi Outlook báo lỗi file OST, không đồng bộ, thiếu thư. File cũ được đổi tên thành .bak; Outlook tải lại toàn bộ hộp thư từ máy chủ.\n\n" +
            "Thư nháp / thư chưa gửi chỉ có trên máy (chưa đồng bộ) sẽ không còn trong hộp thư mới.", "Dựng lại", danger: true)
            ? Run("Đang dựng lại OST…", () => OutlookService.RebuildOstAsync(r.F)) : Task.CompletedTask;

    [RelayCommand] private void OpenFolder(DataFileRow r) => ShellLauncher.Open("explorer.exe", $"/select,\"{r.F.Path}\"");
    [RelayCommand] private void ScanPst() { var r = OutlookService.OpenScanPst(); AddLog(r.Message, r.Ok); if (!r.Ok) DialogWindow.Info("Không mở được", r.Message, error: true); }
    [RelayCommand] private void MailSetup() { var r = OutlookService.OpenMailControlPanel(); AddLog(r.Message, r.Ok); if (!r.Ok) DialogWindow.Info("Không mở được", r.Message, error: true); }
    [RelayCommand] private Task StartWithAsync(string sw) => Run($"Đang mở Outlook {sw}…", () => OutlookService.StartWithAsync(sw), refresh: false);

    [RelayCommand]
    private void BackupSignatures()
    {
        var dlg = new SaveFileDialog { Filter = "Zip (*.zip)|*.zip", FileName = $"Outlook-Signatures-{Environment.UserName}-{DateTime.Now:yyyyMMdd}.zip" };
        if (dlg.ShowDialog() != true) return;
        var r = OutlookService.BackupSignatures(dlg.FileName);
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa sao lưu", r.Message);
    }

    [RelayCommand]
    private void RestoreSignatures()
    {
        var dlg = new OpenFileDialog { Filter = "Zip (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;
        try { var r = OutlookService.RestoreSignatures(dlg.FileName); AddLog(r.Message, r.Ok); }
        catch (Exception ex) { DialogWindow.Info("Không khôi phục được", ex.Message, error: true); }
    }

    [RelayCommand] private void OpenSignatures() { System.IO.Directory.CreateDirectory(OutlookService.SignaturesDir); ShellLauncher.Open(OutlookService.SignaturesDir); }
}
