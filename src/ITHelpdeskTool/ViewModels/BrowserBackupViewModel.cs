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

    // Luồng xuất mật khẩu từng hồ sơ
    [ObservableProperty] private bool _exportActive;
    [ObservableProperty] private string _exportCurrent = "";
    [ObservableProperty] private string _exportNextLabel = "";
    private List<BrowserBackupService.ExportTarget> _targets = [];
    private int _exportIdx;

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

    private List<BrowserRow> Checked() => Items.Where(r => r.IsChecked).ToList();

    private async Task<bool> CloseIfRunning(IEnumerable<BrowserRow> rows)
    {
        var running = rows.Where(r => BrowserBackupService.RunningProcs(r.B).Count > 0).ToList();
        if (running.Count == 0) return true;
        var names = string.Join(", ", running.Select(r => r.B.Name));
        if (!DialogWindow.Confirm($"Đóng {names}?", $"{names} đang mở nên chưa thao tác được. Đóng {names} ngay (nhớ lưu việc đang làm)?", "Đóng và tiếp tục"))
            return false;
        IsBusy = true;
        BusyText = $"Đang đóng {names}…";
        try { foreach (var r in running) await BrowserBackupService.CloseAsync(r.B); }
        finally { IsBusy = false; }
        return true;
    }

    // ================= Sao lưu (chép file) =================
    [RelayCommand] private Task BackupBookmarksPasswordsAsync() => BackupAsync(BackupMode.BookmarksPasswords,
        "Sao lưu bookmark + mật khẩu", "SaoLuu-BM-MK",
        "Sao lưu bookmark và mật khẩu đã lưu của mọi hồ sơ. Khôi phục trên cùng máy thì mật khẩu mở lại được. Bản sao lưu có chứa dữ liệu đăng nhập — giữ ở nơi an toàn.");

    [RelayCommand] private Task BackupCookiesAsync() => BackupAsync(BackupMode.Cookies,
        "Sao lưu cookie", "SaoLuu-Cookie",
        "Sao lưu toàn bộ cookie (phiên đăng nhập các web) của mọi hồ sơ. Khôi phục trên cùng máy thì giữ được đăng nhập các web.");

    private async Task BackupAsync(BackupMode mode, string title, string prefix, string note)
    {
        var sel = Checked();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn trình duyệt", "Tích chọn trình duyệt muốn sao lưu."); return; }
        if (!DialogWindow.Confirm($"{title}?", note + "\n\nCác trình duyệt được chọn sẽ được đóng trong lúc sao lưu.", title)) return;
        var dlg = new OpenFolderDialog { Title = $"Nơi lưu ({title}) — nên chọn USB / ổ khác" };
        if (dlg.ShowDialog() != true) return;
        if (!await CloseIfRunning(sel)) return;

        IsBusy = true;
        var set = BrowserBackupService.NewSetDir(dlg.FolderName, prefix);
        var lines = new List<string>();
        int ok = 0, fail = 0;
        var disp = System.Windows.Application.Current.Dispatcher;
        try
        {
            Directory.CreateDirectory(set);
            foreach (var r in sel)
            {
                var res = await BrowserBackupService.BackupAsync(r.B, mode, set, s => disp.BeginInvoke(() => BusyText = s), CancellationToken.None);
                lines.Add($"[{r.B.Name}] {res.Message}");
                AddLog(res.Message, res.Ok);
                if (res.Ok) ok++; else fail++;
            }
            BrowserBackupService.WriteManifest(set, title.ToUpperInvariant() + " (IT Helpdesk Tool)", lines);
        }
        catch (Exception ex) { AddLog(ex.Message, false); fail++; }
        finally { IsBusy = false; }

        if (ok > 0) ShellLauncher.Open(set);
        if (fail > 0) DialogWindow.Info("Chưa trọn vẹn", $"Đã xong {ok}/{sel.Count} trình duyệt. {fail} mục không có dữ liệu hoặc lỗi — xem nhật ký.", error: true);
    }

    // ================= Xuất bookmark (HTML) + mật khẩu (trang Export của trình duyệt) =================
    [RelayCommand]
    private async Task ExportBookmarksPasswordsAsync()
    {
        var sel = Checked();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn trình duyệt", "Tích chọn trình duyệt muốn xuất."); return; }
        if (!DialogWindow.Confirm("Xuất bookmark + mật khẩu?",
                "App xuất bookmark ra file HTML. Mật khẩu thì app mở trang Export của từng trình duyệt — bạn bấm Export trên đó, trình duyệt sẽ hỏi xác thực Windows rồi lưu file.\n\n" +
                "Lưu ý: file xuất ra là dữ liệu đăng nhập, dùng xong hãy xóa.", "Xuất"))
            return;
        var dlg = new OpenFolderDialog { Title = "Nơi lưu file bookmark HTML" };
        if (dlg.ShowDialog() != true) return;
        var dest = BrowserBackupService.NewSetDir(dlg.FolderName, "XuatTrinhDuyet");

        IsBusy = true;
        BusyText = "Đang xuất bookmark…";
        int ok = 0;
        try
        {
            foreach (var r in sel)
            {
                var res = await BrowserBackupService.ExportBookmarksAsync(r.B, dest);
                AddLog(res.Message, res.Ok);
                if (res.Ok) ok++;
            }
        }
        finally { IsBusy = false; }
        if (ok > 0) ShellLauncher.Open(dest);

        // Mật khẩu: mở trang Export lần lượt từng hồ sơ của từng trình duyệt
        _targets = BrowserBackupService.PasswordTargets(sel.Select(r => r.B));
        if (_targets.Count == 0) { DialogWindow.Info("Xong", "Đã xuất bookmark. Không có hồ sơ nào để xuất mật khẩu."); return; }
        _exportIdx = -1;
        ExportActive = true;
        OpenNextExport();
    }

    /// <summary>Mở trang xuất mật khẩu của hồ sơ kế tiếp trong danh sách.</summary>
    [RelayCommand]
    private void NextExport() => OpenNextExport();

    private void OpenNextExport()
    {
        _exportIdx++;
        if (_exportIdx >= _targets.Count)
        {
            ExportActive = false;
            DialogWindow.Info("Đã xong tất cả hồ sơ", "Đã mở hết các hồ sơ để xuất mật khẩu. File mật khẩu do trình duyệt lưu — dùng xong nhớ xóa.");
            return;
        }
        var t = _targets[_exportIdx];
        var res = BrowserBackupService.OpenPasswordPage(t.B, t.ProfileDir);
        AddLog($"{t.Label}: {res.Message}", res.Ok);
        ExportCurrent = $"Hồ sơ {_exportIdx + 1}/{_targets.Count}: {t.Label}";
        var next = _exportIdx + 1 < _targets.Count ? _targets[_exportIdx + 1].Label : "";
        ExportNextLabel = next.Length > 0 ? $"Xuất xong hồ sơ này → mở tiếp: {next}" : "Đây là hồ sơ cuối — bấm để kết thúc";
    }

    [RelayCommand]
    private void CancelExport() { ExportActive = false; _targets = []; }

    // ================= Khôi phục =================
    [RelayCommand]
    private async Task RestoreAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục sao lưu (SaoLuu-BM-MK… / SaoLuu-Cookie…)" };
        if (dlg.ShowDialog() != true) return;
        var inBackup = BrowserBackupService.InBackup(dlg.FolderName);
        if (inBackup.Count == 0) { DialogWindow.Info("Không có dữ liệu", "Thư mục này không chứa bản sao lưu trình duyệt nào."); return; }
        if (!DialogWindow.Confirm($"Khôi phục {inBackup.Count} trình duyệt?",
                string.Join(", ", inBackup.Select(b => b.Name)) +
                "\n\nCác file đã lưu sẽ được chép đè về trình duyệt. Trình duyệt sẽ được đóng trong lúc khôi phục.\n" +
                "Lưu ý: nếu đây là máy / Windows khác lúc sao lưu, bookmark về được nhưng mật khẩu và cookie có thể không mở được.",
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
            $"Đã khôi phục {ok}/{inBackup.Count} trình duyệt. Mở trình duyệt để kiểm tra.", error: fail > 0);
        await RefreshAsync();
    }
}
