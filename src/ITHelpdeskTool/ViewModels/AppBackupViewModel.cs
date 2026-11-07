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

public sealed partial class BackupRow(BackupItem item) : ObservableObject
{
    public BackupItem I { get; } = item;
    [ObservableProperty] private bool _isChecked = item.CanBackup;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusOk = true;
    public string SizeText => I.Size > 0 ? Fmt.Size(I.Size) : "";
    public bool NeedsLogin => I.Kind == BackupKind.NeedLogin;
    public bool Stopped => I.Kind == BackupKind.Stopped;
    public bool IsWarn => I.Kind is BackupKind.NeedLogin or BackupKind.Stopped or BackupKind.Error;
}

public sealed partial class AppBackupViewModel : PageViewModel
{
    public override string Title => "Sao lưu phần mềm";
    private bool _loaded;
    // Tài khoản SQL nhập trong phiên làm việc — chỉ giữ trong bộ nhớ, không ghi ra đĩa / log
    private readonly Dictionary<string, (string User, string Password)> _logins = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<BackupRow> Items { get; } = [];
    [ObservableProperty] private string _summary = "";

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
        if (ok) Log.Info($"Sao lưu phần mềm: {text}"); else Log.Warn($"Sao lưu phần mềm: {text}");
    }

    private (string, string)? LoginOf(SqlInstance? i) => i is not null && _logins.TryGetValue(i.Name, out var l) ? l : null;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = "Đang tìm SQL Server và phần mềm…";
        try
        {
            var found = new List<BackupItem>();
            foreach (var i in await Task.Run(AppBackupService.Instances))
                found.AddRange(await AppBackupService.DatabasesAsync(i, LoginOf(i)));
            found.AddRange(await Task.Run(AppBackupService.Folders));
            Items.Clear();
            foreach (var x in found) Items.Add(new BackupRow(x));
            var ok = found.Where(x => x.CanBackup).ToList();
            Summary = found.Count == 0
                ? "Không tìm thấy SQL Server, HTKK hay phần mềm kê khai BHXH trên máy này."
                : $"{ok.Count} mục sao lưu được · {Fmt.Size(ok.Sum(x => x.Size))}" + (found.Any(x => !x.CanBackup) ? $" · {found.Count(x => !x.CanBackup)} mục cần xử lý" : "");
        }
        catch (Exception ex) { AddLog($"Không đọc được danh sách: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoginAsync(BackupRow r)
    {
        var i = r.I.Instance;
        if (i is null) return;
        var user = DialogWindow.AskText($"Đăng nhập SQL Server {i.Display}", "Tài khoản SQL của phần mềm (thường là sa):", "sa");
        if (string.IsNullOrWhiteSpace(user)) return;
        var pw = DialogWindow.AskPassword($"Đăng nhập SQL Server {i.Display}", $"Mật khẩu của {user.Trim()} (chỉ dùng trong lần mở app này, không lưu lại):", allowEmpty: true);
        if (pw is null) return;
        _logins[i.Name] = (user.Trim(), pw);
        await RefreshAsync();
        if (Items.Any(x => x.I.Instance?.Name == i.Name && x.NeedsLogin))
        {
            _logins.Remove(i.Name);
            AddLog($"SQL Server {i.Display}: đăng nhập không được (sai tài khoản / mật khẩu?)", false);
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync(BackupRow r)
    {
        if (r.I.Instance is null) return;
        IsBusy = true;
        BusyText = $"Đang bật {r.I.Instance.Service}…";
        try { var res = await AppBackupService.StartServiceAsync(r.I.Instance); AddLog(res.Message, res.Ok); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenFolder(BackupRow r)
    {
        var p = r.I.Kind == BackupKind.Folder ? r.I.Path : r.I.Instance?.DataDir ?? "";
        if (Directory.Exists(p)) ShellLauncher.Open(p);
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var sel = Items.Where(x => x.IsChecked && x.I.CanBackup).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn mục nào", "Tích các cơ sở dữ liệu / thư mục muốn sao lưu."); return; }
        var total = sel.Sum(x => x.I.Size);
        var dlg = new OpenFolderDialog { Title = $"Nơi lưu bản sao (khoảng {Fmt.Size(total)}) — chọn USB / ổ cứng ngoài / ổ khác ổ C:" };
        if (dlg.ShowDialog() != true) return;
        var root = dlg.FolderName;

        var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        if (string.Equals(Path.GetPathRoot(root), sys, StringComparison.OrdinalIgnoreCase) &&
            !DialogWindow.Confirm("Lưu vào ổ hệ thống?", $"Bạn đang chọn ổ {sys.TrimEnd('\\')} — ổ này bị xoá khi cài lại Windows. Nên chọn USB / ổ khác.\n\nVẫn lưu vào đây?", "Vẫn lưu", danger: true))
            return;
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            // Kích thước cơ sở dữ liệu là kích thước file .mdf/.ldf — file .bak thường nhỏ hơn
            if (free < total && !DialogWindow.Confirm("Có thể không đủ chỗ", $"Còn trống {Fmt.Size(free)}, dữ liệu chọn khoảng {Fmt.Size(total)} (file sao lưu thường nhỏ hơn). Vẫn sao lưu?", "Vẫn sao lưu"))
                return;
        }
        catch { }

        IsBusy = true;
        var set = AppBackupService.NewSetDir(root);
        var lines = new List<string>();
        int ok = 0, fail = 0, n = 0;
        try
        {
            Directory.CreateDirectory(set);
            foreach (var row in sel)
            {
                n++;
                Progress = (n - 1) * 100 / sel.Count;
                BusyText = $"Đang sao lưu {n}/{sel.Count}: {row.I.Name}…";
                row.Status = "Đang sao lưu…";
                row.StatusOk = true;
                OpResult res;
                if (row.I.Kind == BackupKind.Database)
                {
                    var (r, file, hash) = await AppBackupService.BackupDatabaseAsync(row.I, set, LoginOf(row.I.Instance));
                    res = r;
                    lines.Add($"[{row.I.App}] {row.I.Name} — {row.I.Detail}");
                    lines.Add(r.Ok ? $"    File: {Path.GetRelativePath(set, file)} · {Fmt.Size(new FileInfo(file).Length)} · SHA256 {hash}" : $"    LỖI: {r.Message}");
                }
                else
                {
                    res = await AppBackupService.BackupFolderAsync(row.I, set);
                    lines.Add($"[{row.I.App}] {row.I.Path}");
                    lines.Add($"    {(res.Ok ? "Thư mục: " + Path.Combine(row.I.App, row.I.Name) : "LỖI: " + res.Message)}");
                }
                row.Status = res.Ok ? "Đã sao lưu" : $"Lỗi: {res.Message}";
                row.StatusOk = res.Ok;
                AddLog(res.Ok ? $"Đã sao lưu {res.Message}" : $"Không sao lưu được {row.I.Name}: {res.Message}", res.Ok);
                if (res.Ok) ok++; else fail++;
            }
            AppBackupService.WriteManifest(set, lines);
        }
        catch (Exception ex) { AddLog(ex.Message, false); fail++; }
        finally { IsBusy = false; Progress = 0; }

        var msg = $"Đã sao lưu {ok}/{sel.Count} mục vào {set}." + (fail > 0 ? $" {fail} mục lỗi — xem nhật ký bên dưới." : "");
        if (fail > 0) DialogWindow.Info("Sao lưu chưa trọn vẹn", msg, error: true);
        if (ok > 0) ShellLauncher.Open(set);
    }
}
