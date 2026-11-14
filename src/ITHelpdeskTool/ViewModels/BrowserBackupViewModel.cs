using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Backup;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class BrowserRow(Browser b) : ObservableObject
{
    [ObservableProperty] private Browser _b = b;
    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusOk = true;
    public string SizeText => B.Size > 0 ? Fmt.Size(B.Size) : "";
}

public sealed partial class BrowserBackupViewModel : PageViewModel
{
    public override string Title => "Sao lưu trình duyệt";
    private bool _loaded;

    public ObservableCollection<BrowserRow> Items { get; } = [];
    [ObservableProperty] private string _summary = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
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
        if (ok) Log.Info($"Trình duyệt: {text}"); else Log.Warn($"Trình duyệt: {text}");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = "Đang tìm trình duyệt…";
        try
        {
            var list = await BrowserBackupService.DetectAsync();
            Items.Clear();
            foreach (var b in list) Items.Add(new BrowserRow(b));
            Summary = list.Count == 0 ? "Không tìm thấy trình duyệt nào có dữ liệu trên máy."
                : $"{list.Count} trình duyệt · {Fmt.Size(list.Sum(b => b.Size))}";
        }
        catch (Exception ex) { AddLog($"Không đọc được danh sách: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    private async Task<bool> CloseIfRunning(IEnumerable<BrowserRow> rows)
    {
        var running = rows.Where(r => BrowserBackupService.RunningProcs(r.B).Count > 0).ToList();
        if (running.Count == 0) return true;
        var names = string.Join(", ", running.Select(r => r.B.Name));
        if (!DialogWindow.Confirm($"Đóng {names}?", $"{names} đang mở nên chưa chép được dữ liệu. Đóng {names} ngay (nhớ lưu việc đang làm)?", "Đóng và tiếp tục"))
            return false;
        IsBusy = true;
        BusyText = $"Đang đóng {names}…";
        try { foreach (var r in running) await BrowserBackupService.CloseAsync(r.B); }
        finally { IsBusy = false; }
        return true;
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var sel = Items.Where(r => r.IsChecked).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn trình duyệt", "Tích chọn trình duyệt muốn sao lưu."); return; }
        if (!DialogWindow.Confirm("Sao lưu trình duyệt?",
                "Bản sao lưu gồm bookmark, mật khẩu đã lưu, cookie, tiện ích… của mọi hồ sơ. Vì có chứa dữ liệu đăng nhập, hãy lưu ra USB / ổ riêng và giữ an toàn.\n\n" +
                "Các trình duyệt được chọn sẽ được đóng trong lúc sao lưu.", "Sao lưu"))
            return;
        var dlg = new OpenFolderDialog { Title = $"Nơi lưu bản sao (khoảng {Fmt.Size(sel.Sum(r => r.B.Size))}) — nên chọn USB / ổ khác" };
        if (dlg.ShowDialog() != true) return;
        if (!await CloseIfRunning(sel)) return;

        IsBusy = true;
        var set = BrowserBackupService.NewSetDir(dlg.FolderName);
        var lines = new List<string>();
        int ok = 0, fail = 0;
        var disp = System.Windows.Application.Current.Dispatcher;
        try
        {
            Directory.CreateDirectory(set);
            foreach (var r in sel)
            {
                r.Status = "Đang sao lưu…"; r.StatusOk = true;
                var res = await BrowserBackupService.BackupAsync(r.B, set, s => disp.BeginInvoke(() => BusyText = s), CancellationToken.None);
                r.Status = res.Ok ? "Đã sao lưu" : $"Lỗi: {res.Message}"; r.StatusOk = res.Ok;
                lines.Add($"[{r.B.Name}] {(res.Ok ? "OK" : "LỖI")} — {res.Message}");
                AddLog(res.Message, res.Ok);
                if (res.Ok) ok++; else fail++;
            }
            BrowserBackupService.WriteManifest(set, lines);
        }
        catch (Exception ex) { AddLog(ex.Message, false); fail++; }
        finally { IsBusy = false; }

        if (fail > 0) DialogWindow.Info("Sao lưu chưa trọn vẹn", $"Đã sao lưu {ok}/{sel.Count} trình duyệt vào {set}. {fail} mục lỗi — xem nhật ký.", error: true);
        if (ok > 0) ShellLauncher.Open(set);
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục sao lưu trình duyệt (TrinhDuyet-…)" };
        if (dlg.ShowDialog() != true) return;
        var inBackup = BrowserBackupService.InBackup(dlg.FolderName);
        if (inBackup.Count == 0) { DialogWindow.Info("Không có dữ liệu", "Thư mục này không chứa bản sao lưu trình duyệt nào."); return; }
        if (!DialogWindow.Confirm($"Khôi phục {inBackup.Count} trình duyệt?",
                string.Join(", ", inBackup.Select(b => b.Name)) +
                "\n\nDữ liệu hiện tại của các trình duyệt này sẽ bị ghi đè. Trình duyệt sẽ được đóng trong lúc khôi phục.\n" +
                "Lưu ý: nếu đây là máy / Windows khác với lúc sao lưu, bookmark về được nhưng mật khẩu đã lưu có thể không mở được.",
                "Khôi phục", danger: true))
            return;
        var rows = inBackup.Select(b => new BrowserRow(b)).ToList();
        if (!await CloseIfRunning(rows)) return;

        IsBusy = true;
        int ok = 0, fail = 0;
        var disp = System.Windows.Application.Current.Dispatcher;
        try
        {
            foreach (var b in inBackup)
            {
                var res = await BrowserBackupService.RestoreAsync(b, dlg.FolderName, s => disp.BeginInvoke(() => BusyText = s), CancellationToken.None);
                AddLog(res.Message, res.Ok);
                if (res.Ok) ok++; else fail++;
            }
        }
        finally { IsBusy = false; }
        DialogWindow.Info(fail == 0 ? "Đã khôi phục" : "Khôi phục chưa trọn vẹn",
            $"Đã khôi phục {ok}/{inBackup.Count} trình duyệt. Mở trình duyệt để kiểm tra bookmark và mật khẩu.", error: fail > 0);
        await RefreshAsync();
    }

    /// <summary>Cho trường hợp cài lại Windows: mở trang Export mật khẩu của trình duyệt (trình duyệt tự bắt xác thực Windows).</summary>
    [RelayCommand]
    private void ExportPasswords(BrowserRow r)
    {
        var res = BrowserBackupService.OpenPasswordPage(r.B);
        AddLog(res.Message, res.Ok);
        if (!res.Ok) DialogWindow.Info("Không mở được", res.Message, error: true);
    }
}
