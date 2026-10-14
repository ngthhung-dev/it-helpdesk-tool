using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Fonts;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class NewFontRow(FontFileInfo info, bool installed) : ObservableObject
{
    public FontFileInfo Info { get; } = info;
    public FontFamily? Family { get; } = info.Preview();
    [ObservableProperty] private bool _isChecked = !installed;
    [ObservableProperty] private bool _isInstalled = installed;
    [ObservableProperty] private string _result = "";
    public string Badge => Info.Legacy ? "Font mã cũ (VNI/TCVN3)" : Info.Vietnamese ? "Đủ dấu tiếng Việt" : "Thiếu dấu tiếng Việt";
    public string Tone => Info.Legacy ? "Warn" : Info.Vietnamese ? "Good" : "Bad";
}

public sealed partial class InstalledFontRow(InstalledFont font) : ObservableObject
{
    public InstalledFont Font { get; } = font;
    [ObservableProperty] private bool _isVisible = true;
    private FontFileInfo? _info;
    private bool _read;
    public FontFileInfo? Info
    {
        get
        {
            if (_read) return _info;
            _read = true;
            try { if (File.Exists(Font.File)) _info = FontService.Read(Font.File); } catch { }
            return _info;
        }
    }
    public FontFamily? Family => Info?.Preview();
    public string Detail => string.Join(" · ", new[]
    {
        Font.FileName, Font.Size > 0 ? Fmt.Size(Font.Size) : "", Font.Scope == FontScope.User ? "chỉ tài khoản này" : "", Font.IsSystem ? "font Windows" : "",
    }.Where(s => s.Length > 0));
}

public sealed partial class WebFontRow(WebFont font, bool installed) : ObservableObject
{
    public WebFont Font { get; } = font;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isInstalled = installed;
    [ObservableProperty] private FontFamily? _family;
}

public sealed partial class FontsViewModel : PageViewModel
{
    public override string Title => "Cài font";
    private bool _loaded;

    [ObservableProperty] private bool _tabNew = true;
    [ObservableProperty] private bool _tabInstalled;
    [ObservableProperty] private bool _tabGoogle;
    partial void OnTabInstalledChanged(bool value) { if (value && Installed.Count == 0) _ = LoadInstalledAsync(); }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _sample = "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập, Tự do, Hạnh phúc. 0123456789";
    [ObservableProperty] private bool _allUsers = true;
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    // ---- Cài mới ----
    public ObservableCollection<NewFontRow> NewFonts { get; } = [];
    [ObservableProperty] private string _newSummary = "";

