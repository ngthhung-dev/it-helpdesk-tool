using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Store;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Uninstall;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed class AppGroup(string name, IEnumerable<StoreAppRow> apps)
{
    public string Name { get; } = name;
    public List<StoreAppRow> Apps { get; } = apps.ToList();
}

public sealed partial class OfflineRow(string path, InstallerType type) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public InstallerType Type { get; } = type;
    public string SizeText { get; } = Fmt.Size(new FileInfo(path).Length);
    [ObservableProperty] private bool _isChecked = true;
}

public sealed partial class PostInstallViewModel : PageViewModel
{
    public override string Title => "App sau cài Win";
    private bool _loaded;

    public InstallQueue Queue { get; } = new();
    public IReadOnlyList<PostInstallPreset> Presets => PostInstallService.Presets;
    public List<StoreAppRow> All { get; }
    public List<AppGroup> Groups { get; }
    public ObservableCollection<OfflineRow> Offline { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _wingetText = "";
    [ObservableProperty] private bool _wingetMissing;

    [ObservableProperty] private bool _netFx3;
    [ObservableProperty] private string _netFx3State = "Đang kiểm tra…";
    [ObservableProperty] private bool _upgradeAll;
    [ObservableProperty] private bool _skipInstalled = true;
    [ObservableProperty] private string _offlineFolder = "";
    [ObservableProperty] private bool _offlineSilent = true;
    [ObservableProperty] private string _startText = "Bắt đầu cài";

    public PostInstallViewModel()
    {
        All = StoreCatalog.Apps.Select(a => new StoreAppRow(a)).ToList();
        foreach (var r in All) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StoreAppRow.IsChecked)) UpdateCount(); };
        Groups = StoreCatalog.Categories.Select(c => new AppGroup(c, All.Where(a => a.App.Category == c))).ToList();
        Queue.ItemFinished += i =>
        {
            foreach (var r in All.Where(a => a.App.Id == i.Key)) { r.IsQueued = false; if (i.Result?.Ok == true) { r.IsInstalled = true; r.IsChecked = false; } }
        };
        Offline.CollectionChanged += (_, _) => UpdateCount();
    }

    partial void OnNetFx3Changed(bool value) => UpdateCount();
    partial void OnUpgradeAllChanged(bool value) => UpdateCount();

    private void UpdateCount()
    {
        SelectedCount = All.Count(a => a.IsChecked) + Offline.Count(o => o.IsChecked) + (NetFx3 ? 1 : 0) + (UpgradeAll ? 1 : 0);
        StartText = SelectedCount == 0 ? "Bắt đầu cài" : $"Bắt đầu cài ({SelectedCount} mục)";
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var v = await WingetService.VersionAsync();
        WingetMissing = v.Length == 0;
        WingetText = WingetMissing ? "Máy chưa có winget — vào trang App Store bấm “Cài winget”, hoặc chỉ dùng bộ cài trong thư mục." : $"winget {v}";
        _ = RefreshNetFxAsync();
        await RefreshInstalledAsync();
        if (ShellViewModel.StartSubPage is { } preset && Presets.FirstOrDefault(p => p.Key == preset) is { } p) ApplyPreset(p);
    }

    private async Task RefreshNetFxAsync()
    {
        var on = await PostInstallService.NetFx3EnabledAsync();
        NetFx3State = on switch { true => "Đã bật", false => "Chưa bật — nhiều phần mềm cũ (kế toán, chữ ký số…) cần", _ => "Không đọc được trạng thái" };
    }

    [RelayCommand]
    private async Task RefreshInstalledAsync()
    {
        try
        {
            var regTask = Task.Run(UninstallService.ReadApps);
            var installed = WingetMissing ? new Dictionary<string, string>() : await WingetService.InstalledAsync();
            var reg = await regTask;
            foreach (var r in All)
                r.IsInstalled = r.App.Kind == StoreKind.Winget ? installed.ContainsKey(r.App.Id) : StoreInstaller.IsInstalledLocally(r.App, reg);
        }
        catch (Exception ex) { Log.Warn($"PostInstall: {ex.Message}"); }
    }

    [RelayCommand]
    private void ApplyPreset(PostInstallPreset p)
    {
        foreach (var r in All) r.IsChecked = p.Ids.Contains(r.App.Id) && !(SkipInstalled && r.IsInstalled);
        if (p.Key is "office" or "tech") NetFx3 = NetFx3State.StartsWith("Chưa");
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var r in All) r.IsChecked = false;
        foreach (var o in Offline) o.IsChecked = false;
        NetFx3 = UpgradeAll = false;
        UpdateCount();
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

    // ================= Lưu / mở danh sách =================
    [RelayCommand]
    private void SaveList()
    {
        var dlg = new SaveFileDialog { Filter = "Danh sách cài đặt (*.json)|*.json", FileName = "danh-sach-cai-dat.json" };
        if (dlg.ShowDialog() != true) return;
        PostInstallService.Save(dlg.FileName, new PostInstallList
        {
            Apps = All.Where(a => a.IsChecked).Select(a => a.App.Id).ToList(),
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
            foreach (var r in All) r.IsChecked = l.Apps.Contains(r.App.Id, StringComparer.OrdinalIgnoreCase) && !(SkipInstalled && r.IsInstalled);
            NetFx3 = l.NetFx3;
            UpgradeAll = l.UpgradeAll;
        }
        catch (Exception ex) { DialogWindow.Info("Không mở được", ex.Message, error: true); }
    }

    // ================= Bắt đầu =================
    [RelayCommand]
    private void Start()
    {
        var apps = All.Where(a => a.IsChecked && !a.IsQueued).OrderBy(a => PostInstallService.Order(a.App)).ToList();
        var offline = Offline.Where(o => o.IsChecked).ToList();
        if (apps.Count + offline.Count == 0 && !NetFx3 && !UpgradeAll)
        {
            DialogWindow.Info("Chưa chọn gì", "Chọn một bộ có sẵn ở trên, hoặc tích các phần mềm muốn cài.");
            return;
        }
        if (WingetMissing && apps.Any(a => a.App.Kind == StoreKind.Winget))
        {
            DialogWindow.Info("Chưa có winget", "Các phần mềm qua winget cần winget. Vào trang App Store bấm “Cài winget” trước.", error: true);
            return;
        }

        if (NetFx3) Queue.Add("netfx3", ".NET Framework 3.5", (s, ct) => PostInstallService.EnableNetFx3Async(s, ct));
        foreach (var r in apps)
        {
            r.IsQueued = true;
            Queue.Add(r.App.Id, r.App.Name, (s, ct) => StoreInstaller.InstallAsync(r.App, s, ct));
        }
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
        Queue.Finished += OnQueueFinished;
    }

    private void OnQueueFinished()
    {
        Queue.Finished -= OnQueueFinished;
        _ = RefreshNetFxAsync();
        _ = RefreshInstalledAsync();
    }
}
