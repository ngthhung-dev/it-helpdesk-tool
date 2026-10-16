using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Printing;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class PrinterRow(PrinterInfo p) : ObservableObject
{
    public PrinterInfo P { get; } = p;
    [ObservableProperty] private bool _showJobs;
    public ObservableCollection<PrintJobInfo> Jobs { get; } = [];
}

public sealed partial class FixRow(PrinterFix fix) : ObservableObject
{
    public PrinterFix Fix { get; } = fix;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _ok = true;
}

public sealed class FixGroupRow(PrinterFixGroup g)
{
    public string Name => g.Name;
    public string Note => g.Note;
    public List<FixRow> Items { get; } = g.Items.Select(f => new FixRow(f)).ToList();
}

public sealed partial class PrinterViewModel : PageViewModel
{
    public override string Title => "Máy in";
    private bool _loaded;

    public ObservableCollection<PrinterRow> Printers { get; } = [];
    public ObservableCollection<string> Drivers { get; } = [];
    public List<FixGroupRow> FixGroups { get; } = PrinterFixCatalog.Groups().Select(g => new FixGroupRow(g)).ToList();
    [ObservableProperty] private bool _showVirtual;
    [ObservableProperty] private string _summary = "";

    // Thêm máy in
    [ObservableProperty] private bool _addByIp = true;
    [ObservableProperty] private bool _addShared;
    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _driver;
    [ObservableProperty] private string _probeText = "";
    [ObservableProperty] private string _unc = "";

