using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;
using S = ITHelpdeskTool.Services.SysConfig.SysConfigService;

namespace ITHelpdeskTool.ViewModels;

public sealed record ActionLogEntry(string Time, string Text, bool Ok);

/// <summary>Công cụ Windows mở nhanh (có tham số).</summary>
public sealed record SysTool(string Glyph, string Title, string Target, string? Args = null);

public sealed partial class SysConfigViewModel : PageViewModel
{
    public override string Title => "Cấu hình hệ thống";

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loaded;

    public SysConfigViewModel()
    {
        _clock.Tick += (_, _) => CurrentTime = DateTime.Now.ToString("HH:mm:ss · dd/MM/yyyy");
        TimeZones = TimeZoneInfo.GetSystemTimeZones();
        Cultures = S.CommonCultures.Select(c => new KeyValuePair<string, string>(c.Name, c.Display)).ToList();
    }

    public override void OnNavigatedTo()
    {
        _clock.Start();
        CurrentTime = DateTime.Now.ToString("HH:mm:ss · dd/MM/yyyy");
        if (!_loaded) { _loaded = true; _ = RefreshAsync(); }
    }

    public override void OnNavigatedFrom() => _clock.Stop();

    // ================= Trạng thái chung =================
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _needsRestart;
    [ObservableProperty] private string _currentTime = "";
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Cấu hình: {text}"); else Log.Warn($"Cấu hình: {text}");
    }

    /// <summary>Chạy một thao tác: khoá giao diện, ghi nhật ký, báo lỗi, rồi đọc lại phần liên quan.</summary>
    private async Task Run(string busy, Func<Task<OpResult>> op, Func<Task>? refresh = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busy + "…";
        OpResult r;
        try { r = await op(); }
        catch (Exception ex) { r = OpResult.Fail(ex.Message); Log.Error(busy, ex); }
        finally { IsBusy = false; }

        AddLog(r.Message, r.Ok);
        if (r.NeedsRestart) NeedsRestart = true;
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        if (refresh is not null)
        {
            try { await refresh(); } catch (Exception ex) { Log.Error("Đọc lại cấu hình lỗi", ex); }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await Task.WhenAll(LoadIdentityAsync(), LoadTimeAsync(), LoadNetworkAsync(), LoadUsersAsync(),
                LoadSharingAsync(), LoadSystemAsync());
        }
        finally { IsLoading = false; }
    }

    // ================= Tên máy & nhóm =================
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GroupText))] private IdentityInfo? _identity;
    public string GroupText => Identity is null ? "" : Identity.InDomain ? $"Domain: {Identity.Domain}" : $"Workgroup: {Identity.Workgroup}";
    [ObservableProperty] private string _newComputerName = "";
    [ObservableProperty] private string _newWorkgroup = "";

    private async Task LoadIdentityAsync()
    {
        Identity = await Task.Run(S.ReadIdentity);
        if (Identity.PendingName.Length > 0) NeedsRestart = true;
        NewComputerName = Identity.PendingName.Length > 0 ? Identity.PendingName : Identity.ComputerName;
        NewWorkgroup = Identity.Workgroup;
    }

    [RelayCommand]
    private Task RenameAsync() => Run("Đang đổi tên máy", () => S.RenameComputerAsync(NewComputerName), LoadIdentityAsync);

    [RelayCommand]
    private Task SetWorkgroupAsync() => Run("Đang đổi workgroup", () => S.SetWorkgroupAsync(NewWorkgroup), LoadIdentityAsync);

    // ================= Thời gian & khu vực =================
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Cultures { get; }
    [ObservableProperty] private TimeZoneInfo? _selectedTimeZone;
    [ObservableProperty] private string _ntpServer = "time.windows.com";
    [ObservableProperty] private bool _timeServiceRunning;
    [ObservableProperty] private string? _selectedCulture;
    [ObservableProperty] private string _currentCulture = "";

    private async Task LoadTimeAsync()
    {
        var t = await Task.Run(S.ReadTime);
        SelectedTimeZone = TimeZones.FirstOrDefault(z => z.Id == t.TimeZoneId);
        if (t.NtpServer.Length > 0) NtpServer = t.NtpServer;
        TimeServiceRunning = t.TimeServiceRunning;
        CurrentCulture = t.Culture;
        SelectedCulture = Cultures.Any(c => c.Key == t.Culture) ? t.Culture : null;
    }

    [RelayCommand]
    private Task ApplyTimeZoneAsync() => SelectedTimeZone is { } tz
        ? Run("Đang đặt múi giờ", () => S.SetTimeZoneAsync(tz.Id), LoadTimeAsync)
        : Task.CompletedTask;

    [RelayCommand]
    private Task SyncTimeAsync() => Run("Đang đồng bộ giờ", () => S.SyncTimeAsync(NtpServer), LoadTimeAsync);

    [RelayCommand]
    private Task ApplyCultureAsync() => SelectedCulture is { } c
        ? Run("Đang đặt định dạng khu vực", () => S.SetCultureAsync(c), LoadTimeAsync)
        : Task.CompletedTask;

    // ================= Mạng =================
    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    [ObservableProperty] private AdapterInfo? _selectedAdapter;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseStatic))] private bool _useDhcp = true;
    public bool UseStatic { get => !UseDhcp; set => UseDhcp = !value; }
    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private string _mask = "";
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DnsManual))] private bool _dnsAuto = true;
    public bool DnsManual { get => !DnsAuto; set => DnsAuto = !value; }
    [ObservableProperty] private string _dns1 = "";
    [ObservableProperty] private string _dns2 = "";

    private async Task LoadNetworkAsync()
    {
        var keep = SelectedAdapter?.Name;
        var list = await Task.Run(S.ReadAdapters);
        Adapters.Clear();
        foreach (var a in list) Adapters.Add(a);
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Name == keep) ?? Adapters.FirstOrDefault(a => a.Connected) ?? Adapters.FirstOrDefault();
    }

    partial void OnSelectedAdapterChanged(AdapterInfo? value)
    {
        if (value is null) return;
        UseDhcp = value.Dhcp;
        Ip = value.Ip;
        Mask = value.Mask.Length > 0 ? value.Mask : "255.255.255.0";
        Gateway = value.Gateway;
        DnsAuto = value.DnsAuto;
        Dns1 = value.Dns.ElementAtOrDefault(0) ?? "";
        Dns2 = value.Dns.ElementAtOrDefault(1) ?? "";
    }

    /// <summary>Điền nhanh DNS: auto | google | cloudflare | quad9.</summary>
    [RelayCommand]
    private void DnsPreset(string name)
    {
        (DnsAuto, Dns1, Dns2) = name switch
        {
            "google" => (false, "8.8.8.8", "8.8.4.4"),
            "cloudflare" => (false, "1.1.1.1", "1.0.0.1"),
            "quad9" => (false, "9.9.9.9", "149.112.112.112"),
            _ => (true, "", ""),
        };
    }

    [RelayCommand]
    private Task ApplyNetworkAsync()
    {
        if (SelectedAdapter is not { } a) return Task.CompletedTask;
        if (UseStatic && !DnsManual)
        {
            DialogWindow.Info("Thiếu DNS", "IP tĩnh không nhận được DNS tự động. Hãy nhập DNS hoặc chọn nhanh Google/Cloudflare.");
            return Task.CompletedTask;
        }
        if (a.Connected && !DialogWindow.Confirm("Đổi cấu hình mạng?",
                $"Áp dụng cho \"{a.Name}\". Kết nối có thể ngắt vài giây; nếu nhập sai IP/gateway máy sẽ mất mạng cho tới khi sửa lại.",
                "Áp dụng"))
            return Task.CompletedTask;

        return Run($"Đang cấu hình {a.Name}", async () =>
        {
            if (UseStatic) return await S.SetStaticAsync(a.Name, Ip, Mask, Gateway, Dns1, Dns2);
            var r = await S.SetDhcpAsync(a.Name, alsoDns: DnsAuto);
            if (!r.Ok || DnsAuto) return r;
            var d = await S.SetDnsAsync(a.Name, Dns1, Dns2);
            return d.Ok ? OpResult.Success($"{a.Name}: IP tự động, DNS {Dns1} {Dns2}".Trim()) : d;
        }, async () => { await Task.Delay(1500); await LoadNetworkAsync(); });
    }

    [RelayCommand]
    private Task FlushDnsAsync() => Run("Đang xoá cache DNS", S.FlushDnsAsync);

    [RelayCommand]
    private Task RenewIpAsync() =>
        DialogWindow.Confirm("Xin lại IP?", "Mọi card mạng dùng DHCP sẽ mất kết nối vài giây trong lúc xin lại địa chỉ IP.", "Xin lại IP")
            ? Run("Đang xin lại IP", S.RenewIpAsync, LoadNetworkAsync)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ResetNetworkAsync() =>
        DialogWindow.Confirm("Reset toàn bộ mạng?",
            "Reset Winsock và TCP/IP về mặc định — hay dùng khi lỗi \"có mạng nhưng không vào được web\". " +
            "IP tĩnh đã đặt có thể bị xoá. Cần khởi động lại máy sau đó.", "Reset", danger: true)
            ? Run("Đang reset mạng", S.ResetNetworkAsync, LoadNetworkAsync)
            : Task.CompletedTask;

    // ================= Tài khoản =================
    public ObservableCollection<UserInfo> Users { get; } = [];
    [ObservableProperty] private string _newUserName = "";
    [ObservableProperty] private bool _newUserAdmin;
    [ObservableProperty] private string _usersError = "";

    private async Task LoadUsersAsync()
    {
        try
        {
            var list = await S.ReadUsersAsync();
            Users.Clear();
            foreach (var u in list) Users.Add(u);
            UsersError = "";
        }
        catch (Exception ex) { UsersError = $"Không đọc được danh sách tài khoản: {ex.Message}"; }
    }

    [RelayCommand]
    private Task CreateUserAsync()
    {
        var name = NewUserName.Trim();
        if (S.ValidateUserName(name) is { } err) { DialogWindow.Info("Tên không hợp lệ", err, error: true); return Task.CompletedTask; }
        if (Users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { DialogWindow.Info("Đã có tài khoản này", $"Máy đã có tài khoản \"{name}\".", error: true); return Task.CompletedTask; }

        var pw = DialogWindow.AskPassword($"Mật khẩu cho {name}", "Để trống nếu muốn đăng nhập không cần mật khẩu.");
        if (pw is null) return Task.CompletedTask;
        return Run($"Đang tạo tài khoản {name}", () => S.CreateUserAsync(name, pw, NewUserAdmin), async () =>
        {
            await LoadUsersAsync();
            if (Users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { NewUserName = ""; NewUserAdmin = false; }
        });
    }

    [RelayCommand]
    private Task SetPasswordAsync(UserInfo u)
    {
        var pw = DialogWindow.AskPassword($"Đặt mật khẩu cho {u.Name}",
            "Để trống để xoá mật khẩu. Lưu ý: dữ liệu mã hoá EFS và mật khẩu đã lưu trong trình duyệt của tài khoản này có thể không mở được nữa.");
        return pw is null ? Task.CompletedTask : Run($"Đang đặt mật khẩu cho {u.Name}", () => S.SetPasswordAsync(u.Name, pw), LoadUsersAsync);
    }

    [RelayCommand]
    private Task ToggleUserEnabledAsync(UserInfo u) =>
        Run(u.Enabled ? $"Đang tắt {u.Name}" : $"Đang bật {u.Name}", () => S.SetUserEnabledAsync(u, !u.Enabled, Users), LoadUsersAsync);

    [RelayCommand]
    private Task ToggleUserAdminAsync(UserInfo u) =>
        Run(u.IsAdmin ? $"Đang bỏ quyền quản trị {u.Name}" : $"Đang cấp quyền quản trị {u.Name}",
            () => S.SetUserAdminAsync(u, !u.IsAdmin, Users), LoadUsersAsync);

    [RelayCommand]
    private Task DeleteUserAsync(UserInfo u) =>
        DialogWindow.Confirm($"Xoá tài khoản {u.Name}?",
            "Tài khoản sẽ bị xoá vĩnh viễn. Thư mục dữ liệu trong C:\\Users vẫn được giữ lại để bạn tự sao lưu.", "Xoá", danger: true)
            ? Run($"Đang xoá {u.Name}", () => S.DeleteUserAsync(u, Users), LoadUsersAsync)
            : Task.CompletedTask;

    // ================= Truy cập & chia sẻ =================
    private SharingInfo? _sharing;
    public bool RdpEnabled => _sharing?.RdpEnabled == true;
    public bool NlaRequired => _sharing?.NlaRequired == true;
    public bool FileSharing => _sharing?.FileSharing == true;
    public bool NetworkDiscovery => _sharing?.NetworkDiscovery == true;

    private async Task LoadSharingAsync()
    {
        _sharing = await S.ReadSharingAsync();
        // Luôn báo đổi để công tắc trở về trạng thái thật nếu thao tác thất bại
        foreach (var n in new[] { nameof(RdpEnabled), nameof(NlaRequired), nameof(FileSharing), nameof(NetworkDiscovery) })
            OnPropertyChanged(n);
    }

    [RelayCommand] private Task SetRdpAsync(bool on) => Run(on ? "Đang bật Remote Desktop" : "Đang tắt Remote Desktop", () => S.SetRemoteDesktopAsync(on), LoadSharingAsync);
    [RelayCommand] private Task SetNlaAsync(bool on) => Run("Đang đổi NLA", () => S.SetNlaAsync(on), LoadSharingAsync);
    [RelayCommand] private Task SetFileSharingAsync(bool on) => Run("Đang đổi chia sẻ file", () => S.SetFileSharingAsync(on), LoadSharingAsync);
    [RelayCommand] private Task SetNetworkDiscoveryAsync(bool on) => Run("Đang đổi Network Discovery", () => S.SetNetworkDiscoveryAsync(on), LoadSharingAsync);

    // ================= Hệ thống =================
    private SystemInfo? _system;
    public bool HibernateOn => _system?.HibernateOn == true;
    public bool FastStartupOn => _system?.FastStartupOn == true;
    public bool RestoreSupported => _system?.RestoreSupported == true;
    public string RestoreText => _system is null ? "" :
        !_system.RestoreSupported ? "Windows Server không có System Restore" :
        (_system.RestoreOnSystemDrive ? "Đang bật bảo vệ ổ hệ thống" : "Đang tắt — sẽ tự bật khi tạo điểm khôi phục") +
        (_system.LastRestorePoint.Length > 0 ? $" · gần nhất: {_system.LastRestorePoint}" : " · chưa có điểm nào");
    public string PagefileText => _system is null ? "" : _system.PagefileAuto ? "Windows tự quản lý" : "Đặt thủ công";
    [ObservableProperty] private string _hostsText = "";
    [ObservableProperty] private string _restoreDescription = "Trước khi chỉnh sửa – IT Helpdesk Tool";

    private async Task LoadSystemAsync()
    {
        _system = await Task.Run(S.ReadSystem);
        var n = await Task.Run(S.CountHostsEntries);
        HostsText = n < 0 ? "Không đọc được" : n == 0 ? "Mặc định (không có dòng nào)" : $"{n} dòng chặn/chuyển hướng";
        foreach (var p in new[] { nameof(HibernateOn), nameof(FastStartupOn), nameof(RestoreSupported), nameof(RestoreText), nameof(PagefileText) })
            OnPropertyChanged(p);
    }

    [RelayCommand] private Task SetHibernateAsync(bool on) => Run("Đang đổi Hibernate", () => S.SetHibernateAsync(on), LoadSystemAsync);
    [RelayCommand] private Task SetFastStartupAsync(bool on) => Run("Đang đổi Fast Startup", () => S.SetFastStartupAsync(on), LoadSystemAsync);

    [RelayCommand]
    private Task CreateRestorePointAsync() =>
        Run("Đang tạo điểm khôi phục (có thể mất 1–2 phút)", () => S.CreateRestorePointAsync(RestoreDescription), LoadSystemAsync);

    [RelayCommand]
    private void OpenHosts()
    {
        try { S.OpenHostsInNotepad(); AddLog("Đã mở file hosts bằng Notepad.", true); }
        catch (Exception ex) { AddLog($"Không mở được file hosts: {ex.Message}", false); }
    }

    [RelayCommand]
    private Task ResetHostsAsync() =>
        DialogWindow.Confirm("Khôi phục file hosts?",
            "Xoá mọi dòng chặn/chuyển hướng tên miền trong file hosts (hay bị phần mềm crack hoặc mã độc sửa). Bản cũ được sao lưu cạnh file.",
            "Khôi phục")
            ? Run("Đang khôi phục file hosts", S.ResetHostsAsync, LoadSystemAsync)
            : Task.CompletedTask;

    [RelayCommand]
    private void Restart()
    {
        if (!DialogWindow.Confirm("Khởi động lại máy?", "Lưu lại công việc đang mở. Máy sẽ khởi động lại sau 10 giây.", "Khởi động lại", danger: true))
            return;
        var r = S.RestartComputer();
        AddLog(r.Message, r.Ok);
    }

    [RelayCommand]
    private void OpenTool(string target) => ShellLauncher.Open(target);

    public IReadOnlyList<SysTool> QuickTools { get; } =
    [
        new("\uE7F8", "System Properties", "sysdm.cpl"),
        new("\uE9F5", "Advanced system settings", "SystemPropertiesAdvanced.exe"),
        new("\uE787", "Date and Time", "timedate.cpl"),
        new("\uE774", "Region", "intl.cpl"),
        new("\uE77B", "User Accounts", "netplwiz.exe"),
        new("\uE83D", "Windows Firewall", "firewall.cpl"),
        new("\uE945", "Power Options", "powercfg.cpl"),
        new("\uE943", "Environment Variables", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"),
    ];

    [RelayCommand]
    private void OpenQuickTool(SysTool t) => ShellLauncher.Open(t.Target, t.Args);
}