    // ---- Đã cài ----
    public ObservableCollection<InstalledFontRow> Installed { get; } = [];
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyAdded;
    [ObservableProperty] private string _installedSummary = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelected))] private InstalledFontRow? _selected;
    public bool HasSelected => Selected is not null;

    // ---- Google ----
    public ObservableCollection<WebFontRow> WebFonts { get; } = [];

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        var names = FontService.InstalledNames();
        foreach (var f in GoogleFontCatalog.Fonts)
            WebFonts.Add(new WebFontRow(f, names.Any(n => n.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))) { Family = new FontFamily(f.Name) });
        switch (ShellViewModel.StartSubPage)
        {
            case "installed": TabNew = false; TabInstalled = true; break;
            case "google": TabNew = false; TabGoogle = true; break;
            case "demo":
                // Xem trước bằng font có sẵn của Windows (dùng khi chụp màn hình)
                var win = FontService.MachineDir;
                _ = AddPathsAsync(new[] { "segoeui.ttf", "times.ttf", "consola.ttf", "georgia.ttf", "tahoma.ttf" }.Select(f => Path.Combine(win, f)).Where(File.Exists));
                break;
        }
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Fonts: {text}"); else Log.Warn($"Fonts: {text}");
    }

    // ================= Cài font mới =================
    [RelayCommand]
    private async Task PickFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Chọn file font",
            Multiselect = true,
            Filter = "Font và file nén (*.ttf;*.otf;*.ttc;*.otc;*.fon;*.zip)|*.ttf;*.otf;*.ttc;*.otc;*.fon;*.fnt;*.zip|Tất cả|*.*",
        };
        if (dlg.ShowDialog() == true) await AddPathsAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục chứa font (đọc cả thư mục con và file .zip)" };
        if (dlg.ShowDialog() == true) await AddPathsAsync([dlg.FolderName]);
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        BusyText = "Đang đọc font…";
        try
        {
            var list = paths.ToList();
            var (infos, names, files) = await Task.Run(() =>
            {
                var found = FontService.Collect(list);
                var inf = new List<FontFileInfo>();
                foreach (var f in found) try { inf.Add(FontService.Read(f)); } catch { }
                return (inf, FontService.InstalledNames(), FontService.InstalledFileNames());
            });
            foreach (var i in infos)
            {
                if (NewFonts.Any(n => n.Info.Path.Equals(i.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var installed = names.Contains(FontService.StripType(i.RegName)) ||
                                (files.Contains(i.FileName) && Path.GetDirectoryName(i.Path)!.Equals(FontService.MachineDir, StringComparison.OrdinalIgnoreCase));
                NewFonts.Add(new NewFontRow(i, installed));
            }
            UpdateNewSummary();
            if (infos.Count == 0) DialogWindow.Info("Không thấy font", "Không có file .ttf / .otf / .ttc / .fon nào trong lựa chọn.");
        }
        finally { IsBusy = false; }
    }

    private void UpdateNewSummary()
    {
        var n = NewFonts.Count;
        NewSummary = n == 0 ? "" :
            $"{n} font · {NewFonts.Count(f => f.Info.Vietnamese)} đủ dấu tiếng Việt · {NewFonts.Count(f => f.IsInstalled)} đã cài" +
            (NewFonts.Any(f => f.Info.Legacy) ? " · có font mã cũ VNI/TCVN3 (cần gõ đúng bảng mã trong UniKey)" : "");
    }

    [RelayCommand]
    private void SelectNew(string which)
    {
        foreach (var f in NewFonts)
            f.IsChecked = which switch { "all" => true, "none" => false, "vn" => f.Info.Vietnamese || f.Info.Legacy, _ => !f.IsInstalled };
    }

    [RelayCommand]
    private void ClearNew() { NewFonts.Clear(); UpdateNewSummary(); }

    [RelayCommand]
    private async Task InstallNewAsync()
    {
        var sel = NewFonts.Where(f => f.IsChecked).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn font", "Tích chọn font muốn cài."); return; }
        IsBusy = true;
        var ok = 0;
        var all = AllUsers;
        try
        {
            foreach (var f in sel)
            {
                BusyText = $"Đang cài {f.Info.Display} ({ok + 1}/{sel.Count})…";
                var r = await Task.Run(() => FontService.Install(f.Info, all, broadcast: false));
                f.Result = r.Message;
                if (r.Ok) { ok++; f.IsInstalled = true; f.IsChecked = false; }
                else AddLog(r.Message, false);
            }
            await Task.Run(FontService.Broadcast);
        }
        finally { IsBusy = false; }
        AddLog($"Đã cài {ok}/{sel.Count} font" + (all ? " cho mọi tài khoản." : " cho tài khoản này."), ok == sel.Count);
        UpdateNewSummary();
        Installed.Clear();
    }

    // ================= Font đã cài =================
    [RelayCommand]
    private async Task LoadInstalledAsync()
    {
        IsBusy = true;
        BusyText = "Đang đọc danh sách font…";
        try
        {
            var list = await Task.Run(FontService.ReadInstalled);
            Installed.Clear();
            foreach (var f in list) Installed.Add(new InstalledFontRow(f));
            ApplyFilter();
            InstalledSummary = $"{list.Count} font · {list.Count(f => !f.IsSystem)} font cài thêm · {Fmt.Size(list.Sum(f => f.Size))}";
        }
        finally { IsBusy = false; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyAddedChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        foreach (var r in Installed)
            r.IsVisible = (!OnlyAdded || !r.Font.IsSystem) &&
                          (f.Length == 0 || r.Font.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || r.Font.FileName.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private async Task UninstallFontAsync(InstalledFontRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (!DialogWindow.Confirm($"Gỡ font {row.Font.Name}?", "Văn bản đang dùng font này sẽ hiển thị bằng font khác.", "Gỡ", danger: true)) return;
        var r = await Task.Run(() => FontService.Uninstall(row.Font));
        AddLog(r.Message, r.Ok);
        if (r.Ok) { Installed.Remove(row); Selected = null; }
        else DialogWindow.Info("Không gỡ được", r.Message, error: true);
    }

    [RelayCommand]
    private void OpenFontsFolder() => ShellLauncher.Open(FontService.MachineDir);

    [RelayCommand]
    private async Task BackupAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn nơi lưu bản sao font cài thêm" };
        if (dlg.ShowDialog() != true) return;
        var dest = Path.Combine(dlg.FolderName, $"Fonts-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}");
        IsBusy = true;
        BusyText = "Đang sao lưu font…";
        try
        {
            var r = await Task.Run(() => FontService.Backup(dest));
            AddLog(r.Message, r.Ok);
            if (r.Ok) ShellLauncher.Open(dest);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResetCacheAsync()
    {
        if (!DialogWindow.Confirm("Sửa lỗi hiển thị font?",
                "Xoá bộ đệm font của Windows — dùng khi chữ hiển thị sai, ra ô vuông, hoặc font mới cài không hiện trong Word. Cần khởi động lại máy sau đó.", "Sửa"))
            return;
        IsBusy = true;
        BusyText = "Đang xoá bộ đệm font…";
        try
        {
            var r = await FontService.ResetCacheAsync();
            AddLog(r.Message, r.Ok);
            DialogWindow.Info("Xong", r.Message);
        }
        finally { IsBusy = false; }
    }

    // ================= Font Google =================
    [RelayCommand]
    private async Task InstallWebAsync()
    {
        var sel = WebFonts.Where(f => f.IsChecked).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn font", "Tích chọn font muốn tải."); return; }
        IsBusy = true;
        var all = AllUsers;
        var ok = 0;
        try
        {
            foreach (var w in sel)
            {
                try
                {
                    var files = await GoogleFontCatalog.DownloadAsync(w.Font, s => BusyText = s);
                    BusyText = $"Đang cài {w.Font.Name}…";
                    var results = await Task.Run(() => files.Select(f => FontService.Install(FontService.Read(f), all, broadcast: false)).ToList());
                    if (results.All(r => r.Ok)) { ok++; w.IsInstalled = true; w.IsChecked = false; w.Family = new FontFamily(w.Font.Name); }
                    else AddLog(results.First(r => !r.Ok).Message, false);
                }
                catch (Exception ex) { AddLog($"{w.Font.Name}: {ex.Message}", false); }
            }
            await Task.Run(FontService.Broadcast);
        }
        finally { IsBusy = false; }
        AddLog($"Đã tải và cài {ok}/{sel.Count} font Google.", ok == sel.Count);
        Installed.Clear();
    }
}
