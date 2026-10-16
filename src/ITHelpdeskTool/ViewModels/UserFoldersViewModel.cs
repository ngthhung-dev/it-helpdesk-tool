using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Disk;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class UserFolderRow(UserFolder f) : ObservableObject
{
    public UserFolder F { get; } = f;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _sizeText = "Đang tính…";
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _oneDrive;
    [ObservableProperty] private bool _onSystemDrive;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusOk = true;
    [ObservableProperty] private bool _exists = true;
}

public sealed partial class UserFoldersViewModel : PageViewModel
{
    public override string Title => "Chuyển thư mục";
    private bool _loaded;

    public ObservableCollection<UserFolderRow> Folders { get; } = [];
    /// <summary>Dữ liệu ứng dụng (Zalo, CapCut…) — chuyển bằng junction.</summary>
    public ObservableCollection<UserFolderRow> AppFolders { get; } = [];
    private IEnumerable<UserFolderRow> AllRows => Folders.Concat(AppFolders);
    public ObservableCollection<string> Bases { get; } = [];
    [ObservableProperty] private string _basePath = "";
    [ObservableProperty] private string _account = $"{Environment.UserDomainName}\\{Environment.UserName}";
    [ObservableProperty] private string _systemFree = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";

    public UserFoldersViewModel()
    {
        foreach (var f in UserFolderService.Folders) Folders.Add(new UserFolderRow(f));
        foreach (var f in UserFolderService.AppFolders) AppFolders.Add(new UserFolderRow(f));
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Đọc ổ ở luồng nền: hỏi ổ mạng / đầu đọc thẻ có thể mất vài giây
        foreach (var b in await Task.Run(UserFolderService.SuggestedBases)) Bases.Add(b);
        if (BasePath.Length == 0) BasePath = Bases.FirstOrDefault() ?? "";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;
        // Đọc vị trí / trạng thái ở luồng nền (thư mục có thể nằm trên ổ mạng, ổ USB đã rút…)
        var info = await Task.Run(() =>
        {
            var d = new DriveInfo(sys);
            var free = $"Ổ Windows {sys.TrimEnd('\\')} còn trống {Fmt.Size(d.AvailableFreeSpace)} / {Fmt.Size(d.TotalSize)}";
            var rows = AllRows.Select(r => (r, path: UserFolderService.GetPath(r.F), def: UserFolderService.IsDefault(r.F),
                exists: !r.F.IsApp || UserFolderService.AppDataExists(r.F))).ToList();
            return (free, rows);
        });
        SystemFree = info.free;
        foreach (var (r, path, def, exists) in info.rows)
        {
            r.Path = path;
            r.IsDefault = def;
            r.Exists = exists;
            r.OnSystemDrive = path.StartsWith(sys, StringComparison.OrdinalIgnoreCase);
            if (r.F.IsApp)
            {
                if (!exists) { r.IsChecked = false; r.SizeText = "Chưa có dữ liệu (chưa cài hoặc chưa mở lần nào)"; }
                continue;
            }
            r.OneDrive = UserFolderService.InOneDrive(path);
            r.IsChecked = r.OnSystemDrive && !r.OneDrive && r.F.Key is "documents" or "downloads" or "desktop" or "pictures" or "videos";
        }
        // Đo dung lượng lần lượt từng 2 thư mục — đo song song tất cả làm ổ cứng HDD quá tải, máy ì
        foreach (var r in AllRows.Where(r => r.Exists)) r.SizeText = "Đang tính…";
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(AllRows.Where(r => r.Exists).Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                var (b, n) = await Task.Run(() => UserFolderService.Measure(r.Path));
                r.SizeText = $"{Fmt.Size(b)} · {n:N0} file";
            }
            finally { gate.Release(); }
        }));
    }

    [RelayCommand] private void UseBase(string b) => BasePath = b;

    [RelayCommand]
    private void PickBase()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn nơi chứa các thư mục (vd D:\\Users\\ten)" };
        if (dlg.ShowDialog() == true) BasePath = dlg.FolderName;
    }

    private async Task MoveOne(UserFolderRow r, string target)
    {
        BusyText = $"Đang chuyển {r.F.Name}…";
        r.Status = "Đang chuyển…";
        r.StatusOk = true;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        OpResult res;
        try { res = await Task.Run(() => UserFolderService.MoveAsync(r.F, target, s => dispatcher.BeginInvoke(() => { BusyText = s; r.Status = s; }), CancellationToken.None)); }
        catch (Exception ex) { res = OpResult.Fail(ex.Message); }
        r.Status = res.Message;
        r.StatusOk = res.Ok;
        if (res.Ok) Log.Info($"UserFolders: {res.Message}"); else Log.Warn($"UserFolders: {res.Message}");
    }

    [RelayCommand]
    private async Task MoveSelectedAsync()
    {
        var sel = AllRows.Where(f => f.IsChecked && f.Exists).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn thư mục", "Tích các thư mục muốn chuyển."); return; }
        var b = BasePath.Trim();
        if (b.Length < 4 || !System.IO.Path.IsPathRooted(b)) { DialogWindow.Info("Chưa chọn nơi chứa", "Chọn thư mục đích, vd D:\\Users\\ten."); return; }
        if (sel.Any(f => f.OneDrive) && !DialogWindow.Confirm("Thư mục đang được OneDrive sao lưu",
                "Một số thư mục đang nằm trong OneDrive. Nên tắt sao lưu thư mục trong OneDrive (Settings › Sync and backup) trước khi chuyển.", "Vẫn chuyển"))
            return;
        if (!DialogWindow.Confirm($"Chuyển {sel.Count} thư mục sang {b}?",
                string.Join("\n", sel.Select(f => $"{f.F.Name}: {f.Path} → {System.IO.Path.Combine(b, f.F.DefaultName)}")) +
                "\n\nFile được chuyển (không nhân đôi). Đóng các ứng dụng đang mở file trong các thư mục này trước khi chuyển.", "Chuyển"))
            return;
        if (!await CloseAppsIfNeeded(sel)) return;
        IsBusy = true;
        try { foreach (var r in sel) await MoveOne(r, System.IO.Path.Combine(b, r.F.DefaultName)); }
        finally { IsBusy = false; }
        await RefreshAsync();
        if (sel.Any(r => !r.F.IsApp)) AskRestartExplorer();
    }

    /// <summary>Ứng dụng đang mở thì không chuyển được dữ liệu — hỏi rồi đóng giúp.</summary>
    private async Task<bool> CloseAppsIfNeeded(IEnumerable<UserFolderRow> rows)
    {
        var running = rows.Where(r => r.F.IsApp && UserFolderService.RunningApps(r.F).Count > 0).ToList();
        if (running.Count == 0) return true;
        var names = string.Join(", ", running.Select(r => r.F.Name));
        if (!DialogWindow.Confirm($"Đóng {names}?", $"{names} đang mở nên chưa chuyển được dữ liệu. Đóng {names} ngay (nhớ lưu việc đang làm)?", "Đóng và chuyển"))
            return false;
        IsBusy = true;
        BusyText = $"Đang đóng {names}…";
        try { foreach (var r in running) await UserFolderService.CloseAppsAsync(r.F); }
        finally { IsBusy = false; }
        return true;
    }

    [RelayCommand]
    private async Task RestoreAsync(UserFolderRow r)
    {
        var target = UserFolderService.DefaultPath(r.F);
        if (!DialogWindow.Confirm($"Trả {r.F.Name} về mặc định?", $"{r.Path} → {target}\nFile được chuyển về ổ Windows — kiểm tra ổ còn đủ chỗ.", "Trả về"))
            return;
        if (!await CloseAppsIfNeeded([r])) return;
        IsBusy = true;
        try { await MoveOne(r, target); }
        finally { IsBusy = false; }
        await RefreshAsync();
        if (!r.F.IsApp) AskRestartExplorer();
    }

    [RelayCommand] private void Open(UserFolderRow r) { if (Directory.Exists(r.Path)) ShellLauncher.Open(r.Path); }

    private static void AskRestartExplorer()
    {
        if (DialogWindow.Confirm("Khởi động lại Explorer?", "Để thanh bên trái của File Explorer và Desktop hiển thị vị trí mới ngay.", "Khởi động lại"))
            _ = Services.Tweaks.TweakService.RestartExplorerAsync();
    }
}
