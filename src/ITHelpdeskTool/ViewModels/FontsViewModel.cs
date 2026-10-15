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

public sealed partial class InstalledFontRow(InstalledFont font, Action<InstalledFontRow, bool> onChecked) : ObservableObject
{
    public InstalledFont Font { get; } = font;
    public bool CanRemove => !Font.IsSystem;
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => onChecked(this, value);

    // ---- Kết quả phân tích (điền sau, chạy nền) ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Badge), nameof(Tone))] private bool _analyzed;
    public string FamilyName { get; private set; } = font.Name;
    public bool Vietnamese { get; private set; }
    public bool Legacy { get; private set; }
    public void SetScan(FontScanResult r)
    {
        FamilyName = r.Family;
        Vietnamese = r.Vietnamese;
        Legacy = r.Legacy;
        _family = null;
        _familyMade = false;
        Analyzed = true;
        OnPropertyChanged(nameof(Vietnamese));
        OnPropertyChanged(nameof(Legacy));
        OnPropertyChanged(nameof(Family));
    }
    public string Badge => !Analyzed ? "" : Legacy ? "VNI/TCVN3" : Vietnamese ? "Tiếng Việt" : "";
    public string Tone => Legacy ? "Warn" : "Good";

    /// <summary>FontFamily đọc thẳng từ file — tạo một lần, chỉ khi dòng hiện trên màn hình.</summary>
    private FontFamily? _family;
    private bool _familyMade;
    public FontFamily? Family
    {
        get
        {
            if (_familyMade) return _family;
            _familyMade = true;
            try
            {
                var ext = Path.GetExtension(Font.File).ToLowerInvariant();
                if (ext is ".fon" or ".fnt") return null;
                var dir = Path.GetDirectoryName(Font.File)!.TrimEnd('\\') + "\\";
                _family = new FontFamily(new Uri(dir), "./" + Uri.EscapeDataString(Font.FileName) + "#" + FamilyName);
            }
            catch { _family = null; }
            return _family;
        }
    }

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
    partial void OnTabInstalledChanged(bool value) { if (value && _all is null && !_loadingInstalled) _ = LoadInstalledAsync(); }

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
    private List<InstalledFontRow>? _all;
    private bool _loadingInstalled;
    private bool _selectFirst;
    private CancellationTokenSource? _scanCts;
    /// <summary>Danh sách đang hiện (đã lọc) — thay cả danh sách một lần thay vì ẩn từng dòng, để cuộn ảo hoá mượt.</summary>
    [ObservableProperty] private IReadOnlyList<InstalledFontRow> _installedView = [];
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _installedSummary = "";
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private bool _scanned;
    [ObservableProperty] private bool _showOwnFont = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelected))] private InstalledFontRow? _selected;
    public bool HasSelected => Selected is not null;

    // Bộ lọc: nguồn font
    [ObservableProperty] private bool _scopeAll = true;
    [ObservableProperty] private bool _scopeWindows;
    [ObservableProperty] private bool _scopeAdded;
    // Bộ lọc: tiếng Việt
    [ObservableProperty] private bool _langAll = true;
    [ObservableProperty] private bool _langVn;
    [ObservableProperty] private bool _langLegacy;
    [ObservableProperty] private bool _langNone;
    // Lưu lựa chọn lọc vào biến riêng: khi bấm nút khác, WPF bật nút mới TRƯỚC rồi mới tắt nút cũ,
    // nên không được suy ra bộ lọc từ các cờ Scope*/Lang* (lúc đó có 2 cờ cùng true).
    private enum ScopeF { All, Windows, Added }
    private enum LangF { All, Vn, Legacy, None }
    private ScopeF _scope = ScopeF.All;
    private LangF _lang = LangF.All;
    private void SetScope(ScopeF f) { _scope = f; ApplyFilter(); }
    private void SetLang(LangF f) { _lang = f; ApplyFilter(); }
    partial void OnScopeAllChanged(bool value) { if (value) SetScope(ScopeF.All); }
    partial void OnScopeWindowsChanged(bool value) { if (value) SetScope(ScopeF.Windows); }
    partial void OnScopeAddedChanged(bool value) { if (value) SetScope(ScopeF.Added); }
    partial void OnLangAllChanged(bool value) { if (value) SetLang(LangF.All); }
    partial void OnLangVnChanged(bool value) { if (value) SetLang(LangF.Vn); }
    partial void OnLangLegacyChanged(bool value) { if (value) SetLang(LangF.Legacy); }
    partial void OnLangNoneChanged(bool value) { if (value) SetLang(LangF.None); }

    // Số lượng hiện trên nút lọc
    [ObservableProperty] private string _countAll = "";
    [ObservableProperty] private string _countWindows = "";
    [ObservableProperty] private string _countAdded = "";
    [ObservableProperty] private string _countVn = "";
    [ObservableProperty] private string _countLegacy = "";
    [ObservableProperty] private string _countNone = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChecked), nameof(CheckedText))] private int _checkedCount;
    public bool HasChecked => CheckedCount > 0;
    public string CheckedText => $"Gỡ {CheckedCount:N0} font đã chọn";

    // ---- Google ----
    public ObservableCollection<WebFontRow> WebFonts { get; } = [];

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadWebAsync();
        switch (ShellViewModel.StartSubPage)
        {
            case "installed": TabNew = false; TabInstalled = true; break;
            case "installed-sel": _selectFirst = true; TabNew = false; TabInstalled = true; break;  // chụp màn hình khung xem trước
            case "google": TabNew = false; TabGoogle = true; break;
            case "demo":
                // Xem trước bằng font có sẵn của Windows (dùng khi chụp màn hình)
                var win = FontService.MachineDir;
                _ = AddPathsAsync(new[] { "segoeui.ttf", "times.ttf", "consola.ttf", "georgia.ttf", "tahoma.ttf" }.Select(f => Path.Combine(win, f)).Where(File.Exists));
                break;
        }
    }

    private async Task LoadWebAsync()
    {
        var names = await Task.Run(FontService.InstalledNames);
        foreach (var f in GoogleFontCatalog.Fonts)
            WebFonts.Add(new WebFontRow(f, names.Any(n => n.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase))) { Family = new FontFamily(f.Name) });
    }

    /// <summary>Danh sách font đã cài cần đọc lại (sau khi cài thêm).</summary>
    private void InvalidateInstalled()
    {
        _scanCts?.Cancel();
        _all = null;
        InstalledView = [];
        Selected = null;
        CheckedCount = 0;
        if (TabInstalled) _ = LoadInstalledAsync();
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
        InvalidateInstalled();
    }

    // ================= Font đã cài =================
    [RelayCommand]
    private async Task LoadInstalledAsync()
    {
        if (_loadingInstalled) return;
        _loadingInstalled = true;
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        IsBusy = true;
        BusyText = "Đang đọc danh sách font…";
        Scanned = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var list = await Task.Run(() => FontService.ReadInstalled());
            CheckedCount = 0;
            Selected = null;
            _all = list.Select(f => new InstalledFontRow(f, OnRowChecked)).ToList();
            InstalledSummary = $"{list.Count:N0} font · {list.Count(f => !f.IsSystem):N0} font cài thêm · {Fmt.Size(list.Sum(f => f.Size))}";
            UpdateCounts();
            ApplyFilter();
            if (_selectFirst) { _selectFirst = false; Selected = InstalledView.FirstOrDefault(r => r.Font.Name.StartsWith("Segoe UI")) ?? InstalledView.FirstOrDefault(); }
            Log.Info($"Fonts: đọc {list.Count} font trong {sw.ElapsedMilliseconds} ms");
        }
        finally { IsBusy = false; _loadingInstalled = false; }
        await ScanAsync(_all!, cts.Token);
    }

    /// <summary>Kiểm tra dấu tiếng Việt / tên họ của mọi font — chạy nền, không khoá giao diện.</summary>
    private async Task ScanAsync(List<InstalledFontRow> rows, CancellationToken ct)
    {
        ScanText = "Đang kiểm tra dấu tiếng Việt…";
        var progress = new Progress<(int Done, int Total)>(p => ScanText = $"Đang kiểm tra dấu tiếng Việt… {p.Done:N0}/{p.Total:N0}");
        IProgress<(int, int)> rep = progress;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fonts = rows.Select(r => r.Font).ToList();
            var map = await Task.Run(() => FontScan.ScanMany(fonts, (d, t) => rep.Report((d, t)), ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(rows, _all)) return;
            foreach (var r in rows)
                if (map.TryGetValue(r.Font.File, out var res)) r.SetScan(res);
                else r.SetScan(new FontScanResult(r.Font.Name, false, FontScan.IsLegacyName(r.Font.Name)));
            Scanned = true;
            ScanText = "";
            UpdateCounts();
            if (_lang != LangF.All) ApplyFilter();
            Log.Info($"Fonts: phân tích {rows.Count} font trong {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ScanText = "Không kiểm tra được: " + ex.Message; }
    }

    private void OnRowChecked(InstalledFontRow row, bool value) => CheckedCount += value ? 1 : -1;

    private void UpdateCounts()
    {
        var all = _all ?? [];
        CountAll = $"Tất cả ({all.Count:N0})";
        CountWindows = $"Font Windows ({all.Count(r => r.Font.IsSystem):N0})";
        CountAdded = $"Font cài thêm ({all.Count(r => !r.Font.IsSystem):N0})";
        var scoped = all.Where(InScope).ToList();
        string N(Func<InstalledFontRow, bool> f) => Scanned ? $" ({scoped.Count(f):N0})" : "";
        CountVn = "Đủ dấu tiếng Việt" + N(r => r.Vietnamese);
        CountLegacy = "Việt hoá VNI/TCVN3" + N(r => r.Legacy);
        CountNone = "Không có tiếng Việt" + N(r => !r.Vietnamese && !r.Legacy);
    }

    private bool InScope(InstalledFontRow r) => _scope switch
    {
        ScopeF.Windows => r.Font.IsSystem,
        ScopeF.Added => !r.Font.IsSystem,
        _ => true,
    };

    private bool InLang(InstalledFontRow r) => !Scanned || _lang switch
    {
        LangF.Vn => r.Vietnamese,
        LangF.Legacy => r.Legacy,
        LangF.None => !r.Vietnamese && !r.Legacy,
        _ => true,
    };

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_all is null) return;
        var f = Filter.Trim();
        var view = _all.Where(r => InScope(r) && InLang(r) &&
                                   (f.Length == 0 || r.Font.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) ||
                                    r.Font.FileName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.FamilyName.Contains(f, StringComparison.CurrentCultureIgnoreCase))).ToList();
        InstalledView = view;
        if (Selected is not null && !view.Contains(Selected)) Selected = null;
        UpdateCounts();
    }

    /// <summary>visible: chọn đúng các font cài thêm đang hiện (bỏ chọn font bị lọc ẩn); none: bỏ chọn hết.</summary>
    [RelayCommand]
    private void SelectInstalled(string which)
    {
        if (_all is null) return;
        var visible = which == "none" ? [] : InstalledView.Where(r => r.CanRemove).ToHashSet();
        foreach (var r in _all) r.IsChecked = visible.Contains(r);
    }

    /// <summary>Cho test tự động: chọn bộ lọc bằng tên.</summary>
    public void SetFilterForTest(string scope, string lang)
    {
        ScopeAll = scope == "all"; ScopeWindows = scope == "windows"; ScopeAdded = scope == "added";
        LangAll = lang == "all"; LangVn = lang == "vn"; LangLegacy = lang == "legacy"; LangNone = lang == "none";
    }

    [RelayCommand]
    private async Task UninstallFontAsync(InstalledFontRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (!DialogWindow.Confirm($"Gỡ font {row.Font.Name}?", "Văn bản đang dùng font này sẽ hiển thị bằng font khác.", "Gỡ", danger: true)) return;
        await RemoveRowsAsync([row]);
    }

    [RelayCommand]
    private async Task UninstallCheckedAsync()
    {
        if (_all is null) return;
        var sel = _all.Where(r => r.IsChecked && r.CanRemove).ToList();
        if (sel.Count == 0) { DialogWindow.Info("Chưa chọn font", "Tích chọn font cài thêm muốn gỡ (font của Windows không gỡ được)."); return; }
        var names = string.Join(", ", sel.Take(8).Select(r => r.Font.Name)) + (sel.Count > 8 ? $" và {sel.Count - 8:N0} font khác" : "");
        if (!DialogWindow.Confirm($"Gỡ {sel.Count:N0} font?",
                $"{names}.\n\nVăn bản đang dùng các font này sẽ hiển thị bằng font khác. Nên “Sao lưu font cài thêm” trước nếu chưa chắc.", "Gỡ", danger: true))
            return;
        await RemoveRowsAsync(sel);
    }

    private async Task RemoveRowsAsync(List<InstalledFontRow> rows)
    {
        IsBusy = true;
        var removed = new List<InstalledFontRow>();
        var fails = new List<string>();
        var restart = false;
        var progress = new Progress<int>(i => BusyText = $"Đang gỡ font {i:N0}/{rows.Count:N0}…");
        IProgress<int> rep = progress;
        try
        {
            rep.Report(0);
            var byFont = rows.ToDictionary(r => r.Font);
            var res = await Task.Run(() => FontService.UninstallMany(rows.Select(r => r.Font).ToList(), rep.Report));
            foreach (var (font, r) in res)
            {
                if (r.Ok) { removed.Add(byFont[font]); restart |= r.NeedsRestart; }
                else fails.Add(r.Message);
            }
        }
        finally { IsBusy = false; }

        if (_all is not null)
        {
            var set = removed.ToHashSet();
            foreach (var r in removed) if (r.IsChecked) r.IsChecked = false;
            _all.RemoveAll(set.Contains);
            InstalledSummary = $"{_all.Count:N0} font · {_all.Count(f => !f.Font.IsSystem):N0} font cài thêm · {Fmt.Size(_all.Sum(f => f.Font.Size))}";
            ApplyFilter();
        }
        var msg = rows.Count == 1 && removed.Count == 1 ? $"Đã gỡ {removed[0].Font.Name}." : $"Đã gỡ {removed.Count:N0}/{rows.Count:N0} font.";
        if (restart) msg += " Một số file đang được ứng dụng dùng — sẽ xoá hẳn sau khi khởi động lại.";
        AddLog(msg, fails.Count == 0);
        foreach (var f in fails.Take(5)) AddLog(f, false);
        if (fails.Count > 0) DialogWindow.Info("Có font không gỡ được", string.Join("\n", fails.Take(10)) + (fails.Count > 10 ? $"\n… và {fails.Count - 10} font khác" : ""), error: true);
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
        InvalidateInstalled();
    }
}
