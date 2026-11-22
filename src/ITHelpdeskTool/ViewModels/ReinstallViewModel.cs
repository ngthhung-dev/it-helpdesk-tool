using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Windows;
using ITHelpdeskTool.Views;
using Microsoft.Win32;
using RI = ITHelpdeskTool.Reinstall;

namespace ITHelpdeskTool.ViewModels;

/// <summary>Một dòng chọn (sao lưu / phần mềm / tinh chỉnh).</summary>
public sealed partial class Choice : ObservableObject
{
    public Choice(object item, string title, bool on, string detail = "", string size = "", bool danger = false)
    {
        Item = item;
        Title = title;
        _isChecked = on;
        Detail = detail;
        Size = size;
        Danger = danger;
    }

    public object Item { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Size { get; }
    public bool Danger { get; }
    [ObservableProperty] private bool _isChecked;
}

/// <summary>Kết quả một mục kiểm tra; mục cần xác nhận riêng có ô tick.</summary>
public sealed partial class CheckRow : ObservableObject
{
    public CheckRow(RI.Check c, bool confirmed)
    {
        Level = c.Level;
        Title = c.Title;
        Detail = c.Detail;
        ConfirmKey = c.ConfirmKey;
        _confirmed = confirmed;
    }

    public RI.CheckLevel Level { get; }
    public string Kind => Level.ToString();
    public string Title { get; }
    public string Detail { get; }
    public string ConfirmKey { get; }
    public bool NeedsConfirm => ConfirmKey.Length > 0;
    [ObservableProperty] private bool _confirmed;
}

/// <summary>Ổ đệm có thể chọn: tự động (máy một ổ) hoặc một phân vùng khác.</summary>
public sealed record StagingOption(string Value, string Display, bool Auto);

/// <summary>
/// Cài lại sạch: xoá ổ Windows đang chạy rồi cài mới từ ISO (không cần USB).
/// Dùng lõi ITHelpdeskTool.Reinstall (cùng luồng với ithreinstall đã thử trên máy ảo).
/// </summary>
public sealed partial class ReinstallViewModel : PageViewModel
{
    public override string Title => "Cài lại sạch";
    private bool _loaded;
    private DispatcherTimer? _timer;
    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _log = new();
    private StreamWriter? _logFile;
    private string _armedStaging = "";

    public static string Target => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\').ToUpperInvariant();

    // ---- Bộ cài ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImage))] private string _imagePath = "";
    public bool HasImage => ImagePath.Length > 0;
    public ObservableCollection<RI.ImageEdition> Editions { get; } = [];
    [ObservableProperty] private RI.ImageEdition? _edition;

    // ---- Ổ đệm ----
    public ObservableCollection<StagingOption> StagingOptions { get; } = [];
    [ObservableProperty] private StagingOption? _staging;

    // ---- Sao lưu ----
    public ObservableCollection<Choice> BackupItems { get; } = [];
    [ObservableProperty] private string _backupTotal = "";
    [ObservableProperty] private bool _restoreAfter = true;
    [ObservableProperty] private bool _ignoreBackupErrors;

    // ---- Windows mới ----
    [ObservableProperty] private string _accountName = "Admin";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _computerName = "";
    [ObservableProperty] private bool _microsoftAccount;
    [ObservableProperty] private bool _preventDeviceEncryption;
    [ObservableProperty] private bool _exportDrivers = true;
    public ObservableCollection<Choice> Software { get; } = [];
    public ObservableCollection<Choice> Tweaks { get; } = [];

