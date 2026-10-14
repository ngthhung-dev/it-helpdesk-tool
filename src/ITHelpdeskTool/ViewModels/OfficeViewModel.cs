using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Office;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class Pick<T>(T item, bool on) : ObservableObject
{
    public T Item { get; } = item;
    [ObservableProperty] private bool _isChecked = on;
}

public sealed partial class OfficeViewModel : PageViewModel
{
    public override string Title => "Office";
    private bool _loaded;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    // ---- Office trên máy ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasC2R), nameof(NoOffice))] private OfficeC2R? _c2r;
    public bool HasC2R => C2r is not null;
    public bool NoOffice => C2r is null && MsiOffice.Count == 0;
    public ObservableCollection<string> MsiOffice { get; } = [];
    public IEnumerable<OfficeChannel> SwitchChannels => OfficeCatalog.SwitchableChannels;
    [ObservableProperty] private OfficeChannel? _targetChannel;

    // ---- Bản quyền ----
    public ObservableCollection<OfficeLicense> Licenses { get; } = [];
    [ObservableProperty] private string _licenseSummary = "Đang đọc…";
    [ObservableProperty] private string _productKey = "";

    // ---- Cài mới ----
    public IReadOnlyList<OfficeProduct> Products => OfficeCatalog.Products;
    [ObservableProperty] private OfficeProduct _product = OfficeCatalog.Products[0];
    public List<Pick<OfficeApp>> Apps { get; } = OfficeCatalog.Apps.Select(a => new Pick<OfficeApp>(a, a.DefaultOn)).ToList();
    public List<Pick<OfficeLanguage>> Languages { get; } = OfficeCatalog.Languages.Select(l => new Pick<OfficeLanguage>(l, l.Id == "vi-vn")).ToList();
    public ObservableCollection<OfficeChannel> InstallChannels { get; } = [];
    [ObservableProperty] private OfficeChannel? _installChannel;
    [ObservableProperty] private bool _x64 = true;
    [ObservableProperty] private bool _x86;
    [ObservableProperty] private bool _visio;
    [ObservableProperty] private bool _project;
    [ObservableProperty] private string _installKey = "";
    [ObservableProperty] private bool _removeMsi = true;
    [ObservableProperty] private bool _showUi = true;
    [ObservableProperty] private bool _autoUpdate = true;
    [ObservableProperty] private string _xml = "";
    [ObservableProperty] private bool _showXml;
    [ObservableProperty] private string _sourceFolder = "";
    public bool CanVisio => Product.VisioId.Length > 0;
    public bool CanProject => Product.ProjectId.Length > 0;

    public OfficeViewModel()
    {
        foreach (var a in Apps) a.PropertyChanged += (_, _) => RebuildXml();
        foreach (var l in Languages) l.PropertyChanged += (_, _) => RebuildXml();
        OnProductChanged(Product);
    }

    partial void OnProductChanged(OfficeProduct value)
    {
        InstallChannels.Clear();
        if (value.Volume) InstallChannels.Add(OfficeCatalog.Channels.First(c => c.Id == value.Channel));
        else foreach (var c in OfficeCatalog.SwitchableChannels) InstallChannels.Add(c);
        InstallChannel = InstallChannels.FirstOrDefault(c => c.Id == value.Channel) ?? InstallChannels.FirstOrDefault();
        if (!CanVisio) Visio = false;
        if (!CanProject) Project = false;
        OnPropertyChanged(nameof(CanVisio));
        OnPropertyChanged(nameof(CanProject));
        RebuildXml();
    }

    partial void OnInstallChannelChanged(OfficeChannel? value) => RebuildXml();
    partial void OnX64Changed(bool value) => RebuildXml();
    partial void OnVisioChanged(bool value) => RebuildXml();
    partial void OnProjectChanged(bool value) => RebuildXml();
    partial void OnInstallKeyChanged(string value) => RebuildXml();
    partial void OnRemoveMsiChanged(bool value) => RebuildXml();
    partial void OnShowUiChanged(bool value) => RebuildXml();
    partial void OnAutoUpdateChanged(bool value) => RebuildXml();
    partial void OnSourceFolderChanged(string value) => RebuildXml();

    private OfficeConfig Config() => new()
    {
        Product = Product,
        Channel = InstallChannel?.Id ?? "",
        X64 = X64,
        Languages = Languages.Where(l => l.IsChecked).Select(l => l.Item.Id).ToList(),
        ExcludeApps = Apps.Where(a => !a.IsChecked).Select(a => a.Item.Id).ToList(),
        Visio = Visio,
        Project = Project,
        ProductKey = InstallKey,
        RemoveMsi = RemoveMsi,
        ShowUi = ShowUi,
        AutoUpdate = AutoUpdate,
        SourcePath = SourceFolder,
    };

