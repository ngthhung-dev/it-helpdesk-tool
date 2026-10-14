using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool.ViewModels;

public sealed record QuickTool(string Glyph, string Title, string Target);

public sealed partial class DashboardViewModel : PageViewModel
{
    private const int HistoryLength = 60;

    private readonly SystemInfoService _sys = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<double> _cpuHist = new(), _ramHist = new();
    private int _tick;

    public override string Title => "Tổng quan";
    public override string Subtitle => "Tình trạng máy theo thời gian thực";

    // ---- Số liệu realtime ----
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "–";
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private double[] _ramHistory = [];
    [ObservableProperty] private string _uptimeText = "–";

    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private double _batteryPercent;
    [ObservableProperty] private string _batteryText = "–";
    [ObservableProperty] private string _batteryGlyph = "\uE83F";

    [ObservableProperty] private double _systemDrivePercent;
    [ObservableProperty] private string _systemDriveText = "–";

    // ---- Thông tin tĩnh ----
    [ObservableProperty] private HardwareInfo? _hardware;
    [ObservableProperty] private bool _isLoadingHardware;
    [ObservableProperty] private string _greeting = "";

    public ObservableCollection<DriveState> Drives { get; } = [];

    public IReadOnlyList<QuickTool> QuickTools { get; } =
    [
        new("\uE9D9", "Task Manager", "taskmgr"),
        new("\uE772", "Device Manager", "devmgmt.msc"),
        new("\uEDA2", "Disk Management", "diskmgmt.msc"),
        new("\uE912", "Services", "services.msc"),
        new("\uE80A", "Control Panel", "control"),
        new("\uE946", "System Information", "msinfo32"),
        new("\uE81C", "Event Viewer", "eventvwr.msc"),
        new("\uE839", "Kết nối mạng", "ncpa.cpl"),
    ];

    public DashboardViewModel()
    {
        _timer.Tick += (_, _) => Tick();
        Greeting = DateTime.Now.Hour switch
        {
            < 11 => "Chào buổi sáng",
            < 14 => "Chào buổi trưa",
            < 18 => "Chào buổi chiều",
            _ => "Chào buổi tối",
        } + $", {Environment.UserName}";
    }

    public override void OnNavigatedTo()
    {
        _sys.Sample(); // lấy mốc CPU
        Tick();
        _timer.Start();
        if (Hardware is null) _ = LoadHardwareAsync();
    }

    public override void OnNavigatedFrom() => _timer.Stop();

    private void Tick()
    {
        var s = _sys.Sample();

        CpuPercent = s.CpuPercent;
        RamPercent = s.RamPercent;
        RamText = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB";
        CpuHistory = Push(_cpuHist, s.CpuPercent);
        RamHistory = Push(_ramHist, s.RamPercent);
        UptimeText = FormatUptime(s.Uptime);

        var b = s.Battery;
        HasBattery = b.HasBattery;
        BatteryPercent = b.Percent;
        BatteryText = !b.HasBattery ? "Máy bàn · dùng điện lưới"
            : b.Charging ? "Đang sạc"
            : b.Remaining is { } r ? $"Còn khoảng {(int)r.TotalHours}h {r.Minutes:00}m"
            : "Đang dùng pin";
        BatteryGlyph = !b.HasBattery ? "\uE945" : b.Charging ? "\uEA93" : "\uE83F";

        // Ổ đĩa đổi chậm, 10 giây đọc lại một lần
        if (_tick++ % 10 == 0) RefreshDrives();
    }

    private void RefreshDrives()
    {
        var drives = SystemInfoService.GetDrives();
        Drives.Clear();
        foreach (var d in drives) Drives.Add(d);

        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var sys = drives.FirstOrDefault(d => d.Name.Equals(sysRoot, StringComparison.OrdinalIgnoreCase));
        if (sys is not null)
        {
            SystemDrivePercent = sys.UsedPercent;
            SystemDriveText = $"Trống {sys.FreeGb:0} / {sys.TotalGb:0} GB";
        }
    }

    private async Task LoadHardwareAsync()
    {
        IsLoadingHardware = true;
        try { Hardware = await Task.Run(SystemInfoService.GetHardware); }
        finally { IsLoadingHardware = false; }
    }

    // ---- Lệnh ----
    [RelayCommand]
    private async Task RefreshAsync()
    {
        Hardware = null;
        RefreshDrives();
        await LoadHardwareAsync();
    }

    [RelayCommand]
    private void OpenTool(string target) => ShellLauncher.Open(target);

    [RelayCommand]
    private void CopyInfo()
    {
        if (Hardware is not { } h) return;
        var sb = new StringBuilder()
            .AppendLine($"Máy:        {h.ComputerName} ({h.UserName})")
            .AppendLine($"Hãng/Model: {h.Manufacturer} {h.Model}")
            .AppendLine($"Serial:     {h.SerialNumber}")
            .AppendLine($"Hệ điều hành: {h.OsName} {h.OsVersion} (build {h.OsBuild}, {h.Architecture})")
            .AppendLine($"CPU:        {h.CpuName} · {h.Cores} nhân / {h.Threads} luồng")
            .AppendLine($"RAM:        {h.RamTotalGb:0} GB {h.RamDetail}")
            .AppendLine($"GPU:        {h.Gpu}")
            .AppendLine($"Mainboard:  {h.Mainboard} · BIOS {h.Bios}");
        foreach (var d in Drives)
            sb.AppendLine($"Ổ {d.Name}       {d.Label} · {d.Format} · trống {d.FreeGb:0}/{d.TotalGb:0} GB");

        try
        {
            Clipboard.SetText(sb.ToString());
            CopyStatus = "Đã chép!";
        }
        catch
        {
            CopyStatus = "Clipboard đang bận, thử lại";
        }
        _ = ResetCopyStatusAsync();
    }

    [ObservableProperty] private string _copyStatus = "Chép thông tin máy";

    private async Task ResetCopyStatusAsync()
    {
        await Task.Delay(2000);
        CopyStatus = "Chép thông tin máy";
    }

    // ---- Tiện ích ----
    private static double[] Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
        return q.ToArray();
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} ngày {t.Hours} giờ" : $"{t.Hours} giờ {t.Minutes} phút";
}