    [ObservableProperty] private bool _rpcEnforced = true;

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
        if (ok) Log.Info($"Printer: {text}"); else Log.Warn($"Printer: {text}");
    }

    partial void OnShowVirtualChanged(bool value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var list = await PrinterService.ListAsync();
            Printers.Clear();
            foreach (var p in list.Where(p => ShowVirtual || !p.IsVirtual)) Printers.Add(new PrinterRow(p));
            var real = list.Count(p => !p.IsVirtual);
            Summary = $"{real} máy in thật · {list.Count - real} máy in ảo (PDF, XPS, OneNote, Fax)";
            if (Drivers.Count == 0)
            {
                foreach (var d in await PrinterService.DriversAsync()) Drivers.Add(d);
                Driver ??= Drivers.FirstOrDefault(d => d.StartsWith("Microsoft IPP", StringComparison.OrdinalIgnoreCase)) ??
                           Drivers.FirstOrDefault(d => d.Contains("PCL6", StringComparison.OrdinalIgnoreCase)) ?? Drivers.FirstOrDefault();
            }
            RpcEnforced = PrinterService.RpcPrivacyEnforced();
        }
        catch (Exception ex) { Summary = ex.Message; }
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
        finally { IsBusy = false; }
        if (refresh) await RefreshAsync();
    }

    [RelayCommand] private Task SetDefaultAsync(PrinterRow r) => Run("Đang đặt mặc định…", () => PrinterService.SetDefaultAsync(r.P));
    [RelayCommand] private Task TestPageAsync(PrinterRow r) => Run("Đang gửi trang in thử…", () => PrinterService.TestPageAsync(r.P));
    [RelayCommand] private Task ClearJobsAsync(PrinterRow r) => Run("Đang xoá lệnh in…", () => PrinterService.ClearJobsAsync(r.P));
    [RelayCommand] private void Queue(PrinterRow r) => PrinterService.OpenQueue(r.P);
    [RelayCommand] private void Properties(PrinterRow r) => PrinterService.OpenProperties(r.P);
    [RelayCommand] private void Preferences(PrinterRow r) => PrinterService.OpenPreferences(r.P);

    [RelayCommand]
    private async Task ToggleJobsAsync(PrinterRow r)
    {
        r.ShowJobs = !r.ShowJobs;
        if (!r.ShowJobs) return;
        r.Jobs.Clear();
        foreach (var j in await PrinterService.JobsAsync(r.P.Name)) r.Jobs.Add(j);
    }

    [RelayCommand]
    private async Task CancelJobAsync(PrintJobInfo j)
    {
        await Run("Đang huỷ lệnh in…", () => PrinterService.RemoveJobAsync(j), refresh: false);
        if (Printers.FirstOrDefault(p => p.P.Name == j.Printer) is { } row) row.Jobs.Remove(j);
    }

    [RelayCommand]
    private Task RenameAsync(PrinterRow r)
    {
        var name = DialogWindow.AskText("Đổi tên máy in", "Tên mới:", r.P.Name);
        return string.IsNullOrWhiteSpace(name) || name == r.P.Name ? Task.CompletedTask : Run("Đang đổi tên…", () => PrinterService.RenameAsync(r.P, name));
    }

    [RelayCommand]
    private Task ShareAsync(PrinterRow r)
    {
        if (r.P.Shared) return Run("Đang ngừng chia sẻ…", () => PrinterService.ShareAsync(r.P, false));
        var name = DialogWindow.AskText("Chia sẻ máy in", "Tên chia sẻ (máy khác kết nối bằng \\\\TÊN-MÁY\\tên này):",
            new string(r.P.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()));
        return string.IsNullOrWhiteSpace(name) ? Task.CompletedTask : Run("Đang chia sẻ…", () => PrinterService.ShareAsync(r.P, true, name.Trim()));
    }

    [RelayCommand]
    private Task RemoveAsync(PrinterRow r) =>
        DialogWindow.Confirm($"Gỡ máy in {r.P.Name}?", "Driver vẫn được giữ lại để thêm lại nhanh.", "Gỡ", danger: true)
            ? Run("Đang gỡ máy in…", () => PrinterService.RemoveAsync(r.P)) : Task.CompletedTask;

    // ================= Thêm =================
    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (Ip.Trim().Length == 0) return;
        ProbeText = "Đang kiểm tra…";
        var (ok, detail) = await PrinterService.ProbeAsync(Ip.Trim());
        ProbeText = ok ? $"Máy in có phản hồi ({detail})." : $"Không thấy máy in ở {Ip.Trim()} ({detail}) — kiểm tra IP, cáp mạng, máy in đã bật.";
        if (ok && NewName.Length == 0) NewName = $"Máy in {Ip.Trim()}";
    }

    [RelayCommand]
    private Task AddAsync()
    {
        if (AddByIp)
        {
            if (Ip.Trim().Length == 0 || Driver is null) { DialogWindow.Info("Thiếu thông tin", "Nhập IP máy in và chọn driver."); return Task.CompletedTask; }
            var name = NewName.Trim().Length > 0 ? NewName.Trim() : $"Máy in {Ip.Trim()}";
            return Run("Đang thêm máy in…", async () => { var r = await PrinterService.AddByIpAsync(Ip, name, Driver); if (r.Ok) { Ip = NewName = ProbeText = ""; } return r; });
        }
        if (Unc.Trim().Length == 0) { DialogWindow.Info("Thiếu thông tin", "Nhập đường dẫn máy in chia sẻ, vd \\\\KETOAN-PC\\Canon2900."); return Task.CompletedTask; }
        return Run("Đang kết nối máy in chia sẻ…", async () => { var r = await PrinterService.AddSharedAsync(Unc); if (r.Ok) Unc = ""; return r; });
    }

    // ================= Sửa lỗi =================
    [RelayCommand] private Task RestartSpoolerAsync() => Run("Đang khởi động lại Print Spooler…", PrinterService.RestartSpoolerAsync);

    [RelayCommand]
    private Task FixSharingAsync()
    {
        if (RpcEnforced)
        {
            if (!DialogWindow.Confirm("Sửa lỗi 0x0000011b?",
                    "Chạy trên MÁY ĐANG CHIA SẺ máy in. Cách này hạ mức xác thực RPC của dịch vụ in (bản vá bảo mật CVE-2021-1678) để máy cũ kết nối được.\n\n" +
                    "Microsoft khuyến nghị cập nhật Windows đầy đủ cho cả hai máy thay vì cách này. Chỉ dùng trong mạng nội bộ tin cậy.", "Áp dụng", danger: true))
                return Task.CompletedTask;
            return Run("Đang áp dụng…", () => PrinterService.SetRpcPrivacyAsync(false));
        }
        return Run("Đang khôi phục mặc định…", () => PrinterService.SetRpcPrivacyAsync(true));
    }

    [RelayCommand] private void OpenSettings() => PrinterService.OpenSettings();

    // ================= Sửa lỗi nâng cao =================
    [RelayCommand]
    private async Task RunFixAsync(FixRow row)
    {
        var f = row.Fix;
        if (f.Open == "@printmgmt") { await OpenPrintManagementAsync(); return; }
        if (f.IsWarn && !DialogWindow.Confirm($"{f.Title}?", f.Warn, "Vẫn chạy", danger: true)) return;
        IsBusy = true;
        BusyText = $"Đang chạy: {f.Title}…";
        try
        {
            var r = await PrinterFixCatalog.RunAsync(f);
            var lines = r.Message.Split('\n');
            row.Ok = r.Ok;
            var body = string.Join("\n", lines.Skip(r.Ok ? 1 : 0));
            var items = lines.Count(l => l.TrimStart().StartsWith('•'));
            row.Result = f.ShowOutput && r.Ok ? $"{items} mục — đã mở cửa sổ kết quả"
                : string.Join("\n", lines.Skip(r.Ok ? 1 : 0).Take(6)) + (lines.Length > 7 ? "\n…" : "");
            if (row.Result.Length == 0) row.Result = r.Ok ? "Xong" : r.Message;
            AddLog(r.Ok ? $"{f.Title}: xong" : r.Message, r.Ok);
            if (f.ShowOutput && r.Ok) DialogWindow.ShowText(f.Title, body);
            else if (!r.Ok) DialogWindow.Info(f.Title, body, error: true);
        }
        catch (Exception ex) { row.Ok = false; row.Result = ex.Message; AddLog(ex.Message, false); }
        finally { IsBusy = false; }
        if (f.Open.Length == 0 && !f.ShowOutput) await RefreshAsync();
    }
    [RelayCommand]
    private async Task OpenPrintManagementAsync()
    {
        if (PrinterService.HasPrintManagement) { PrinterService.OpenPrintManagement(); return; }
        if (PrinterService.IsHomeEdition)
        {
            DialogWindow.Info("Windows Home không có Print Management",
                "Print Management chỉ có trên Windows Pro / Enterprise / Education. Trên máy này hãy dùng các nút ngay trong trang Máy in, hoặc Settings → Printers & scanners.");
            return;
        }
        if (!DialogWindow.Confirm("Máy chưa có Print Management",
                "Print Management là tính năng tuỳ chọn của Windows, máy này chưa cài. Cài ngay từ Windows Update (khoảng 1–3 phút, cần mạng)?", "Cài"))
            return;
        await Run("Đang cài Print Management…", PrinterService.InstallPrintManagementAsync, refresh: false);
        if (PrinterService.HasPrintManagement) PrinterService.OpenPrintManagement();
    }
}
