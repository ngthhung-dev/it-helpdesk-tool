using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Disk;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class BitLockerRow(BitLockerVolume v) : ObservableObject
{
    [ObservableProperty] private BitLockerVolume _v = v;
    [ObservableProperty] private bool _showKeys;
    public IEnumerable<KeyProtector> RecoveryKeys => V.Protectors.Where(p => p.Type == "RecoveryPassword");
    partial void OnVChanged(BitLockerVolume value) => OnPropertyChanged(nameof(RecoveryKeys));
}

public sealed partial class BitLockerViewModel : PageViewModel
{
    public override string Title => "BitLocker";
    private bool _loaded;
    private DispatcherTimer? _timer;

    public ObservableCollection<BitLockerRow> Volumes { get; } = [];
    [ObservableProperty] private bool _available = true;
    [ObservableProperty] private string _unavailableText = "";
    [ObservableProperty] private string _tpmText = "";
    [ObservableProperty] private bool _domainJoined;
    [ObservableProperty] private bool _entraJoined;
    [ObservableProperty] private bool _osProtected;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    public override void OnNavigatedTo()
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
    }

    public override void OnNavigatedFrom() => _timer?.Stop();

    private async void Tick(object? s, EventArgs e)
    {
        if (!IsBusy && Volumes.Any(v => v.V.InProgress)) await RefreshAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 30) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"BitLocker: {text}"); else Log.Warn($"BitLocker: {text}");
    }

    private async Task LoadAsync()
    {
        var (ok, msg) = await BitLockerService.AvailableAsync();
        Available = ok;
        UnavailableText = msg;
        var tpm = await Task.Run(() => Wmi.TryQuery("SELECT SpecVersion, IsEnabled_InitialValue FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm").FirstOrDefault());
        TpmText = tpm is null ? "Không có TPM — ổ hệ điều hành chỉ bật được BitLocker khi có chính sách cho phép dùng mật khẩu / USB."
            : $"TPM {tpm.Str("SpecVersion").Split(',')[0]}" + (tpm.Bool("IsEnabled_InitialValue") == false ? " (đang tắt trong BIOS)" : " sẵn sàng");
        DomainJoined = Wmi.First("SELECT PartOfDomain FROM Win32_ComputerSystem")?.Bool("PartOfDomain") == true;
        var ds = await ProcessRunner.ConsoleAsync("dsregcmd.exe", "/status");
        EntraJoined = System.Text.RegularExpressions.Regex.IsMatch(ds.Output, @"AzureAdJoined\s*:\s*YES", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ok) await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!Available) return;
        try
        {
            var list = await BitLockerService.VolumesAsync();
            // Giữ trạng thái “hiện mã” khi làm mới
            var shown = Volumes.Where(v => v.ShowKeys).Select(v => v.V.MountPoint).ToHashSet();
            Volumes.Clear();
            foreach (var v in list) Volumes.Add(new BitLockerRow(v) { ShowKeys = shown.Contains(v.MountPoint) });
            OsProtected = list.Any(v => v.IsOs && v.On);
        }
        catch (Exception ex) { AddLog($"Không đọc được trạng thái BitLocker: {ex.Message}", false); }
    }

    private async Task Run(string busy, Func<Task<OpResult>> f)
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
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    [RelayCommand] private void ToggleKeys(BitLockerRow r) => r.ShowKeys = !r.ShowKeys;

    [RelayCommand]
    private void SaveKey(BitLockerRow r)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Văn bản (*.txt)|*.txt",
            FileName = $"BitLocker-{Environment.MachineName}-{r.V.MountPoint.TrimEnd(':', '\\')}-{DateTime.Now:yyyyMMdd}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        var res = BitLockerService.SaveRecovery(r.V, dlg.FileName);
        AddLog(res.Message, res.Ok);
        if (!res.Ok) DialogWindow.Info("Chưa lưu", res.Message, error: true);
    }

    [RelayCommand]
    private void CopyKey(KeyProtector k)
    {
        try { System.Windows.Clipboard.SetText(k.RecoveryPassword); AddLog("Đã chép mã khôi phục vào clipboard.", true); } catch { }
    }

    [RelayCommand] private Task AddRecoveryAsync(BitLockerRow r) => Run("Đang thêm mã khôi phục…", () => BitLockerService.AddRecoveryPasswordAsync(r.V));

    [RelayCommand]
    private Task BackupAsync(BitLockerRow r) =>
        Run("Đang sao lưu mã khôi phục…", () => BitLockerService.BackupToDirectoryAsync(r.V, entra: EntraJoined));

    [RelayCommand]
    private async Task EnableAsync(BitLockerRow r)
    {
        var v = r.V;
        string? pw = null;
        if (v.IsOs)
        {
            if (!DialogWindow.Confirm($"Bật BitLocker cho ổ {v.MountPoint}?",
                    "Ổ hệ điều hành được bảo vệ bằng TPM — máy tự mở khoá khi khởi động bình thường.\n\n" +
                    "App sẽ tạo MÃ KHÔI PHỤC trước. BẮT BUỘC lưu mã này (ra USB / in giấy / sao lưu tài khoản): khi thay main, cập nhật BIOS hoặc TPM lỗi, " +
                    "không có mã khôi phục sẽ MẤT TOÀN BỘ DỮ LIỆU.", "Bật BitLocker"))
                return;
        }
        else
        {
            pw = DialogWindow.AskPassword($"Mật khẩu cho ổ {v.MountPoint}", "Mật khẩu mở khoá ổ khi cắm sang máy khác (ít nhất 8 ký tự):", allowEmpty: false);
            if (pw is null) return;
            if (pw.Length < 8) { DialogWindow.Info("Mật khẩu quá ngắn", "Cần ít nhất 8 ký tự."); return; }
            var again = DialogWindow.AskPassword("Nhập lại mật khẩu", "Nhập lại để xác nhận:", allowEmpty: false);
            if (again != pw) { DialogWindow.Info("Không khớp", "Hai lần nhập mật khẩu không giống nhau."); return; }
        }
        await Run($"Đang bật BitLocker cho {v.MountPoint}…", () => BitLockerService.EnableAsync(v, pw, autoUnlock: !v.IsOs && OsProtected, skipHardwareTest: false));
        // Nhắc lưu mã khôi phục ngay
        if (Volumes.FirstOrDefault(x => x.V.MountPoint == v.MountPoint) is { V.HasRecovery: true } row)
        {
            DialogWindow.Info("Lưu mã khôi phục ngay", $"Chọn nơi lưu mã khôi phục của ổ {v.MountPoint} (ổ khác hoặc USB — không lưu trên chính ổ này).");
            SaveKey(row);
        }
    }

    [RelayCommand]
    private Task SuspendAsync(BitLockerRow r)
    {
        if (!DialogWindow.Confirm($"Tạm dừng BitLocker trên {r.V.MountPoint}?",
                "Dùng trước khi cập nhật BIOS / firmware hoặc thay phần cứng để không bị hỏi mã khôi phục. Dữ liệu vẫn được mã hoá; bảo vệ tự bật lại sau 1 lần khởi động.", "Tạm dừng"))
            return Task.CompletedTask;
        return Run("Đang tạm dừng…", () => BitLockerService.SuspendAsync(r.V, r.V.IsOs ? 1 : 0));
    }

    [RelayCommand] private Task ResumeAsync(BitLockerRow r) => Run("Đang bật lại bảo vệ…", () => BitLockerService.ResumeAsync(r.V));

    [RelayCommand]
    private Task DisableAsync(BitLockerRow r)
    {
        if (!DialogWindow.Confirm($"Tắt BitLocker trên {r.V.MountPoint}?",
                "Ổ sẽ được giải mã hoàn toàn (chạy nền, có thể mất vài giờ). Sau đó dữ liệu không còn được bảo vệ nếu mất máy / ổ.", "Tắt BitLocker", danger: true))
            return Task.CompletedTask;
        return Run("Đang tắt BitLocker…", () => BitLockerService.DisableAsync(r.V));
    }

    [RelayCommand]
    private Task UnlockPasswordAsync(BitLockerRow r)
    {
        var pw = DialogWindow.AskPassword($"Mở khoá {r.V.MountPoint}", "Mật khẩu BitLocker của ổ:", allowEmpty: false);
        return pw is null ? Task.CompletedTask : Run("Đang mở khoá…", () => BitLockerService.UnlockWithPasswordAsync(r.V, pw));
    }

    [RelayCommand]
    private Task UnlockRecoveryAsync(BitLockerRow r)
    {
        var id = r.RecoveryKeys.FirstOrDefault()?.Id.Trim('{', '}') ?? "";
        var key = DialogWindow.AskText($"Mở khoá {r.V.MountPoint} bằng mã khôi phục",
            "Nhập mã khôi phục 48 chữ số (8 nhóm, cách nhau dấu -)." + (id.Length > 0 ? $"\nMã định danh của ổ: {id[..8]}… — tìm mã khớp trong file sao lưu hoặc tài khoản Microsoft (aka.ms/myrecoverykey)." : ""));
        if (key is null) return Task.CompletedTask;
        if (!BitLockerService.IsRecoveryFormat(key)) { DialogWindow.Info("Mã chưa đúng định dạng", "Mã khôi phục có dạng 000000-000000-000000-000000-000000-000000-000000-000000."); return Task.CompletedTask; }
        return Run("Đang mở khoá…", () => BitLockerService.UnlockWithRecoveryAsync(r.V, key));
    }

    [RelayCommand] private Task LockAsync(BitLockerRow r) => Run("Đang khoá ổ…", () => BitLockerService.LockAsync(r.V));

    [RelayCommand] private Task AutoUnlockAsync(BitLockerRow r) => Run("Đang đổi tự mở khoá…", () => BitLockerService.SetAutoUnlockAsync(r.V, !r.V.AutoUnlock));

    [RelayCommand] private void OpenMyKeys() => ShellLauncher.Open("https://aka.ms/myrecoverykey");
    /// <summary>
    /// Mở “BitLocker Drive Encryption” trong Control Panel. control.exe gọi từ tiến trình quyền admin thường không hiện gì,
    /// nên mở qua Explorer bằng CLSID; Windows Home không có mục này → mở Device encryption trong Settings.
    /// </summary>
    [RelayCommand]
    private void OpenControlPanel()
    {
        if (!Available) { ShellLauncher.Open("ms-settings:deviceencryption"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:::{D9EF8727-CAC2-4e60-809E-86F80A666C91}") { UseShellExecute = true });
        }
        catch { ShellLauncher.Open("control.exe", "/name Microsoft.BitLockerDriveEncryption"); }
    }

    // ================= Sao lưu tất cả mã khôi phục =================
    public bool CanBackupDirectory => DomainJoined || EntraJoined;
    partial void OnDomainJoinedChanged(bool value) => OnPropertyChanged(nameof(CanBackupDirectory));
    partial void OnEntraJoinedChanged(bool value) => OnPropertyChanged(nameof(CanBackupDirectory));

    private bool CheckAnyKeys()
    {
        if (Volumes.Any(v => v.V.HasRecovery)) return true;
        DialogWindow.Info("Chưa có mã khôi phục",
            Volumes.Any(v => v.V.Encrypted)
                ? "Các ổ đã mã hoá chưa có mã khôi phục — bấm “Thêm mã khôi phục” ở từng ổ trước."
                : "Chưa có ổ nào bật BitLocker nên chưa có mã khôi phục để sao lưu. Mã khôi phục được tạo khi bật BitLocker.");
        return false;
    }

    [RelayCommand]
    private void BackupAllKeys()
    {
        if (!CheckAnyKeys()) return;
        var dlg = new SaveFileDialog
        {
            Title = "Lưu mã khôi phục BitLocker (nên chọn USB / ổ không mã hoá)",
            Filter = "Văn bản (*.txt)|*.txt",
            FileName = $"BitLocker-Keys-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        var r = BitLockerService.SaveAllRecovery(Volumes.Select(v => v.V).ToList(), dlg.FileName);
        AddLog(r.Message, r.Ok);
        if (!r.Ok) { DialogWindow.Info("Chưa lưu", r.Message, error: true); return; }
        ShellLauncher.Open("explorer.exe", $"/select,\"{dlg.FileName}\"");
    }

    [RelayCommand]
    private void PrintKeys()
    {
        if (!CheckAnyKeys()) return;
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BitLocker-Keys-{Environment.MachineName}.txt");
        var r = BitLockerService.SaveAllRecovery(Volumes.Select(v => v.V).ToList(), tmp, csv: false);
        if (!r.Ok) { DialogWindow.Info("Chưa in được", r.Message, error: true); return; }
        ShellLauncher.Open("notepad.exe", $"/p \"{tmp}\"");
        AddLog("Đã gửi mã khôi phục tới máy in mặc định.", true);
        // Xoá bản tạm sau khi Notepad đã đọc xong
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => { try { System.IO.File.Delete(tmp); } catch { } });
    }

    [RelayCommand]
    private Task BackupAllDirectoryAsync()
    {
        if (!CheckAnyKeys()) return Task.CompletedTask;
        return Run("Đang sao lưu mã khôi phục…", () => BitLockerService.BackupAllToDirectoryAsync(Volumes.Select(v => v.V).ToList(), entra: EntraJoined));
    }
}
