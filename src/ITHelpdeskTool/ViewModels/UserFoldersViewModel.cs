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
    /// <summary>Dữ liệu ứng dụng (Zalo, CapCut, Canva…) — chuyển bằng junction.</summary>
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
        foreach (var b in UserFolderService.SuggestedBases()) Bases.Add(b);
        BasePath = Bases.FirstOrDefault() ?? "";
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;
        var d = new DriveInfo(sys);
        SystemFree = $"Ổ Windows {sys.TrimEnd('\\')} còn trống {Fmt.Size(d.AvailableFreeSpace)} / {Fmt.Size(d.TotalSize)}";
        foreach (var r in Folders)
        {
            r.Path = UserFolderService.GetPath(r.F);
            r.IsDefault = UserFolderService.IsDefault(r.F);
            r.OneDrive = UserFolderService.InOneDrive(r.Path);
            r.OnSystemDrive = r.Path.StartsWith(sys, StringComparison.OrdinalIgnoreCase);
            r.IsChecked = r.OnSystemDrive && !r.OneDrive && r.F.Key is "documents" or "downloads" or "desktop" or "pictures" or "videos";
        }
        foreach (var r in AppFolders)
        {
            r.Exists = UserFolderService.AppDataExists(r.F);
            r.Path = UserFolderService.GetPath(r.F);
            r.IsDefault = UserFolderService.IsDefault(r.F);
            r.OnSystemDrive = r.Path.StartsWith(sys, StringComparison.OrdinalIgnoreCase);
            if (!r.Exists) { r.IsChecked = false; r.SizeText = "Chưa có dữ liệu (chưa cài hoặc chưa mở lần nào)"; }
        }
        await Task.WhenAll(AllRows.Where(r => r.Exists).Select(async r =>
        {
            var (b, n) = await Task.Run(() => UserFolderService.Measure(r.Path));
            r.SizeText = $"{Fmt.Size(b)} · {n:N0} file";
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