    private void RebuildXml() => Xml = OfficeCatalog.BuildXml(Config());

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Office: {text}"); else Log.Warn($"Office: {text}");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var (c2r, msi) = await Task.Run(() => (OfficeService.ReadC2R(), OfficeService.ReadMsiOffice()));
        C2r = c2r;
        MsiOffice.Clear();
        foreach (var m in msi) MsiOffice.Add($"{m.Name} {m.Version}".Trim());
        OnPropertyChanged(nameof(NoOffice));
        TargetChannel = OfficeCatalog.SwitchableChannels.FirstOrDefault(c => c.Id == c2r?.ChannelId) ?? OfficeCatalog.SwitchableChannels.First();
        await RefreshLicensesAsync();
    }

    private async Task RefreshLicensesAsync()
    {
        try
        {
            var list = await OfficeService.LicensesAsync();
            Licenses.Clear();
            foreach (var l in list) Licenses.Add(l);
            var sub = OfficeService.HasSubscriptionLicense();
            LicenseSummary = OfficeService.OsppPath() is null ? "Chưa cài Office."
                : list.Count == 0 ? (sub ? "Dùng giấy phép Microsoft 365 qua tài khoản đăng nhập (không có key trên máy)." : "Chưa có key nào trên máy.")
                : $"{list.Count(l => l.Licensed)}/{list.Count} giấy phép đã kích hoạt" + (sub ? " · có thêm giấy phép Microsoft 365 qua tài khoản" : "");
        }
        catch (Exception ex) { LicenseSummary = $"Không đọc được giấy phép: {ex.Message}"; }
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
        catch (Exception ex) { AddLog(ex.Message, false); DialogWindow.Info("Lỗi", ex.Message, error: true); }
        finally { IsBusy = false; }
        if (refresh) await RefreshAsync();
    }

    // ================= Thao tác với Office đang cài =================
    [RelayCommand] private Task UpdateNowAsync() => Run("Đang mở trình cập nhật Office…", OfficeService.UpdateNowAsync, refresh: false);

    [RelayCommand]
    private Task RepairAsync(string mode)
    {
        var quick = mode == "quick";
        if (!DialogWindow.Confirm(quick ? "Sửa nhanh Office?" : "Sửa trực tuyến Office?",
                quick ? "Sửa file lỗi ngay trên máy, không cần mạng, vài phút. Các ứng dụng Office đang mở sẽ bị đóng."
                      : "Gỡ và tải lại toàn bộ Office (vài GB) — dùng khi sửa nhanh không được. Các ứng dụng Office đang mở sẽ bị đóng.", "Sửa"))
            return Task.CompletedTask;
        return Run("Đang chạy trình sửa Office…", () => OfficeService.RepairAsync(quick), refresh: false);
    }

    [RelayCommand]
    private Task ChangeChannelAsync()
    {
        if (TargetChannel is not { } ch) return Task.CompletedTask;
        if (!DialogWindow.Confirm($"Chuyển sang kênh {ch.Name}?", "Office sẽ tải bản cập nhật của kênh mới. Chỉ áp dụng cho bản Microsoft 365 / bán lẻ.", "Chuyển"))
            return Task.CompletedTask;
        return Run("Đang đổi kênh cập nhật…", () => OfficeService.ChangeChannelAsync(ch));
    }

    [RelayCommand]
    private Task ResetSignInAsync()
    {
        if (!DialogWindow.Confirm("Xoá thông tin đăng nhập Office?",
                "Dùng khi Office báo “Unlicensed Product”, đăng nhập lỗi, hoặc muốn đổi sang tài khoản khác. Các ứng dụng Office sẽ được yêu cầu đóng.", "Xoá"))
            return Task.CompletedTask;
        return Run("Đang xoá thông tin đăng nhập…", OfficeService.ResetSignInAsync);
    }

    [RelayCommand]
    private Task RemoveAllAsync()
    {
        if (NoOffice) { DialogWindow.Info("Không có Office", "Máy này không có bản Office nào để gỡ."); return Task.CompletedTask; }
        if (!DialogWindow.Confirm("Gỡ toàn bộ Office?",
                "Gỡ mọi sản phẩm Office Click-to-Run trên máy" + (RemoveMsi ? " và cả bản MSI cũ (2010/2013/2016)" : "") +
                ". Tài liệu của bạn không bị ảnh hưởng.", "Gỡ", danger: true))
            return Task.CompletedTask;
        return Run("Đang gỡ Office — xem tiến độ trên cửa sổ của Microsoft…", () => OfficeService.RunOdtAsync(OfficeService.RemoveAllXml(RemoveMsi), "configure", s => BusyText = s));
    }

    // ================= Bản quyền =================
    [RelayCommand]
    private Task ActivateKeyAsync()
    {
        if (!OfficeService.IsKeyFormat(ProductKey)) { DialogWindow.Info("Key chưa đúng", "Key có dạng XXXXX-XXXXX-XXXXX-XXXXX-XXXXX."); return Task.CompletedTask; }
        var key = ProductKey;
        return Run("Đang nhập key và kích hoạt…", async () => { var r = await OfficeService.ActivateWithKeyAsync(key); if (r.Ok) ProductKey = ""; return r; });
    }

    [RelayCommand] private Task ActivateAsync() => Run("Đang kích hoạt…", OfficeService.ActivateAsync);

    [RelayCommand]
    private Task RemoveKeyAsync(OfficeLicense l)
    {
        if (l.Last5.Length == 0) return Task.CompletedTask;
        if (!DialogWindow.Confirm($"Gỡ key …{l.Last5}?", $"{l.Name}\nOffice sẽ chưa kích hoạt cho tới khi nhập key khác / đăng nhập tài khoản.", "Gỡ", danger: true))
            return Task.CompletedTask;
        return Run("Đang gỡ key…", () => OfficeService.RemoveKeyAsync(l.Last5));
    }

    // ================= Cài mới =================
    private bool CheckConfig()
    {
        if (!Languages.Any(l => l.IsChecked)) { DialogWindow.Info("Chưa chọn ngôn ngữ", "Chọn ít nhất một ngôn ngữ."); return false; }
        if (!Apps.Any(a => a.IsChecked)) { DialogWindow.Info("Chưa chọn ứng dụng", "Chọn ít nhất một ứng dụng."); return false; }
        if (InstallKey.Trim().Length > 0 && !OfficeService.IsKeyFormat(InstallKey)) { DialogWindow.Info("Key chưa đúng", "Key có dạng XXXXX-XXXXX-XXXXX-XXXXX-XXXXX, hoặc để trống."); return false; }
        return true;
    }

    [RelayCommand]
    private Task InstallAsync()
    {
        if (!CheckConfig()) return Task.CompletedTask;
        var apps = string.Join(", ", Apps.Where(a => a.IsChecked).Select(a => a.Item.Name));
        if (!DialogWindow.Confirm($"Cài {Product.Name}?",
                $"{(X64 ? "64-bit" : "32-bit")} · {string.Join(", ", Languages.Where(l => l.IsChecked).Select(l => l.Item.Name))}\n{apps}" +
                (Visio ? "\n+ Visio" : "") + (Project ? "\n+ Project" : "") +
                (SourceFolder.Length > 0 ? $"\n\nCài từ bộ cài offline: {SourceFolder}" : "\n\nTải từ máy chủ Microsoft (khoảng 2–4 GB).") +
                (RemoveMsi ? "\nBản Office MSI cũ (nếu có) sẽ được gỡ trước." : ""), "Cài đặt"))
            return Task.CompletedTask;
        var xml = Xml;
        return Run("Đang cài Office…", () => OfficeService.RunOdtAsync(xml, "configure", s => BusyText = s));
    }

    [RelayCommand]
    private Task DownloadOfflineAsync()
    {
        if (!CheckConfig()) return Task.CompletedTask;
        var dlg = new OpenFolderDialog { Title = "Chọn nơi lưu bộ cài Office offline (cần 3–5 GB trống)" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        var folder = Path.Combine(dlg.FolderName, $"Office-{Product.Id}-{(X64 ? "x64" : "x86")}");
        Directory.CreateDirectory(folder);
        var xml = OfficeCatalog.BuildDownloadXml(Config(), folder);
        return Run("Đang tải bộ cài Office…", async () =>
        {
            var r = await OfficeService.RunOdtAsync(xml, "download", s => BusyText = s, workDir: folder);
            if (r.Ok)
            {
                // Chép kèm setup.exe + cấu hình cài để mang sang máy khác chạy luôn
                File.Copy(await OfficeService.EnsureOdtAsync(), Path.Combine(folder, "setup.exe"), true);
                var c = Config();
                c.SourcePath = "";
                var install = System.Xml.Linq.XDocument.Parse(OfficeCatalog.BuildXml(c));
                install.Root!.Element("Add")!.SetAttributeValue("SourcePath", null);
                await File.WriteAllTextAsync(Path.Combine(folder, "cai-dat.xml"), install.ToString());
                await File.WriteAllTextAsync(Path.Combine(folder, "CaiDat.cmd"), "@echo off\r\ncd /d \"%~dp0\"\r\nsetup.exe /configure cai-dat.xml\r\n");
                ShellLauncher.Open(folder);
                return OpResult.Success($"Đã tải bộ cài offline vào {folder}. Mang thư mục này sang máy khác, chạy CaiDat.cmd (quyền admin).");
            }
            return r;
        }, refresh: false);
    }

    [RelayCommand]
    private void PickSource()
    {
        var dlg = new OpenFolderDialog { Title = "Chọn thư mục bộ cài Office offline (có thư mục con Office\\Data)" };
        if (dlg.ShowDialog() != true) return;
        if (!Directory.Exists(Path.Combine(dlg.FolderName, "Office", "Data")))
        {
            DialogWindow.Info("Không phải bộ cài offline", "Thư mục này không có Office\\Data — chọn thư mục đã tải bằng “Tải bộ cài offline”.", error: true);
            return;
        }
        SourceFolder = dlg.FolderName;
    }

    [RelayCommand] private void ClearSource() => SourceFolder = "";

    [RelayCommand]
    private void SaveXml()
    {
        var dlg = new SaveFileDialog { Filter = "Cấu hình ODT (*.xml)|*.xml", FileName = $"office-{Product.Id}.xml" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, Xml);
        AddLog($"Đã lưu cấu hình vào {dlg.FileName}. Chạy: setup.exe /configure \"{Path.GetFileName(dlg.FileName)}\"", true);
    }
}
