using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Startup;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

/// <summary>Một dòng có công tắc bật/tắt + lọc tìm kiếm.</summary>
public sealed partial class ToggleRow<T>(T item, bool on) : ObservableObject
{
    public T Item { get; } = item;
    [ObservableProperty] private bool _isOn = on;
    [ObservableProperty] private bool _isVisible = true;

    public void SetState(bool on)
    {
        if (IsOn == on) OnPropertyChanged(nameof(IsOn)); else IsOn = on;
    }
}

public sealed record BootTimeRow(string When, string Seconds, double BarWidth, Tone Tone);

public sealed partial class StartupViewModel : PageViewModel
{
    public override string Title => "Startup & Boot";
    private bool _loaded;

    // ================= Tab =================
    [ObservableProperty] private bool _tabApps = true;
    [ObservableProperty] private bool _tabTasks;
    [ObservableProperty] private bool _tabBoot;

    partial void OnTabTasksChanged(bool value) { if (value && !_tasksLoaded) _ = LoadTasksAsync(); }
    partial void OnTabBootChanged(bool value) { if (value && !_bootLoaded) _ = LoadBootAsync(); }

    // ================= Chung =================
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _filter = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAppsAsync();
        // Mở thẳng tab chỉ định: --page startup/tasks hoặc startup/boot
        switch (ShellViewModel.StartSubPage)
        {
            case "tasks": TabTasks = true; TabApps = false; break;
            case "boot": TabBoot = true; TabApps = false; break;
        }
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Startup: {text}"); else Log.Warn($"Startup: {text}");
    }

    private async Task<OpResult> Run(string busy, Func<Task<OpResult>> op)
    {
        if (IsBusy) return OpResult.Fail("Đang bận");
        IsBusy = true;
        BusyText = busy + "…";
        OpResult r;
        try { r = await op(); }
        catch (Exception ex) { r = OpResult.Fail(ex.Message); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        return r;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        bool M(params string[] fields) => f.Length == 0 || fields.Any(x => x.Contains(f, StringComparison.CurrentCultureIgnoreCase));
        foreach (var a in Apps) a.IsVisible = M(a.Item.Name, a.Item.Command, a.Item.Publisher);
        foreach (var t in Tasks) t.IsVisible = M(t.Item.Name, t.Item.Action, t.Item.Author);
        foreach (var s in Services) s.IsVisible = M(s.Item.DisplayName, s.Item.Name, s.Item.Publisher);
    }

    // ================= Ứng dụng khởi động =================
    public ObservableCollection<ToggleRow<StartupEntry>> Apps { get; } = [];
    [ObservableProperty] private string _appsSummary = "";

    [RelayCommand]
    private async Task LoadAppsAsync()
    {
        var list = await Task.Run(StartupService.ReadAll);
        Apps.Clear();
        foreach (var e in list) Apps.Add(new ToggleRow<StartupEntry>(e, e.Enabled));
        var on = list.Count(e => e.Enabled);
        var broken = list.Count(e => !e.FileExists);
        AppsSummary = $"{list.Count} ứng dụng · {on} đang tự chạy" + (broken > 0 ? $" · {broken} mục hỏng (file không còn)" : "");
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ToggleAppAsync(ToggleRow<StartupEntry> row)
    {
        var on = row.IsOn;
        var r = await Run(on ? $"Đang bật {row.Item.Name}" : $"Đang tắt {row.Item.Name}",
            () => Task.Run(() => StartupService.SetEnabled(row.Item, on)));
        if (!r.Ok) row.SetState(!on);
        await LoadAppsAsync();
    }

    [RelayCommand]
    private async Task DeleteAppAsync(ToggleRow<StartupEntry> row)
    {
        var e = row.Item;
        if (!DialogWindow.Confirm($"Xoá {e.Name} khỏi khởi động?",
                (e.FileExists ? "Thường chỉ cần TẮT là đủ (bật lại được). " : "File của mục này không còn trên máy nên xoá là an toàn. ") +
                $"Xoá sẽ bỏ hẳn dòng lệnh:\n{e.Command}\n\nApp có lưu bản sao lưu để khôi phục khi cần.", "Xoá", danger: e.FileExists))
            return;
        await Run($"Đang xoá {e.Name}", () => Task.Run(() => StartupService.Delete(e)));
        await LoadAppsAsync();
    }

    [RelayCommand]
    private void OpenAppLocation(ToggleRow<StartupEntry> row)
    {
        try { StartupService.OpenLocation(row.Item); }
        catch (Exception ex) { AddLog($"Không mở được vị trí: {ex.Message}", false); }
    }

    // Thêm ứng dụng
    [ObservableProperty] private bool _showAddForm;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string _newArgs = "";
    [ObservableProperty] private bool _newAllUsers;

    [RelayCommand] private void ToggleAddForm() => ShowAddForm = !ShowAddForm;

    [RelayCommand]
    private void BrowseExe()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn chương trình chạy khi khởi động",
            Filter = "Chương trình (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Tất cả file|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog() != true) return;
        NewPath = dlg.FileName;
        if (NewName.Trim().Length == 0) NewName = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    [RelayCommand]
    private async Task AddAppAsync()
    {
        var r = await Run($"Đang thêm {NewName}", () => Task.Run(() => StartupService.Add(NewName, NewPath, NewArgs, NewAllUsers)));
        if (r.Ok) { NewName = NewPath = NewArgs = ""; ShowAddForm = false; }
        await LoadAppsAsync();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(StartupService.BackupFolder);
        ShellLauncher.Open(StartupService.BackupFolder);
    }

    [RelayCommand]
    private void OpenStartupFolder(string which) =>
        ShellLauncher.Open(which == "common" ? StartupService.CommonFolder : StartupService.UserFolder);

    // ================= Tác vụ & dịch vụ =================
    private bool _tasksLoaded;
    public ObservableCollection<ToggleRow<TaskEntry>> Tasks { get; } = [];
    public ObservableCollection<ToggleRow<ServiceEntry>> Services { get; } = [];
    [ObservableProperty] private string _tasksSummary = "Đang đọc…";
    [ObservableProperty] private string _servicesSummary = "Đang đọc…";

    [RelayCommand]
    private async Task LoadTasksAsync()
    {
        _tasksLoaded = true;
        var tasksJob = AutoRunService.ReadTasksAsync();
        var svcJob = Task.Run(AutoRunService.ReadServices);
        try
        {
            var tasks = await tasksJob;
            Tasks.Clear();
            foreach (var t in tasks) Tasks.Add(new ToggleRow<TaskEntry>(t, t.Enabled));
            TasksSummary = tasks.Count == 0 ? "Không có tác vụ nào ngoài của Windows chạy lúc khởi động/đăng nhập."
                : $"{tasks.Count} tác vụ · {tasks.Count(t => t.Enabled)} đang bật";
        }
        catch (Exception ex) { TasksSummary = $"Không đọc được tác vụ: {ex.Message}"; }

        var svcs = await svcJob;
        Services.Clear();
        foreach (var s in svcs) Services.Add(new ToggleRow<ServiceEntry>(s, s.Auto));
        ServicesSummary = svcs.Count == 0 ? "Không có dịch vụ của hãng thứ ba."
            : $"{svcs.Count} dịch vụ hãng thứ ba · {svcs.Count(s => s.Auto)} tự chạy cùng Windows";
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(ToggleRow<TaskEntry> row)
    {
        var on = row.IsOn;
        var r = await Run(on ? $"Đang bật {row.Item.Name}" : $"Đang tắt {row.Item.Name}", () => AutoRunService.SetTaskEnabledAsync(row.Item, on));
        if (!r.Ok) row.SetState(!on);
        await LoadTasksAsync();
    }

    [RelayCommand]
    private async Task ToggleServiceAsync(ToggleRow<ServiceEntry> row)
    {
        var on = row.IsOn;
        if (!on && !DialogWindow.Confirm($"Không tự chạy {row.Item.DisplayName}?",
                "Dịch vụ chuyển sang chạy khi cần (Manual). Phần mềm của hãng vẫn tự bật dịch vụ khi dùng tới, " +
                "nhưng một số tính năng chạy nền (đồng bộ, cập nhật, driver tiện ích) có thể ngừng.", "Đồng ý"))
        {
            row.SetState(true);
            return;
        }
        var r = await Run($"Đang đổi {row.Item.DisplayName}", () => AutoRunService.SetServiceAutoAsync(row.Item, on));
        if (!r.Ok) row.SetState(!on);
        await LoadTasksAsync();
    }

    // ================= Khởi động máy =================
    private bool _bootLoaded;
    public ObservableCollection<BootEntry> BootEntries { get; } = [];
    public ObservableCollection<BootTimeRow> BootTimes { get; } = [];
    [ObservableProperty] private string _timeoutText = "";
    [ObservableProperty] private string _safeBoot = "";
    [ObservableProperty] private bool _isUefi;
    [ObservableProperty] private string _bootSummary = "";
    [ObservableProperty] private string _bootTimesNote = "";
    [ObservableProperty] private string _scheduleMinutes = "60";
    public bool SafeBootOn => SafeBoot.Length > 0;
    partial void OnSafeBootChanged(string value) => OnPropertyChanged(nameof(SafeBootOn));

    [RelayCommand]
    private async Task LoadBootAsync()
    {
        _bootLoaded = true;
        try
        {
            var cfg = await Task.Run(BootService.ReadConfig);
            BootEntries.Clear();
            foreach (var e in cfg.Entries) BootEntries.Add(e);
            TimeoutText = cfg.TimeoutSeconds.ToString();
            SafeBoot = cfg.SafeBoot;
            IsUefi = cfg.IsUefi;
        }
        catch (Exception ex) { AddLog($"Không đọc được menu khởi động: {ex.Message}", false); }

        var up = DateTime.Now - BootService.LastBoot;
        BootSummary = $"Khởi động lần cuối {BootService.LastBoot:HH:mm dd/MM/yyyy} · đã chạy {Fmt.Duration(up)}";

        var times = await BootService.ReadBootTimesAsync();
        BootTimes.Clear();
        var max = times.Count == 0 ? 1 : times.Max(t => t.Seconds);
        foreach (var t in times)
            BootTimes.Add(new BootTimeRow(t.When.ToString("dd/MM HH:mm"), $"{t.Seconds:0.0} giây",
                Math.Max(4, t.Seconds / max * 320), t.Seconds > 90 ? Tone.Bad : t.Seconds > 45 ? Tone.Warn : Tone.Good));
        BootTimesNote = times.Count == 0
            ? "Chưa có số liệu (Windows chỉ ghi khi khởi động đầy đủ; bản Server không ghi)."
            : $"Trung bình {times.Average(t => t.Seconds):0.0} giây · dưới 45 giây là tốt. Fast Startup bật thì Windows ít ghi số liệu này.";
    }

    [RelayCommand]
    private async Task SetDefaultBootAsync(BootEntry e)
    {
        if (e.IsDefault) return;
        await Run("Đang đổi mục mặc định", () => BootService.SetDefaultAsync(e));
        await LoadBootAsync();
    }

    [RelayCommand]
    private async Task ApplyTimeoutAsync()
    {
        if (!int.TryParse(TimeoutText, out var s)) { DialogWindow.Info("Số không hợp lệ", "Nhập số giây (0–999).", error: true); return; }
        await Run("Đang đặt thời gian chờ", () => BootService.SetTimeoutAsync(s));
        await LoadBootAsync();
    }

    [RelayCommand]
    private async Task SetSafeBootAsync(string mode)
    {
        if (mode.Length > 0 && !DialogWindow.Confirm("Vào Safe Mode lần sau?",
                "Mỗi lần khởi động sẽ vào Safe Mode cho tới khi bạn tắt lại (mở app trong Safe Mode → bấm “Khởi động bình thường”). " +
                "Trong Safe Mode nhiều driver và phần mềm không chạy.", "Đồng ý"))
            return;
        await Run("Đang đổi chế độ khởi động", () => BootService.SetSafeBootAsync(mode));
        await LoadBootAsync();
    }

    [RelayCommand]
    private async Task RestartSpecialAsync(string target)
    {
        var (title, msg) = target == "fw"
            ? ("Khởi động lại vào BIOS/UEFI?", "Máy khởi động lại sau 5 giây và vào thẳng màn hình cài đặt BIOS/UEFI.")
            : ("Khởi động lại vào Advanced Startup?", "Máy khởi động lại vào Windows RE: Safe Mode, System Restore, Startup Repair, Command Prompt...");
        if (!DialogWindow.Confirm(title, msg + " Lưu công việc đang mở trước.", "Khởi động lại", danger: true)) return;
        await Run("Đang khởi động lại", () => Task.Run(() => target == "fw" ? BootService.RestartToFirmware() : BootService.RestartToRecovery()));
    }

    [RelayCommand]
    private async Task ScheduleAsync(string kind)
    {
        if (!int.TryParse(ScheduleMinutes, out var m)) { DialogWindow.Info("Số không hợp lệ", "Nhập số phút.", error: true); return; }
        await Run("Đang hẹn giờ", () => Task.Run(() => BootService.Schedule(kind == "restart", m)));
    }

    [RelayCommand]
    private Task CancelScheduleAsync() => Run("Đang huỷ hẹn giờ", () => Task.Run(BootService.CancelSchedule));
}
