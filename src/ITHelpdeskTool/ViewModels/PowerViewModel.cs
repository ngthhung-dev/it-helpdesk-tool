using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Power;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

/// <summary>Một dòng thiết lập nguồn với 2 ô chọn: cắm điện (AC) và dùng pin (DC).</summary>
public sealed partial class PowerSettingRow(PowerSettingDef def) : ObservableObject
{
    public PowerSettingDef Def { get; } = def;
    public string Title => Def.Title;
    public string Hint => Def.Hint;
    public bool HasHint => Def.Hint.Length > 0;
    public ObservableCollection<KeyValuePair<int, string>> Options { get; } = new(PowerService.Options(def.Kind));

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged))] private int? _ac;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged))] private int? _dc;
    [ObservableProperty] private bool _isAvailable;
    public int? OrigAc { get; private set; }
    public int? OrigDc { get; private set; }
    public bool IsChanged => Ac != OrigAc || Dc != OrigDc;

    private void Ensure(int v)
    {
        if (Options.All(o => o.Key != v)) Options.Add(new(v, PowerService.Describe(Def.Kind, v)));
    }

    public void Load(PowerValue? v)
    {
        IsAvailable = v is not null;
        if (v is null) { OrigAc = OrigDc = Ac = Dc = null; return; }
        Ensure(v.Ac);
        Ensure(v.Dc);
        OrigAc = v.Ac;
        OrigDc = v.Dc;
        Ac = v.Ac;
        Dc = v.Dc;
        OnPropertyChanged(nameof(IsChanged));
    }

    public void Set(int ac, int dc)
    {
        if (!IsAvailable) return;
        Ensure(ac);
        Ensure(dc);
        Ac = ac;
        Dc = dc;
    }
}

public sealed partial class PowerViewModel : PageViewModel
{
    public override string Title => "Power Plan";
    private bool _loaded;

    /// <summary>Máy có pin → hiện cột "Dùng pin" và thiết lập gập máy.</summary>
    public bool HasBattery { get; } =
        NativeMethods.GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;

    public ObservableCollection<PowerPlan> Plans { get; } = [];
    public ObservableCollection<PowerSettingRow> SettingRows { get; }
    public ObservableCollection<ToggleRow<WakeDevice>> WakeDevices { get; } = [];
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _activeName = "";
    [ObservableProperty] private PowerPlan? _editPlan;
    [ObservableProperty] private string _wakeSummary = "";
    [ObservableProperty] private string _diagTitle = "";
    [ObservableProperty] private string _diagText = "";

