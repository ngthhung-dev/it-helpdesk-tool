using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Store;
using ITHelpdeskTool.Services.Uninstall;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class StoreAppRow(StoreApp app) : ObservableObject
{
    public StoreApp App { get; } = app;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private bool _isInstalled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private string _installedVersion = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private string _available = "";
    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isVisible = true;
    public bool HasUpdate => Available.Length > 0;
    partial void OnAvailableChanged(string value) => OnPropertyChanged(nameof(HasUpdate));
    public string SourceText => App.Kind switch { StoreKind.Winget => "winget", StoreKind.Portable => "bản chạy liền", _ => "trang hãng" };
    public string StateText => !IsInstalled ? "" : HasUpdate ? $"Có bản mới {Available}" : InstalledVersion.Length > 0 && InstalledVersion != "Unknown" ? $"Đã cài {InstalledVersion}" : "Đã cài";
    public int Hue => Math.Abs(App.Name.Aggregate(17, (h, c) => h * 31 + c)) % 6;
}

public sealed partial class UpgradeRow(WingetRow row) : ObservableObject
{
    public WingetRow Row { get; } = row;
    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private bool _isQueued;
    public bool CanUpgrade => !Row.Id.EndsWith('…');
}

public sealed partial class SearchRow(WingetRow row, bool installed) : ObservableObject
{
    public WingetRow Row { get; } = row;
    [ObservableProperty] private bool _isInstalled = installed;
    [ObservableProperty] private bool _isQueued;
}