    // ---- Kiểm tra ----
    public ObservableCollection<CheckRow> Checks { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))] private bool _checked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart))] private bool _blocked = true;
    [ObservableProperty] private string _checkText = "Chọn bộ cài rồi bấm “Kiểm tra máy”.";
    [ObservableProperty] private string _encryptionText = "";
    [ObservableProperty] private bool _targetEncrypted;
    [ObservableProperty] private bool _decrypting;

    // ---- Chạy ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle)), NotifyPropertyChangedFor(nameof(CanStart))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    public bool CanStart => IsIdle && Checked && !Blocked;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private bool _armed;

    public string TargetText => $"Ổ sẽ bị XOÁ: {Target} (Windows đang chạy" + (ActiveUser.Name.Length > 0 ? $", tài khoản {ActiveUser.Name}" : "") + ")";

    public override void OnNavigatedTo()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
        if (_loaded) return;
        _loaded = true;
        ComputerName = Environment.MachineName;
        foreach (var s in RI.Catalog.Software)
            Software.Add(new Choice(s, s.Name, s.Default, s.Kind.ToString()));
        foreach (var t in RI.Catalog.Tweaks)
            Tweaks.Add(new Choice(t, t.Title, t.Default && !t.Danger, t.Warning, danger: t.Danger));
        LoadStaging();
        _ = ScanBackupAsync();
        _ = RefreshEncryptionAsync();
        _armedStaging = FindArmed();
        Armed = _armedStaging.Length > 0;
        // --page reinstall/download: mở sẵn phần tải bộ cài (chụp màn hình kiểm thử)
        if (string.Equals(ShellViewModel.StartSubPage, "download", StringComparison.OrdinalIgnoreCase)) _ = OpenDownloadAsync();
    }

    public override void OnNavigatedFrom() => _timer?.Stop();

    private async void Tick(object? s, EventArgs e)
    {
        try { if (Decrypting) await RefreshEncryptionAsync(); }
        catch (Exception ex) { Log.Warn($"Reinstall: {ex.Message}"); }   // async void: không để lỗi làm sập app
    }

    private void AddLog(string m)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {m}";
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
            try { _logFile?.WriteLine(line); _logFile?.Flush(); } catch { }
        }
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            lock (_log) LogText = _log.ToString();
            BusyText = m.Length > 120 ? m[..120] + "…" : m;
        });
    }

    private void OpenLog(string staging)
    {
        lock (_log)
        {
            try
            {
                _logFile?.Dispose();
                var dir = Path.Combine(staging, "logs");
                Directory.CreateDirectory(dir);
                _logFile = new StreamWriter(Path.Combine(dir, "prepare.log"), append: true, new UTF8Encoding(false));
                _logFile.Write(_log.ToString());
                _logFile.Flush();
            }
            catch { _logFile = null; }
        }
    }

    // ================= Bộ cài =================
    [RelayCommand]
    private async Task PickImageAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Bộ cài Windows (*.iso;install.wim;install.esd)|*.iso;*.wim;*.esd",
            InitialDirectory = Directory.Exists(ActiveUser.Downloads) ? ActiveUser.Downloads : "",
        };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try { await LoadImageAsync(dlg.FileName); }
        finally { IsBusy = false; }
    }

    private async Task LoadImageAsync(string path)
    {
        BusyText = "Đang đọc danh sách bản Windows trong bộ cài…";
        try
        {
            var list = await Task.Run(() => RI.Workflow.ReadEditions(new RI.ProcessRunner(), path));
            if (list.Count == 0) throw new InvalidOperationException("Không thấy bản Windows nào trong file này.");
            ImagePath = path;
            Editions.Clear();
            foreach (var e in list) Editions.Add(e);
            // Mặc định chọn bản cùng loại với Windows đang dùng (Home/Pro…) để giữ bản quyền số
            var cur = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", "") as string ?? "";
            Edition = list.FirstOrDefault(e => e.Edition.Equals(cur, StringComparison.OrdinalIgnoreCase)) ?? list[0];
            Invalidate();
        }
        catch (Exception ex) { DialogWindow.Info("Không đọc được bộ cài", ex.Message, error: true); }
    }

    // ================= Tải bộ cài gốc từ Microsoft =================
    public IReadOnlyList<string> DlProducts { get; } = [RI.EsdCatalog.Windows11, RI.EsdCatalog.Windows10];
    // Máy Windows 7 / 8 (bản nhẹ) thường không đủ điều kiện Windows 11 (TPM 2.0, CPU đời mới) → mặc định Windows 10
    [ObservableProperty] private string _dlProduct = RI.Platform.IsLegacy ? RI.EsdCatalog.Windows10 : RI.EsdCatalog.Windows11;
    [ObservableProperty] private bool _showDownload;
    public ObservableCollection<RI.EsdFile> DlFiles { get; } = [];
    [ObservableProperty] private RI.EsdFile? _dlFile;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DlIdle))] private bool _downloading;
    public bool DlIdle => !Downloading;
    [ObservableProperty] private double _dlProgress;
    [ObservableProperty] private string _dlText = "";
    private CancellationTokenSource? _dlCts;

    partial void OnDlProductChanged(string value) { if (ShowDownload) _ = LoadCatalogAsync(); }

    [RelayCommand]
    private async Task OpenDownloadAsync()
    {
        ShowDownload = !ShowDownload;
        if (ShowDownload && DlFiles.Count == 0) await LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync()
    {
        DlText = "Đang đọc danh mục bộ cài của Microsoft…";
        DlFiles.Clear();
        try
        {
            var product = DlProduct;
            var arch = RI.EsdCatalog.MachineArchitecture();
            var files = await RI.EsdCatalog.LoadAsync(new RI.ProcessRunner(), product, Path.Combine(Path.GetTempPath(), "ITH-catalog"));
            foreach (var f in files.Where(f => f.Consumer && f.Architecture.Equals(arch, StringComparison.OrdinalIgnoreCase))) DlFiles.Add(f);
            var ui = System.Globalization.CultureInfo.InstalledUICulture.Name.ToLowerInvariant();
            DlFile = DlFiles.FirstOrDefault(f => f.LanguageCode == ui) ?? DlFiles.FirstOrDefault(f => f.LanguageCode == "en-us") ?? DlFiles.FirstOrDefault();
            DlText = DlFiles.Count == 0 ? "Danh mục không có bản phù hợp với máy này."
                : $"{product} bản {DlFile?.Build} ({arch}) · Microsoft không phát hành bản tiếng Việt riêng — cài xong thêm gói tiếng Việt trong Settings › Language.";
        }
        catch (Exception ex) { DlText = "Không đọc được danh mục: " + ex.Message; }
    }

    /// <summary>Thư mục lưu bộ cài tải về: ổ đệm (không bị xoá) nếu có, không thì Downloads.</summary>
    private string DownloadRoot() =>
        Staging is { Auto: false } s ? Path.Combine(Path.GetPathRoot(s.Value)!, "ITH-Windows") : Path.Combine(ActiveUser.Downloads, "ITH-Windows");

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (DlFile is not { } f) return;
        var root = DownloadRoot();
        var media = Path.Combine(root, $"{f.Product.Replace(" ", "")}-{f.Build}-{f.LanguageCode}-{f.Architecture}");
        if (RI.MediaBuilder.Existing(media) is { } ready)
        {
            await LoadImageAsync(ready);
            ShowDownload = false;
            return;
        }
        var part = Path.Combine(root, f.FileName + ".part");
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        var need = f.Size - have + (1L << 30);
        var free = FreeGb(root) * 1073741824.0;
        if (free < need)
        {
            DialogWindow.Info("Không đủ chỗ trống", $"Cần ~{RI.Workflow.Size(need)} trống trên {Path.GetPathRoot(root)} để tải, còn {RI.Workflow.Size((long)free)}.", error: true);
            return;
        }
        Downloading = true;
        _dlCts = new CancellationTokenSource();
        var ct = _dlCts.Token;
        var started = DateTime.UtcNow;
        var startBytes = have;
        try
        {
            var file = Path.Combine(root, f.FileName);
            await RI.EsdDownloader.DownloadAsync(f, file, (d, t) => Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                DlProgress = t > 0 ? d * 100.0 / t : 0;
                var secs = Math.Max(1, (DateTime.UtcNow - started).TotalSeconds);
                var speed = Math.Max(0, d - startBytes) / secs;
                DlText = $"{d / 1048576.0:N0} / {t / 1048576.0:N0} MB" + (speed > 1 && d < t ? $" · {speed / 1048576:0.0} MB/s · còn ~{TimeSpan.FromSeconds((t - d) / speed):h\\:mm\\:ss}" : "");
            }), m => Application.Current?.Dispatcher.InvokeAsync(() => DlText = m), ct);
            DlText = "Đã tải xong, SHA1 khớp với Microsoft. Đang dựng bộ cài (2–5 phút)…";
            var install = await Task.Run(() => RI.MediaBuilder.Build(new RI.ProcessRunner(), file, media,
                m => Application.Current?.Dispatcher.InvokeAsync(() => DlText = m)), ct);
            await LoadImageAsync(install);
            DlText = "Đã tải và dựng xong: " + media;
            ShowDownload = false;
            Log.Info($"Reinstall: downloaded {f.FileName} → {media}");
        }
        catch (OperationCanceledException) { DlText = "Đã dừng tải — bấm Tải lại để tải tiếp phần còn thiếu."; }
        catch (Exception ex)
        {
            DlText = "Lỗi: " + ex.Message;
            Log.Warn($"Reinstall download: {ex}");
        }
        finally
        {
            Downloading = false;
            _dlCts.Dispose();
            _dlCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _dlCts?.Cancel();

    partial void OnEditionChanged(RI.ImageEdition? value) => Invalidate();
    partial void OnStagingChanged(StagingOption? value) => Invalidate();

    /// <summary>Đổi lựa chọn → phải kiểm tra lại.</summary>
    private void Invalidate()
    {
        if (!Checked) return;
        Checked = false;
        CheckText = "Đã đổi lựa chọn — bấm “Kiểm tra máy” lại.";
    }

    // ================= Ổ đệm =================
    /// <summary>Ổ (C:) → số ổ cứng vật lý (Win32_LogicalDiskToPartition: "Disk #0, Partition #1" → "C:").</summary>
    private static Dictionary<string, int> DiskOfDrives()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Wmi.TryQuery("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
        {
            var disk = System.Text.RegularExpressions.Regex.Match(r.Str("Antecedent"), @"Disk #(\d+)");
            var drive = System.Text.RegularExpressions.Regex.Match(r.Str("Dependent"), @"DeviceID=""([A-Z]:)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (disk.Success && drive.Success) map[drive.Groups[1].Value.ToUpperInvariant()] = int.Parse(disk.Groups[1].Value);
        }
        return map;
    }

    private void LoadStaging()
    {
        StagingOptions.Clear();
        var diskOf = DiskOfDrives();
        var targetDisk = diskOf.TryGetValue(Target, out var td) ? td : -1;
        var sameDisk = new List<StagingOption>();
        var otherDisk = new List<StagingOption>();
        StagingOptions.Add(new StagingOption("auto", RI.Platform.IsLegacy
            // Windows 7 / 8 không tự thu nhỏ ổ được (xem StagingPartition.Probe)
            ? $"Tự động — dùng thư mục {Target}\\ITH ngay trên ổ Windows (không format; xoá mọi thứ trên {Target} trừ thư mục này)"
            : $"Tự động — thu nhỏ {Target} tạo phân vùng đệm (cài xong tự gộp lại); không được thì dùng {Target}\\ITH", true));
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var root = d.Name.TrimEnd('\\').ToUpperInvariant();
                if (root == Target || (d.DriveFormat != "NTFS" && !d.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))) continue;
                var label = d.VolumeLabel.Length > 0 ? d.VolumeLabel + " " : "";
                var other = targetDisk >= 0 && diskOf.TryGetValue(root, out var dk) && dk != targetDisk;
                var opt = new StagingOption(root + "\\ITH",
                    $"{root} {label}— trống {d.AvailableFreeSpace / 1073741824.0:0.#} GB (dùng thư mục {root}\\ITH, dữ liệu trên ổ này giữ nguyên)" +
                    (other ? " · ổ cứng khác" : ""), false);
                (other ? otherDisk : sameDisk).Add(opt);
            }
            catch { }
        }
        // Cùng ổ cứng với Windows lên trước: máy UEFI bật Fast Boot có thể không thấy ổ cứng thứ hai lúc khởi động
        foreach (var o in sameDisk.Concat(otherDisk)) StagingOptions.Add(o);
        // Mặc định: phân vùng cùng ổ cứng đủ chỗ (≥ 20 GB) → tự động (thu nhỏ C:) → máy BIOS / không rõ ổ cứng thì ổ khác đủ chỗ
        Staging = sameDisk.FirstOrDefault(o => FreeGb(o.Value) >= 20)
                  ?? (RI.BootEntry.DetectFirmware(new RI.ProcessRunner()) == RI.FirmwareMode.Bios || targetDisk < 0
                      ? otherDisk.FirstOrDefault(o => FreeGb(o.Value) >= 20) : null)
                  ?? StagingOptions[0];
    }

    private static double FreeGb(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace / 1073741824.0; } catch { return 0; }
    }

    // ================= Sao lưu =================
    private static RI.UserPaths UserPaths() => new()
    {
        Profile = ActiveUser.ProfilePath,
        Desktop = ActiveUser.Folder(Environment.SpecialFolder.DesktopDirectory),
        Documents = ActiveUser.Folder(Environment.SpecialFolder.MyDocuments),
        Downloads = ActiveUser.Downloads,
        Pictures = ActiveUser.Folder(Environment.SpecialFolder.MyPictures),
        Videos = ActiveUser.Folder(Environment.SpecialFolder.MyVideos),
        Music = ActiveUser.Folder(Environment.SpecialFolder.MyMusic),
        Roaming = ActiveUser.Folder(Environment.SpecialFolder.ApplicationData),
        Local = ActiveUser.Folder(Environment.SpecialFolder.LocalApplicationData),
    };

    [RelayCommand]
    private async Task ScanBackupAsync()
    {
        BackupTotal = "Đang đo dung lượng…";
        try
        {
            var keep = BackupItems.ToDictionary(c => ((RI.BackupItem)c.Item).Id, c => c.IsChecked);
            var list = await Task.Run(() => RI.BackupScanner.Scan(UserPaths()));
            foreach (var c in BackupItems) c.PropertyChanged -= BackupChanged;
            BackupItems.Clear();
            foreach (var b in list)
            {
                var note = b.Note;
                if (b.CloudFiles > 0) note = (note.Length > 0 ? note + " · " : "") + $"{b.CloudFiles} file chỉ có trên OneDrive — không chép (vẫn còn trên cloud)";
                var c = new Choice(b, b.Title + (b.Source.Length > 0 ? "  ·  " + b.Source : ""), keep.TryGetValue(b.Id, out var on) ? on : b.Default,
                    note, b.Bytes > 0 ? RI.Workflow.Size(b.Bytes) : "");
                c.PropertyChanged += BackupChanged;
                BackupItems.Add(c);
            }
            UpdateBackupTotal();
        }
        catch (Exception ex) { BackupTotal = "Không đọc được thư mục người dùng: " + ex.Message; }
    }

    private void BackupChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Choice.IsChecked)) return;
        UpdateBackupTotal();
        Invalidate();
    }

    private List<RI.BackupItem> SelectedBackup() => BackupItems.Where(c => c.IsChecked).Select(c => (RI.BackupItem)c.Item).ToList();

    private void UpdateBackupTotal()
    {
        var sel = SelectedBackup();
        BackupTotal = $"Đã chọn {sel.Count}/{BackupItems.Count} mục · {RI.Workflow.Size(sel.Sum(b => b.Bytes))}";
    }

    // ================= BitLocker ổ Windows =================
    private async Task RefreshEncryptionAsync()
    {
        var st = await Task.Run(() => RI.Encryption.Status(new RI.ProcessRunner(), Target));
        TargetEncrypted = st is { Encrypted: true };
        Decrypting = st is { Decrypting: true };
        EncryptionText = st is null || !st.Encrypted ? ""
            : st.Decrypting ? $"Đang giải mã {Target}: còn {st.Percent:0.#}% mã hoá. Vẫn dùng máy bình thường được; xong thì kiểm tra lại."
            : $"{Target} đang bật BitLocker / mã hoá thiết bị — phải tắt trước khi cài lại (giải mã chạy nền 10–60 phút).";
        if (!Decrypting && Checked && !TargetEncrypted && Checks.Any(c => c.Title.Contains("BitLocker") && c.Level == RI.CheckLevel.Block))
            CheckText = $"{Target} đã giải mã xong — bấm “Kiểm tra máy” lại.";
    }

    [RelayCommand]
    private async Task DecryptAsync()
    {
        if (!DialogWindow.Confirm($"Tắt mã hoá ổ {Target}?",
                $"Windows sẽ giải mã {Target} chạy nền (10–60 phút tuỳ dung lượng), vẫn dùng máy bình thường được. " +
                "Môi trường cài không đọc được ổ mã hoá nên phải tắt trước khi cài lại.", "Tắt mã hoá"))
            return;
        var r = await Task.Run(() => RI.Encryption.TurnOff(new RI.ProcessRunner(), Target));
        if (!r.Ok) DialogWindow.Info("Không tắt được mã hoá", r.Output.Trim(), error: true);
        await RefreshEncryptionAsync();
    }

    // ================= Kiểm tra =================
    private RI.ReinstallPlan Plan()
    {
        var p = new RI.ReinstallPlan
        {
            ImagePath = ImagePath,
            ImageIndex = Edition?.Index ?? 1,
            StagingDir = Staging is { Auto: false } s ? s.Value : Target + "\\ITH",
            AutoStaging = Staging?.Auto ?? true,
            TargetDrive = Target,
            AccountName = AccountName.Trim().Length > 0 ? AccountName.Trim() : "Admin",
            Password = Password,
            ComputerName = ComputerName.Trim().Length > 0 ? ComputerName.Trim() : "*",
            MicrosoftAccount = MicrosoftAccount,
            PreventDeviceEncryption = PreventDeviceEncryption,
            ExportDrivers = ExportDrivers,
            Firmware = RI.BootEntry.DetectFirmware(new RI.ProcessRunner()),
        };
        p.Software = Software.Where(c => c.IsChecked).Select(c => ((RI.SoftwareItem)c.Item).Clone()).ToList();
        p.Tweaks = Tweaks.Where(c => c.IsChecked).Select(c => ((RI.TweakItem)c.Item).Clone()).ToList();
        return p;
    }

    private RI.WorkflowOptions Options(bool arm) => new()
    {
        Backup = SelectedBackup(),
        Confirmed = new HashSet<string>(_confirmed, StringComparer.OrdinalIgnoreCase),
        UserName = ActiveUser.Name.Length > 0 ? ActiveUser.Name : null,
        IgnoreBackupErrors = IgnoreBackupErrors,
        NoRestore = !RestoreAfter,
        Arm = arm,
    };

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (!HasImage || Edition is null) { DialogWindow.Info("Chưa chọn bộ cài", "Chọn file ISO Windows trước (tải ở trang “Tải ISO”)."); return; }
        IsBusy = true;
        BusyText = "Đang kiểm tra máy (không thay đổi gì)…";
        try
        {
            var p = Plan();
            var o = Options(false);
            var r = await Task.Run(() => new RI.Workflow(new RI.ProcessRunner(), AddLog).Inspect(p, o));
            ShowChecks(r.Checks);
            await RefreshEncryptionAsync();
        }
        catch (Exception ex)
        {
            Checked = false;
            CheckText = "Lỗi khi kiểm tra: " + ex.Message;
            Log.Warn($"Reinstall check: {ex}");
        }
        finally { IsBusy = false; }
    }

    private void ShowChecks(List<RI.Check> list)
    {
        foreach (var c in Checks) c.PropertyChanged -= ConfirmChanged;
        Checks.Clear();
        foreach (var c in list.OrderByDescending(c => c.Level))
        {
            var row = new CheckRow(c, c.ConfirmKey.Length > 0 && _confirmed.Contains(c.ConfirmKey));
            row.PropertyChanged += ConfirmChanged;
            Checks.Add(row);
        }
        Checked = true;
        UpdateBlocked();
    }

    private void ConfirmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (s is not CheckRow row || e.PropertyName != nameof(CheckRow.Confirmed)) return;
        if (row.Confirmed) _confirmed.Add(row.ConfirmKey); else _confirmed.Remove(row.ConfirmKey);
        UpdateBlocked();
    }

    /// <summary>Mục Block chưa xác nhận → không cho bắt đầu.</summary>
    private void UpdateBlocked()
    {
        var blocks = Checks.Count(c => c.Level == RI.CheckLevel.Block && !(c.NeedsConfirm && c.Confirmed));
        var warns = Checks.Count(c => c.Level == RI.CheckLevel.Warn || (c.Level == RI.CheckLevel.Block && c.NeedsConfirm && c.Confirmed));
        Blocked = blocks > 0;
        CheckText = blocks > 0 ? $"{blocks} mục chặn (đỏ) — xử lý xong rồi kiểm tra lại."
            : warns > 0 ? $"Đủ điều kiện, có {warns} lưu ý (vàng) — đọc kỹ trước khi bắt đầu."
            : "Đủ điều kiện cài lại.";
    }

    // ================= Bắt đầu =================
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart || Edition is null) return;
        var backup = SelectedBackup();
        var summary =
            $"Bản cài: {Edition}\n" +
            $"Ổ đệm: {(Staging?.Auto == true ? "tự động" : Staging?.Value)}\n" +
            $"Sao lưu trước khi cài: {(backup.Count == 0 ? "KHÔNG sao lưu gì" : $"{backup.Count} mục, {RI.Workflow.Size(backup.Sum(b => b.Bytes))}")}\n" +
            $"Tài khoản mới: {(MicrosoftAccount ? "đăng nhập tài khoản Microsoft lúc cài" : AccountName)}\n\n" +
            $"TOÀN BỘ ổ {Target} sẽ bị xoá: Windows, phần mềm, dữ liệu chưa sao lưu. Không hoàn tác được.\n" +
            $"Gõ đúng  XOA {Target}  để xác nhận:";
        var typed = DialogWindow.AskText($"Xoá {Target} và cài lại Windows", summary);
        if (typed is null) return;
        if (!string.Equals(typed.Trim(), $"XOA {Target}", StringComparison.OrdinalIgnoreCase))
        {
            DialogWindow.Info("Chưa xác nhận", $"Gõ không đúng “XOA {Target}” — chưa làm gì.");
            return;
        }
        await RunAsync();
    }

    private async Task RunAsync()
    {
        IsBusy = true;
        Result = "";
        lock (_log) { _log.Clear(); LogText = ""; }
        try
        {
            var p = Plan();
            var o = Options(true);
            var wf = new RI.Workflow(new RI.ProcessRunner(AddLog), AddLog) { StagingReady = OpenLog };
            var r = await Task.Run(() => wf.Prepare(p, o));
            if (r.Checks.Count > 0) ShowChecks(r.Checks);

            if (r.Stop == RI.WorkflowStop.NeedKeyConfirm)
            {
                var keys = string.Join("\n", r.Keys.Select(k => $"Ổ {k.Drive}   {k.Key}"));
                DialogWindow.ShowText("Khoá khôi phục BitLocker — chép lại trước khi cài",
                    keys + $"\n\nĐã lưu vào: {r.KeyFile}\n(file này nằm trong ổ đệm, mất nếu ổ đệm bị xoá).",
                    "Windows mới cần khoá này để mở các ổ dữ liệu đang mã hoá. Chép ra giấy hoặc lưu ngoài máy (điện thoại, email).");
                if (DialogWindow.Confirm("Đã chép khoá khôi phục?", "Tôi đã chép các khoá khôi phục BitLocker ra ngoài máy.", "Đã chép, làm tiếp"))
                {
                    _confirmed.Add(RI.ConfirmKeys.BitLockerKeys);
                    await RunAsync();
                    return;
                }
            }
            if (!r.Ok)
            {
                Result = r.Message.Length > 0 ? r.Message : "Đã dừng.";
                ResultOk = false;
                Log.Warn($"Reinstall: {Result}");
                return;
            }

            _armedStaging = p.StagingDir;
            Armed = true;
            Result = "Đã chuẩn bị xong. Khởi động lại máy để bắt đầu cài (máy tự làm tiếp, 20–40 phút).";
            ResultOk = true;
            Log.Info($"Reinstall: armed {p.StagingDir}");
            if (DialogWindow.Confirm("Khởi động lại ngay?",
                    "Đã chuẩn bị xong. Máy sẽ khởi động vào môi trường cài, xoá " + Target + " và cài Windows mới, rồi tự chép lại dữ liệu đã sao lưu.\n\n" +
                    "Cắm sạc (laptop), đừng tắt máy giữa chừng. Lỗi xảy ra trước khi xoá thì máy tự quay về Windows hiện tại.", "Khởi động lại", danger: true))
                await ProcessRunner.ConsoleAsync("shutdown.exe", "/r /t 10 /c \"IT Helpdesk Tool - cai lai Windows\"");
        }
        catch (Exception ex)
        {
            Result = "Lỗi: " + ex.Message;
            ResultOk = false;
            AddLog("LỖI: " + ex);
            Log.Error("Reinstall", ex);
        }
        finally
        {
            lock (_log) { _logFile?.Dispose(); _logFile = null; }
            IsBusy = false;
        }
    }

    // ================= Huỷ =================
    /// <summary>Thư mục đệm đang được đặt khởi động (lần chuẩn bị trước chưa khởi động lại).</summary>
    private static string FindArmed()
    {
        try
        {
            var auto = RI.StagingPartition.FindExisting();
            var dirs = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name.TrimEnd('\\') + "\\ITH").ToList();
            if (auto != null) dirs.Insert(0, auto + "\\ITH");
            return dirs.FirstOrDefault(d => File.Exists(Path.Combine(d, "bootids.cmd"))) ?? "";
        }
        catch { return ""; }
    }

    [RelayCommand]
    private async Task CancelArmAsync()
    {
        var staging = _armedStaging.Length > 0 ? _armedStaging : FindArmed();
        if (staging.Length == 0) { Armed = false; return; }
        if (!DialogWindow.Confirm("Huỷ cài lại?",
                "Bỏ mục khởi động vào môi trường cài — máy khởi động bình thường vào Windows hiện tại, không xoá gì.", "Huỷ cài lại"))
            return;
        var isOurs = RI.StagingPartition.IsOurs(Path.GetPathRoot(staging) ?? "");
        var remove = DialogWindow.Confirm("Xoá luôn thư mục đệm?",
            $"Xoá {staging} gồm file cài và BẢN SAO LƯU trong đó" +
            (isOurs ? $"; phân vùng đệm do app tạo được xoá và trả dung lượng lại cho {Target}." : ".") +
            "\n\nKhông bấm “Xoá” nếu muốn lấy lại file sao lưu hoặc chuẩn bị lại sau (nhanh hơn).", "Xoá", danger: true);
        IsBusy = true;
        BusyText = "Đang huỷ…";
        try
        {
            await Task.Run(() => new RI.Stager(new RI.ProcessRunner(AddLog), AddLog).Cancel(staging, Target, removeFiles: remove));
            _armedStaging = "";
            Armed = false;
            Result = "Đã huỷ — máy khởi động bình thường vào Windows hiện tại." + (remove ? "" : $" File vẫn ở {staging}.");
            ResultOk = true;
            LoadStaging();
        }
        catch (Exception ex)
        {
            Result = "Không huỷ được: " + ex.Message;
            ResultOk = false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenStaging()
    {
        var dir = _armedStaging.Length > 0 ? _armedStaging : Staging is { Auto: false } s ? s.Value : "";
        if (Directory.Exists(dir)) ShellLauncher.Open(dir);
    }
}