    public PowerViewModel()
    {
        SettingRows = new(PowerService.Settings
            .Where(d => HasBattery || !d.LaptopOnly)
            .Select(d => new PowerSettingRow(d)));
        foreach (var r in SettingRows)
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PowerSettingRow.IsChanged)) OnPropertyChanged(nameof(HasChanges)); };
    }

    public bool HasChanges => SettingRows.Any(r => r.IsChanged);

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAllAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Power: {text}"); else Log.Warn($"Power: {text}");
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

    [RelayCommand]
    private async Task LoadAllAsync()
    {
        await LoadPlansAsync();
        await LoadWakeAsync();
    }

    // ================= Gói nguồn =================
    private async Task LoadPlansAsync(string? selectGuid = null)
    {
        try
        {
            var keep = selectGuid ?? EditPlan?.Guid;
            var list = await PowerService.ListAsync();
            Plans.Clear();
            foreach (var p in list) Plans.Add(p);
            ActiveName = list.FirstOrDefault(p => p.IsActive)?.Name ?? "—";
            _suppressPlanLoad = true;
            EditPlan = Plans.FirstOrDefault(p => p.Guid == keep) ?? Plans.FirstOrDefault(p => p.IsActive) ?? Plans.FirstOrDefault();
            _suppressPlanLoad = false;
            await LoadSettingsAsync();
        }
        catch (Exception ex) { AddLog($"Không đọc được gói nguồn: {ex.Message}", false); }
    }

    private bool _suppressPlanLoad;
    partial void OnEditPlanChanged(PowerPlan? value)
    {
        if (!_suppressPlanLoad && value is not null) _ = LoadSettingsAsync();
    }

    [RelayCommand]
    private async Task ActivateAsync(PowerPlan p)
    {
        await Run($"Đang chuyển sang {p.Name}", () => PowerService.SetActiveAsync(p));
        await LoadPlansAsync(p.Guid);
    }

    [RelayCommand]
    private async Task AddPlanAsync(string which)
    {
        var template = which == "ultimate" ? PowerService.Ultimate : PowerService.HighPerf;
        string? guid = null;
        await Run("Đang thêm gói nguồn", async () =>
        {
            var (r, g) = await PowerService.AddBuiltInAsync(template);
            guid = g;
            return r;
        });
        await LoadPlansAsync(guid);
    }

    [RelayCommand]
    private async Task RenamePlanAsync(PowerPlan p)
    {
        var name = DialogWindow.AskText("Đổi tên gói nguồn", "Tên mới cho gói:", p.Name);
        if (name is null) return;
        await Run("Đang đổi tên", () => PowerService.RenameAsync(p, name));
        await LoadPlansAsync(p.Guid);
    }

    [RelayCommand]
    private async Task DeletePlanAsync(PowerPlan p)
    {
        if (!DialogWindow.Confirm($"Xoá gói {p.Name}?",
                p.IsBuiltIn ? "Đây là gói có sẵn của Windows; muốn lấy lại thì bấm “Khôi phục mặc định”." : "Gói tự tạo sẽ bị xoá vĩnh viễn (nên Xuất ra file trước).",
                "Xoá", danger: true))
            return;
        await Run($"Đang xoá {p.Name}", () => PowerService.DeleteAsync(p));
        await LoadPlansAsync();
    }

    [RelayCommand]
    private async Task ExportPlanAsync(PowerPlan p)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Xuất gói nguồn",
            Filter = "Gói nguồn Windows (*.pow)|*.pow",
            FileName = string.Concat(p.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + ".pow",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog() != true) return;
        if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);   // powercfg không ghi đè file có sẵn
        await Run("Đang xuất", () => PowerService.ExportAsync(p, dlg.FileName));
    }

    [RelayCommand]
    private async Task ImportPlanAsync()
    {
        var dlg = new OpenFileDialog { Title = "Nhập gói nguồn", Filter = "Gói nguồn Windows (*.pow)|*.pow|Tất cả file|*.*" };
        if (dlg.ShowDialog() != true) return;
        string? guid = null;
        await Run("Đang nhập", async () =>
        {
            var (r, g) = await PowerService.ImportAsync(dlg.FileName);
            guid = g;
            return r;
        });
        await LoadPlansAsync(guid);
    }

    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (!DialogWindow.Confirm("Khôi phục gói nguồn mặc định?",
                "Mọi gói tự tạo (kể cả Ultimate Performance đã thêm) sẽ bị xoá, các gói gốc trở về thiết lập ban đầu của Windows.",
                "Khôi phục", danger: true))
            return;
        await Run("Đang khôi phục", PowerService.RestoreDefaultsAsync);
        await LoadPlansAsync();
    }

    // ================= Thiết lập =================
    private async Task LoadSettingsAsync()
    {
        if (EditPlan is not { } p) return;
        try
        {
            var values = await PowerService.ReadSettingsAsync(p.Guid);
            foreach (var r in SettingRows) r.Load(values.GetValueOrDefault(r.Def.Key));
            OnPropertyChanged(nameof(HasChanges));
        }
        catch (Exception ex) { AddLog($"Không đọc được thiết lập của {p.Name}: {ex.Message}", false); }
    }

    [RelayCommand]
    private void ApplyPreset(string name)
    {
        foreach (var (key, (ac, dc)) in PowerService.Preset(name))
            SettingRows.FirstOrDefault(r => r.Def.Key == key)?.Set(ac, dc);
    }

    [RelayCommand]
    private Task UndoSettingsAsync() => LoadSettingsAsync();

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (EditPlan is not { } p) return;
        var changes = SettingRows.Where(r => r.IsChanged)
            .Select(r => (r.Def, r.Ac != r.OrigAc ? r.Ac : null, HasBattery && r.Dc != r.OrigDc ? r.Dc : null))
            .ToList();
        await Run($"Đang lưu thiết lập cho {p.Name}", () => PowerService.ApplySettingsAsync(p, changes));
        await LoadSettingsAsync();
    }

    // ================= Thiết bị đánh thức =================
    private async Task LoadWakeAsync()
    {
        try
        {
            var list = await PowerService.ReadWakeDevicesAsync();
            WakeDevices.Clear();
            foreach (var d in list) WakeDevices.Add(new ToggleRow<WakeDevice>(d, d.Armed));
            WakeSummary = list.Count == 0
                ? "Không có thiết bị nào có thể đánh thức máy."
                : $"{list.Count(d => d.Armed)}/{list.Count} thiết bị đang được phép đánh thức máy khỏi chế độ ngủ.";
        }
        catch (Exception ex) { WakeSummary = $"Không đọc được: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ToggleWakeAsync(ToggleRow<WakeDevice> row)
    {
        var on = row.IsOn;
        var r = await Run(on ? "Đang cho phép đánh thức" : "Đang chặn đánh thức", () => PowerService.SetWakeAsync(row.Item, on));
        if (!r.Ok) row.SetState(!on);
        await LoadWakeAsync();
    }

    // ================= Chẩn đoán =================
    [RelayCommand]
    private async Task DiagnoseAsync(string what)
    {
        switch (what)
        {
            case "battery":
            {
                string? file = null;
                var r = await Run("Đang tạo báo cáo pin", async () => { var (res, f) = await PowerService.BatteryReportAsync(); file = f; return res; });
                if (r.Ok && file is not null) ShellLauncher.Open(file);
                break;
            }
            case "energy":
            {
                string? file = null;
                var r = await Run("Đang theo dõi năng lượng 15 giây — đừng dùng máy trong lúc này", async () =>
                {
                    var (res, f) = await PowerService.EnergyReportAsync(15);
                    file = f;
                    return res;
                });
                if (r.Ok && file is not null) ShellLauncher.Open(file);
                break;
            }
            default:
            {
                IsBusy = true;
                BusyText = "Đang kiểm tra…";
                try
                {
                    (DiagTitle, DiagText) = what switch
                    {
                        "requests" => ("Những gì đang chặn máy ngủ / tắt màn hình", await PowerService.RequestsAsync()),
                        "lastwake" => ("Lần đánh thức máy gần nhất", await PowerService.LastWakeAsync()),
                        _ => ("Các chế độ ngủ máy hỗ trợ", await PowerService.SleepStatesAsync()),
                    };
                }
                finally { IsBusy = false; }
                break;
            }
        }
    }

    [RelayCommand]
    private void OpenReports()
    {
        Directory.CreateDirectory(PowerService.ReportFolder);
        ShellLauncher.Open(PowerService.ReportFolder);
    }

    [RelayCommand]
    private void OpenPowerOptions() => ShellLauncher.Open("powercfg.cpl");
}