public sealed partial class CategoryChip(string key, string label, int count) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = $"{label} · {count}";
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class StoreViewModel : PageViewModel
{
    public override string Title => "App Store";
    private bool _loaded;
    private Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);

    public InstallQueue Queue { get; } = new();
    public ObservableCollection<StoreAppRow> Apps { get; } = [];
    public ObservableCollection<CategoryChip> Categories { get; } = [];
    public ObservableCollection<UpgradeRow> Upgrades { get; } = [];
    public ObservableCollection<SearchRow> SearchResults { get; } = [];

    [ObservableProperty] private bool _tabBrowse = true;
    [ObservableProperty] private bool _tabUpdates;
    partial void OnTabUpdatesChanged(bool value) { if (value && !_upgradesLoaded) _ = LoadUpgradesAsync(); }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _wingetText = "Đang kiểm tra winget…";
    [ObservableProperty] private bool _wingetMissing;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _upgradeSummary = "";
    private bool _upgradesLoaded;

    public StoreViewModel()
    {
        foreach (var a in StoreCatalog.Apps)
        {
            var row = new StoreAppRow(a);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StoreAppRow.IsChecked)) SelectedCount = Apps.Count(r => r.IsChecked); };
            Apps.Add(row);
        }
        Categories.Add(new CategoryChip("", "Tất cả", Apps.Count) { IsSelected = true });
        foreach (var c in StoreCatalog.Categories) Categories.Add(new CategoryChip(c, c, Apps.Count(r => r.App.Category == c)));
        foreach (var chip in Categories)
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CategoryChip.IsSelected) && chip.IsSelected) Category = chip.Key; };
        Queue.ItemFinished += OnItemFinished;
        Queue.Finished += () => _ = RefreshInstalledAsync();
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await CheckWingetAsync();
        if (ShellViewModel.StartSubPage == "updates") { TabBrowse = false; TabUpdates = true; }
        await RefreshInstalledAsync();
    }

    private async Task CheckWingetAsync()
    {
        var v = await WingetService.VersionAsync();
        WingetMissing = v.Length == 0;
        WingetText = WingetMissing ? "Máy chưa có winget (App Installer) — các mục qua winget chưa cài được." : $"winget {v}";
    }

    [RelayCommand]
    private async Task RepairWingetAsync()
    {
        IsBusy = true;
        BusyText = "Đang cài winget…";
        try
        {
            var r = await WingetService.RepairAsync();
            if (!r.Ok) DialogWindow.Info("Chưa cài được winget", r.Message + "\n\nCó thể cài “App Installer” từ Microsoft Store.", error: true);
            await CheckWingetAsync();
            if (!WingetMissing) await RefreshInstalledAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        try
        {
            var regTask = Task.Run(UninstallService.ReadApps);
            if (!WingetMissing)
            {
                try { _installed = await WingetService.InstalledAsync(); }
                catch (Exception ex) { Log.Warn($"winget export: {ex.Message}"); }
            }
            var reg = await regTask;
            foreach (var r in Apps)
            {
                if (r.App.Kind == StoreKind.Winget)
                {
                    r.IsInstalled = _installed.TryGetValue(r.App.Id, out var v);
                    r.InstalledVersion = v ?? "";
                }
                else r.IsInstalled = StoreInstaller.IsInstalledLocally(r.App, reg);
            }
            foreach (var s in SearchResults) s.IsInstalled = _installed.ContainsKey(s.Row.Id);
            var n = Apps.Count(a => a.IsInstalled);
            Summary = $"{Apps.Count} phần mềm · đã cài {n}";
        }
        catch (Exception ex) { Log.Warn($"Store: {ex.Message}"); }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnCategoryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        foreach (var r in Apps)
            r.IsVisible = (Category.Length == 0 || r.App.Category == Category) &&
                          (f.Length == 0 || r.App.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                                         || r.App.Description.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                                         || r.App.Id.Contains(f, StringComparison.OrdinalIgnoreCase));
        SearchText = f.Length >= 2 ? $"Tìm “{f}” trong toàn bộ kho winget" : "";
    }

    // ================= Cài / gỡ =================
    private void Enqueue(StoreAppRow r)
    {
        if (r.IsQueued) return;
        r.IsQueued = true;
        Queue.Add(r.App.Id, r.App.Name, (s, ct) => StoreInstaller.InstallAsync(r.App, s, ct));
    }

    [RelayCommand]
    private void Install(StoreAppRow r)
    {
        if (r.App.Kind == StoreKind.Winget && WingetMissing) { DialogWindow.Info("Chưa có winget", "Bấm “Cài winget” ở đầu trang trước."); return; }
        Enqueue(r);
    }

    [RelayCommand]
    private void InstallSelected()
    {
        var sel = Apps.Where(a => a.IsChecked && !a.IsQueued).ToList();
        if (sel.Count == 0) return;
        foreach (var r in sel) { Enqueue(r); r.IsChecked = false; }
    }

    [RelayCommand]
    private void ClearSelection() { foreach (var a in Apps) a.IsChecked = false; }

    [RelayCommand]
    private async Task UninstallAsync(StoreAppRow r)
    {
        if (!DialogWindow.Confirm($"Gỡ {r.App.Name}?", "Gỡ im lặng bằng trình gỡ của phần mềm. Muốn dọn sạch phần còn sót, dùng trang “Gỡ app sạch”.", "Gỡ", danger: true)) return;
        IsBusy = true;
        BusyText = $"Đang gỡ {r.App.Name}…";
        try
        {
            var res = await StoreInstaller.UninstallAsync(r.App);
            if (!res.Ok) DialogWindow.Info("Chưa gỡ được", res.Message, error: true);
            await RefreshInstalledAsync();
        }
        finally { IsBusy = false; }
    }

    private void OnItemFinished(QueueItem item)
    {
        foreach (var r in Apps.Where(a => a.App.Id == item.Key)) { r.IsQueued = false; if (item.Result?.Ok == true) { r.IsInstalled = true; r.Available = ""; } }
        foreach (var u in Upgrades.Where(u => u.Row.Id == item.Key).ToList()) { u.IsQueued = false; if (item.Result?.Ok == true) Upgrades.Remove(u); }
        foreach (var s in SearchResults.Where(s => s.Row.Id == item.Key)) { s.IsQueued = false; if (item.Result?.Ok == true) s.IsInstalled = true; }
    }

    // ================= Tìm trên winget =================
    [RelayCommand]
    private async Task SearchWingetAsync()
    {
        var q = Filter.Trim();
        if (q.Length < 2 || WingetMissing) return;
        IsBusy = true;
        BusyText = $"Đang tìm “{q}” trên winget…";
        try
        {
            var rows = await WingetService.SearchAsync(q);
            SearchResults.Clear();
            foreach (var r in rows.Take(40)) SearchResults.Add(new SearchRow(r, _installed.ContainsKey(r.Id)));
            if (rows.Count == 0) DialogWindow.Info("Không tìm thấy", $"Kho winget không có phần mềm khớp “{q}”.");
        }
        catch (Exception ex) { DialogWindow.Info("Tìm lỗi", ex.Message, error: true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void InstallSearch(SearchRow r)
    {
        if (r.IsQueued || r.Row.Truncated) return;
        r.IsQueued = true;
        Queue.Add(r.Row.Id, r.Row.Name, (s, ct) => WingetService.InstallAsync(r.Row.Id, r.Row.Name, line => { if (StoreInstaller.Stage(line) is { } x) s(x); }, ct));
    }

    [RelayCommand]
    private void ClearSearch() => SearchResults.Clear();

    // ================= Cập nhật =================
    [RelayCommand]
    private async Task LoadUpgradesAsync()
    {
        if (WingetMissing) { UpgradeSummary = "Cần winget để kiểm tra bản cập nhật."; return; }
        _upgradesLoaded = true;
        IsBusy = true;
        BusyText = "Đang kiểm tra bản cập nhật…";
        try
        {
            if (_installed.Count == 0) _installed = await WingetService.InstalledAsync();
            var rows = await WingetService.UpgradesAsync(_installed);
            Upgrades.Clear();
            foreach (var r in rows) Upgrades.Add(new UpgradeRow(r));
            foreach (var a in Apps) a.Available = rows.FirstOrDefault(r => r.Id.Equals(a.App.Id, StringComparison.OrdinalIgnoreCase))?.Available ?? "";
            UpgradeSummary = rows.Count == 0 ? "Tất cả phần mềm winget quản lý đều đã là bản mới nhất." : $"{rows.Count} phần mềm có bản mới";
        }
        catch (Exception ex) { UpgradeSummary = $"Không kiểm tra được: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Upgrade(UpgradeRow r)
    {
        if (r.IsQueued || !r.CanUpgrade) return;
        r.IsQueued = true;
        Queue.Add(r.Row.Id, r.Row.Name, (s, ct) => WingetService.UpgradeAsync(r.Row.Id, r.Row.Name, line => { if (StoreInstaller.Stage(line) is { } x) s(x); }, ct));
    }

    [RelayCommand]
    private void UpgradeSelected()
    {
        foreach (var r in Upgrades.Where(u => u.IsChecked)) Upgrade(r);
    }

    [RelayCommand]
    private void UpgradeAppRow(StoreAppRow r)
    {
        if (r.IsQueued) return;
        r.IsQueued = true;
        Queue.Add(r.App.Id, r.App.Name, (s, ct) => WingetService.UpgradeAsync(r.App.Id, r.App.Name, line => { if (StoreInstaller.Stage(line) is { } x) s(x); }, ct));
    }
}
