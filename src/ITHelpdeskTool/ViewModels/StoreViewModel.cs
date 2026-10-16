using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Store;
using ITHelpdeskTool.Services.Uninstall;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

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

public sealed partial class PresetChip(PostInstallPreset preset) : ObservableObject
{
    public PostInstallPreset Preset { get; } = preset;
    public string Label { get; } = $"{preset.Name} · {preset.Ids.Length}";
    [ObservableProperty] private bool _isActive;
}

public sealed partial class OfflineRow(string path, InstallerType type) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public InstallerType Type { get; } = type;
    public string SizeText { get; } = Fmt.Size(new FileInfo(path).Length);
    [ObservableProperty] private bool _isChecked = true;
}

public sealed partial class StoreViewModel : PageViewModel
{
    public override string Title => "App Store";
    private bool _loaded;
    private Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);

    public InstallQueue Queue { get; } = new();
    public ObservableCollection<StoreAppRow> Apps { get; } = [];
    /// <summary>
    /// Danh sách hiển thị đã lọc. Lọc bằng CollectionView (không ẩn thẻ bằng Collapsed): WrapPanel có ItemWidth
    /// vẫn chừa chỗ cho phần tử bị ẩn, làm lưới bị trống ô ở đầu.
    /// </summary>
    public System.ComponentModel.ICollectionView AppsView { get; }
    public ObservableCollection<CategoryChip> Categories { get; } = [];
    public ObservableCollection<UpgradeRow> Upgrades { get; } = [];
    public ObservableCollection<SearchRow> SearchResults { get; } = [];
    public ObservableCollection<OfflineRow> Offline { get; } = [];
    public IReadOnlyList<PostInstallPreset> Presets => PostInstallService.Presets;
    public List<PresetChip> PresetChips { get; }
    private bool _presetSync;

    [ObservableProperty] private bool _tabBrowse = true;
    [ObservableProperty] private bool _tabUpdates;
    [ObservableProperty] private bool _tabExtras;
    partial void OnTabUpdatesChanged(bool value) { if (value && !_upgradesLoaded) _ = LoadUpgradesAsync(); }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _wingetText = "Đang kiểm tra winget…";
    [ObservableProperty] private bool _wingetMissing;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InstallText))] private int _selectedCount;
    public string InstallText => SelectedCount == 0 ? "Cài mục đã chọn" : $"Cài {SelectedCount} mục đã chọn";
    [ObservableProperty] private bool _skipInstalled = true;
    [ObservableProperty] private bool _netFx3;
    [ObservableProperty] private string _netFx3State = "Đang kiểm tra…";
    [ObservableProperty] private bool _upgradeAll;
    [ObservableProperty] private string _offlineFolder = "";
    [ObservableProperty] private bool _offlineSilent = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _upgradeSummary = "";
    private bool _upgradesLoaded;

    public StoreViewModel()
    {
        foreach (var a in StoreCatalog.Apps)
        {
            var row = new StoreAppRow(a);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StoreAppRow.IsChecked)) UpdateCount(); };
            Apps.Add(row);
        }
        AppsView = new System.Windows.Data.ListCollectionView(Apps) { Filter = o => o is StoreAppRow r && r.IsVisible };
        Categories.Add(new CategoryChip("", "Tất cả", Apps.Count) { IsSelected = true });
        foreach (var c in StoreCatalog.Categories) Categories.Add(new CategoryChip(c, c, Apps.Count(r => r.App.Category == c)));
        foreach (var chip in Categories)
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CategoryChip.IsSelected) && chip.IsSelected) Category = chip.Key; };
        PresetChips = Presets.Select(p => new PresetChip(p)).ToList();
        foreach (var pc in PresetChips)
            pc.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PresetChip.IsActive) && !_presetSync) TogglePreset(pc); };
        Queue.ItemFinished += OnItemFinished;
        Queue.Finished += () => { _ = RefreshInstalledAsync(); _ = RefreshNetFxAsync(); };
        Offline.CollectionChanged += (_, _) => UpdateCount();
    }

    partial void OnNetFx3Changed(bool value) => UpdateCount();
    partial void OnUpgradeAllChanged(bool value) => UpdateCount();

    private void UpdateCount() =>
        SelectedCount = Apps.Count(a => a.IsChecked) + Offline.Count(o => o.IsChecked) + (NetFx3 ? 1 : 0) + (UpgradeAll ? 1 : 0);

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await CheckWingetAsync();
        _ = RefreshNetFxAsync();
        var sub = ShellViewModel.StartSubPage;
        if (sub == "updates") { TabBrowse = false; TabUpdates = true; }
        else if (sub == "extras") { TabBrowse = false; TabExtras = true; }
        await RefreshInstalledAsync();
        if (sub is not null && PresetChips.FirstOrDefault(p => p.Preset.Key == sub) is { } preset) preset.IsActive = true;
    }

    private async Task RefreshNetFxAsync()
    {
        var on = await PostInstallService.NetFx3EnabledAsync();
        NetFx3State = on switch { true => "Đã bật", false => "Chưa bật — nhiều phần mềm cũ (kế toán, chữ ký số…) cần", _ => "Không đọc được trạng thái" };
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
        AppsView.Refresh();
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

    /// <summary>Cài mọi mục đã chọn: .NET 3.5 → phần mềm (runtime trước) → bộ cài trong thư mục → cập nhật tất cả.</summary>
    [RelayCommand]
    private void InstallSelected()
    {
        var apps = Apps.Where(a => a.IsChecked && !a.IsQueued).OrderBy(a => PostInstallService.Order(a.App)).ToList();
        var offline = Offline.Where(o => o.IsChecked).ToList();
        if (apps.Count + offline.Count == 0 && !NetFx3 && !UpgradeAll)
        {
            DialogWindow.Info("Chưa chọn gì", "Chọn một bộ có sẵn, hoặc tích các phần mềm muốn cài.");
            return;
        }
        if (WingetMissing && (UpgradeAll || apps.Any(a => a.App.Kind == StoreKind.Winget)))
        {
            DialogWindow.Info("Chưa có winget", "Các phần mềm qua winget cần winget. Bấm “Cài winget” ở đầu trang trước.", error: true);
            return;
        }

        if (NetFx3) Queue.Add("netfx3", ".NET Framework 3.5", (s, ct) => PostInstallService.EnableNetFx3Async(s, ct));
        foreach (var r in apps) { Enqueue(r); r.IsChecked = false; }
        var silent = OfflineSilent;
        foreach (var o in offline)
            Queue.Add("file:" + o.Path, o.FileName, async (s, ct) =>
            {
                s(silent && o.Type.CanSilent ? $"Đang cài im lặng ({o.Type.Label})…" : "Đang chạy bộ cài — làm theo hướng dẫn trên cửa sổ cài đặt");
                return await InstallerRunner.RunAsync(o.Path, o.FileName, silent, ct);
            });
        if (UpgradeAll) Queue.Add("upgrade-all", "Cập nhật các phần mềm khác", (s, ct) =>
            WingetService.UpgradeAllAsync(line => { if (StoreInstaller.Stage(line) is { } x) s(x); }, ct));

        foreach (var o in offline) o.IsChecked = false;
        NetFx3 = UpgradeAll = false;
        ResetPresets();
    }

    private void ResetPresets()
    {
        _presetSync = true;
        foreach (var pc in PresetChips) pc.IsActive = false;
        _presetSync = false;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var a in Apps) a.IsChecked = false;
        foreach (var o in Offline) o.IsChecked = false;
        NetFx3 = UpgradeAll = false;
        ResetPresets();
    }

    // ================= Bộ có sẵn / danh sách =================
    /// <summary>
    /// Bấm bộ có sẵn: bật → tích thêm các phần mềm của bộ; bấm lần nữa → bỏ tích các phần mềm của bộ
    /// (trừ phần mềm cũng thuộc bộ khác đang bật). Bật được nhiều bộ cùng lúc.
    /// </summary>
    private void TogglePreset(PresetChip chip)
    {
        var p = chip.Preset;
        if (chip.IsActive)
        {
            ShowAll();
            foreach (var r in Apps.Where(r => p.Ids.Contains(r.App.Id) && !r.IsQueued && !(SkipInstalled && r.IsInstalled))) r.IsChecked = true;
            if (p.Key is "office" or "tech" && NetFx3State.StartsWith("Chưa")) NetFx3 = true;
            TabUpdates = TabExtras = false;
            TabBrowse = true;
        }
        else
        {
            var keep = PresetChips.Where(c => c.IsActive).SelectMany(c => c.Preset.Ids).ToHashSet();
            foreach (var r in Apps.Where(r => p.Ids.Contains(r.App.Id) && !keep.Contains(r.App.Id))) r.IsChecked = false;
            if (p.Key is "office" or "tech" && !PresetChips.Any(c => c.IsActive && c.Preset.Key is "office" or "tech")) NetFx3 = false;
        }
    }

    private void ShowAll()
    {
        Filter = "";
        foreach (var c in Categories) c.IsSelected = c.Key.Length == 0;
        Category = "";
    }

    [RelayCommand]
    private void SaveList()
    {
        var dlg = new SaveFileDialog { Filter = "Danh sách cài đặt (*.json)|*.json", FileName = "danh-sach-cai-dat.json" };
        if (dlg.ShowDialog() != true) return;
        PostInstallService.Save(dlg.FileName, new PostInstallList
        {
            Apps = Apps.Where(a => a.IsChecked).Select(a => a.App.Id).ToList(),
            NetFx3 = NetFx3,
            UpgradeAll = UpgradeAll,
        });
    }

    [RelayCommand]
    private void OpenList()
    {
        var dlg = new OpenFileDialog { Filter = "Danh sách cài đặt (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var l = PostInstallService.Load(dlg.FileName);
            ResetPresets();
            ShowAll();
            foreach (var r in Apps) r.IsChecked = l.Apps.Contains(r.App.Id, StringComparer.OrdinalIgnoreCase) && !(SkipInstalled && r.IsInstalled);
            NetFx3 = l.NetFx3;
            UpgradeAll = l.UpgradeAll;
        }
        catch (Exception ex) { DialogWindow.Info("Không mở được", ex.Message, error: true); }
    }

    // ================= Bộ cài trong thư mục =================
    [RelayCommand]
    private void PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục chứa bộ cài (.exe, .msi)" };
        if (dlg.ShowDialog() != true) return;
        LoadFolder(dlg.FolderName);
    }

    public void LoadFolder(string dir)
    {
        if (!Directory.Exists(dir)) return;
        OfflineFolder = dir;
        Offline.Clear();
        foreach (var f in PostInstallService.ScanFolder(dir))
        {
            var row = new OfflineRow(f, InstallerRunner.Detect(f));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OfflineRow.IsChecked)) UpdateCount(); };
            Offline.Add(row);
        }
        if (Offline.Count == 0) DialogWindow.Info("Không có bộ cài", "Thư mục này không có file .exe / .msi nào.");
    }

    [RelayCommand]
    private void ClearOffline() { Offline.Clear(); OfflineFolder = ""; }

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
