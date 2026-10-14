using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Uninstall;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class AppRow(InstalledApp app) : ObservableObject
{
    public InstalledApp App { get; } = app;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class LeftoverRow(Leftover item) : ObservableObject
{
    public Leftover Item { get; } = item;
    [ObservableProperty] private bool _isChecked = item.HighConfidence;
    public string SizeText => Item.Bytes > 0 ? Fmt.Size(Item.Bytes) : "";
}

public sealed partial class AppxRow(AppxApp app) : ObservableObject
{
    public AppxApp App { get; } = app;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class UninstallViewModel : PageViewModel
{
    public override string Title => "Gỡ app sạch";
    private bool _loaded;
    private List<InstalledApp> _all = [];

    // ================= Tab =================
    [ObservableProperty] private bool _tabApps = true;
    [ObservableProperty] private bool _tabStore;
    partial void OnTabStoreChanged(bool value) { if (value && !_storeLoaded) _ = LoadStoreAsync(); }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    public ObservableCollection<AppRow> Apps { get; } = [];

    // Bảng điều khiển gỡ của app đang chọn
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private InstalledApp? _selected;
    public bool HasSelection => Selected is not null;
    [ObservableProperty] private bool _quiet = true;
    [ObservableProperty] private string _stage = "";       // "", running, scanned
    [ObservableProperty] private string _stageText = "";
    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    [ObservableProperty] private string _leftoverSummary = "";
    private CancellationTokenSource? _waitCts;

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Uninstall: {text}"); else Log.Warn($"Uninstall: {text}");
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Đang đọc danh sách phần mềm…";
        try
        {
            _all = await Task.Run(UninstallService.ReadApps);
            Apps.Clear();
            foreach (var a in _all) Apps.Add(new AppRow(a));
            ApplyFilter();
            var total = _all.Sum(a => a.SizeKb) * 1024d;
            Summary = $"{_all.Count} phần mềm · khoảng {Fmt.Size(total)}";
        }
        catch (Exception ex) { AddLog($"Không đọc được danh sách: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        bool M(params string[] s) => f.Length == 0 || s.Any(x => x.Contains(f, StringComparison.CurrentCultureIgnoreCase));
        foreach (var r in Apps) r.IsVisible = M(r.App.Name, r.App.Publisher);
        foreach (var r in StoreApps) r.IsVisible = M(r.App.Display, r.App.Name, r.App.Publisher);
    }

    [RelayCommand]
    private void Select(AppRow row)
    {
        _waitCts?.Cancel();
        Selected = row.App;
        Stage = "";
        StageText = row.App.HasUninstaller ? "" : "Phần mềm này không có trình gỡ (mục hỏng) — bấm “Quét phần còn sót” để dọn.";
        Leftovers.Clear();
        LeftoverSummary = "";
    }

    [RelayCommand]
    private void CloseSelection()
    {
        _waitCts?.Cancel();
        Selected = null;
        Leftovers.Clear();
    }

    /// <summary>Bước 1: chạy trình gỡ, chờ xong rồi tự quét phần sót.</summary>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (Selected is not { } a) return;
        if (!DialogWindow.Confirm($"Gỡ {a.Name}?",
                (Quiet ? "Gỡ im lặng nếu được (không hiện cửa sổ của hãng). " : "Trình gỡ của hãng sẽ mở ra, làm theo hướng dẫn trên đó. ") +
                "Gỡ xong app sẽ tự quét phần còn sót để bạn chọn xoá.", "Gỡ", danger: true))
            return;

        Stage = "running";
        StageText = "Đang chạy trình gỡ cài đặt… (nếu có cửa sổ của hãng hiện ra, làm theo hướng dẫn)";
        _waitCts = new CancellationTokenSource();
        var ct = _waitCts.Token;
        var r = await UninstallService.RunUninstallerAsync(a, Quiet);
        AddLog(r.Message, r.Ok);

        // Nhiều trình gỡ (Inno, NSIS) tự chép ra thư mục tạm rồi thoát ngay → chờ mục trong registry biến mất
        try
        {
            StageText = "Đang chờ trình gỡ hoàn tất… Bấm “Quét phần còn sót” nếu đã gỡ xong.";
            var until = DateTime.Now.AddMinutes(10);
            while (UninstallService.StillInstalled(a) && DateTime.Now < until)
                await Task.Delay(1000, ct);
        }
        catch (OperationCanceledException) { return; }

        await ScanAsync();
    }

    /// <summary>Bước 2: tìm phần còn sót.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (Selected is not { } a) return;
        _waitCts?.Cancel();
        IsBusy = true;
        BusyText = "Đang tìm file, registry, dịch vụ còn sót…";
        try
        {
            var others = await Task.Run(UninstallService.ReadApps);
            var items = await UninstallService.FindLeftoversAsync(a, others);
            Leftovers.Clear();
            foreach (var l in items.OrderBy(l => l.Kind)) Leftovers.Add(new LeftoverRow(l));
            Stage = "scanned";
            var still = UninstallService.StillInstalled(a);
            StageText = still && a.HasUninstaller
                ? "Phần mềm vẫn còn trong danh sách đã cài — có thể trình gỡ chưa chạy xong hoặc bị huỷ."
                : items.Count == 0 ? "Sạch sẽ — không tìm thấy gì còn sót." : "";
            LeftoverSummary = items.Count == 0 ? ""
                : $"Tìm thấy {items.Count} mục còn sót · {Fmt.Size(items.Sum(i => i.Bytes))}. Mục có độ tin cậy thấp chưa được tích sẵn — xem kỹ trước khi chọn.";
        }
        catch (Exception ex) { AddLog($"Quét lỗi: {ex.Message}", false); }
        finally { IsBusy = false; }
    }

    /// <summary>Bước 3: xoá các mục đã chọn.</summary>
    [RelayCommand]
    private async Task RemoveLeftoversAsync()
    {
        if (Selected is not { } a) return;
        var sel = Leftovers.Where(l => l.IsChecked).Select(l => l.Item).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn mục nào", "Tích chọn các mục còn sót muốn xoá."); return; }
        if (!DialogWindow.Confirm($"Xoá {sel.Count} mục còn sót?",
                "File và thư mục được đưa vào Thùng rác; registry được sao lưu ra file .reg trước khi xoá.", "Xoá", danger: true))
            return;
        IsBusy = true;
        BusyText = "Đang dọn phần còn sót…";
        OpResult r;
        try { r = await UninstallService.RemoveLeftoversAsync(a, sel); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa dọn hết", r.Message, error: true);
        await ScanAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private void SelectLeftovers(string which)
    {
        foreach (var l in Leftovers) l.IsChecked = which switch { "all" => true, "none" => false, _ => l.Item.HighConfidence };
    }

    [RelayCommand]
    private void OpenBackups()
    {
        System.IO.Directory.CreateDirectory(UninstallService.BackupRoot);
        ShellLauncher.Open(UninstallService.BackupRoot);
    }

    [RelayCommand]
    private void OpenLocation(LeftoverRow row)
    {
        var p = row.Item.Path;
        if (System.IO.Directory.Exists(p)) ShellLauncher.Open(p);
        else if (System.IO.File.Exists(p)) ShellLauncher.Open("explorer.exe", $"/select,\"{p}\"");
    }

    // ================= Ứng dụng Store =================
    private bool _storeLoaded;
    public ObservableCollection<AppxRow> StoreApps { get; } = [];
    [ObservableProperty] private string _storeSummary = "Đang đọc…";
    [ObservableProperty] private bool _storeAllUsers;

    [RelayCommand]
    private async Task LoadStoreAsync()
    {
        _storeLoaded = true;
        IsBusy = true;
        BusyText = "Đang đọc ứng dụng Store…";
        try
        {
            var list = await AppxService.ReadAsync();
            StoreApps.Clear();
            foreach (var a in list) StoreApps.Add(new AppxRow(a));
            StoreSummary = $"{list.Count} ứng dụng · {list.Count(a => a.IsBloat)} thuộc nhóm cài sẵn thường không cần";
            ApplyFilter();
        }
        catch (Exception ex) { StoreSummary = $"Không đọc được: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SelectBloat()
    {
        foreach (var r in StoreApps) r.IsChecked = r.App.IsBloat;
    }

    [RelayCommand]
    private async Task RemoveStoreAsync()
    {
        var sel = StoreApps.Where(r => r.IsChecked).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn ứng dụng", "Tích chọn ứng dụng muốn gỡ."); return; }
        if (!DialogWindow.Confirm($"Gỡ {sel.Count} ứng dụng?",
                string.Join(", ", sel.Select(s => s.App.Display)) +
                (StoreAllUsers ? "\n\nGỡ cho mọi tài khoản và không cho Windows tự cài lại cho tài khoản mới." : "\n\nChỉ gỡ cho tài khoản đang dùng.") +
                "\nCài lại được từ Microsoft Store.", "Gỡ", danger: true))
            return;
        IsBusy = true;
        var ok = 0;
        try
        {
            foreach (var r in sel)
            {
                BusyText = $"Đang gỡ {r.App.Display}…";
                var res = await AppxService.RemoveAsync(r.App, StoreAllUsers);
                AddLog(res.Message, res.Ok);
                if (res.Ok) ok++;
            }
        }
        finally { IsBusy = false; }
        AddLog($"Đã gỡ {ok}/{sel.Count} ứng dụng Store.", ok == sel.Count);
        await LoadStoreAsync();
    }
}
