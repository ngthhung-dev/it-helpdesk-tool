using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Policy;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

/// <summary>Một dòng policy trên giao diện.</summary>
public sealed partial class PolicyItem(PolicyDef def, bool isHome) : ObservableObject
{
    public PolicyDef Def { get; } = def;
    public string Title => Def.Title;
    public string Description => Def.Description;
    public string? Warning => Def.Warning;
    public bool IsUserScope => Def.Scope == PolicyScope.User;
    public bool ShowProBadge => Def.ProOnly;
    /// <summary>Máy Home + policy chỉ chạy trên Pro → báo trước.</summary>
    public bool NotOnThisEdition => Def.ProOnly && isHome;
    /// <summary>Nhãn hiệu lực; null = có hiệu lực ngay (không cần nhãn).</summary>
    public string? EffectText => Def.Effect switch
    {
        PolicyEffect.Relogon => "Cần đăng xuất lại",
        PolicyEffect.Restart => "Cần khởi động lại",
        _ => null,
    };

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Báo lại IsOn kể cả khi không đổi, để công tắc quay về trạng thái thật nếu thao tác lỗi.</summary>
    public void SetState(bool on)
    {
        if (IsOn == on) OnPropertyChanged(nameof(IsOn));
        else IsOn = on;
    }
}

public sealed partial class PolicyGroup(string title, string glyph, IReadOnlyList<PolicyItem> items) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public IReadOnlyList<PolicyItem> Items { get; } = items;
    public bool HasUserItems => Items.Any(i => i.IsUserScope);
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private int _onCount;
}

/// <summary>Một USB trong danh sách "chỉ USB được phép".</summary>
public sealed partial class UsbAllowRow(UsbStorage d, bool allowed) : ObservableObject
{
    public UsbStorage Disk { get; } = d;
    [ObservableProperty] private bool _isChecked = allowed || d.Present;
    public bool IsAllowed { get; } = allowed;
}

public sealed partial class PolicyViewModel : PageViewModel
{
    public override string Title => "Policy";

    private bool _loaded;
    public bool IsHome { get; } = PolicyService.IsHomeEdition();
    public bool HasGpedit { get; } = PolicyService.HasGpedit();

    public IReadOnlyList<PolicyGroup> Groups { get; }
    public ObservableCollection<PolicyUser> Users { get; } = [];
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _needsRestart;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetIsAdmin), nameof(TargetNeedsProfile))]
    private PolicyUser? _targetUser;
    public bool TargetIsAdmin => TargetUser?.IsAdmin == true;
    public bool TargetNeedsProfile => TargetUser is { HasProfile: false };

    // Mật khẩu
    [ObservableProperty] private string _minLength = "0";
    [ObservableProperty] private string _maxAge = "0";
    [ObservableProperty] private string _lockoutThreshold = "0";
    [ObservableProperty] private string _lockoutMinutes = "15";

    private static readonly Dictionary<string, string> GroupGlyphs = new()
    {
        [PolicyCatalog.GroupUpdate] = "\uE895",
        [PolicyCatalog.GroupUser] = "\uE72E",
        [PolicyCatalog.GroupDevice] = "\uE88E",
        [PolicyCatalog.GroupApps] = "\uE71D",
        [PolicyCatalog.GroupLogon] = "\uE77B",
    };

    public PolicyViewModel()
    {
        Groups = PolicyCatalog.Groups
            .Select(g => new PolicyGroup(g, GroupGlyphs.GetValueOrDefault(g, "\uE8D7"),
                PolicyCatalog.All.Where(p => p.Group == g).Select(p => new PolicyItem(p, IsHome)).ToList()))
            .ToList();
    }

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
        _ = LoadUsbAsync();
    }

    // ================= Chỉ cho dùng USB được phép =================
    public ObservableCollection<UsbAllowRow> UsbRows { get; } = [];
    [ObservableProperty] private bool _usbWlEnabled;
    [ObservableProperty] private string _usbWlStatus = "";

    [RelayCommand]
    private async Task LoadUsbAsync()
    {
        try
        {
            UsbWlEnabled = UsbWhitelistService.IsEnabled();
            var allowed = UsbWhitelistService.AllowedIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = await UsbWhitelistService.ListAsync();
            UsbRows.Clear();
            // Khi đang bật chỉ hiện USB (bỏ ổ trong máy cũng nằm trong danh sách phép)
            foreach (var d in list.Where(d => d.Present || UsbLike(d.InstanceId)))
                UsbRows.Add(new UsbAllowRow(d, allowed.Contains(d.InstanceId)));
            var n = UsbRows.Count(r => r.IsAllowed);
            UsbWlStatus = UsbWlEnabled
                ? $"Đang bật · {n} USB được phép. USB khác cắm vào máy sẽ không được nhận."
                : UsbRows.Count == 0 ? "Chưa cắm USB nào — cắm USB cần cho phép rồi bấm “Quét lại”." : "Đang tắt · mọi USB đều dùng được.";
        }
        catch (Exception ex) { UsbWlStatus = "Không đọc được danh sách USB: " + ex.Message; }
    }

    private static bool UsbLike(string id) =>
        id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || id.Contains("USB", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task EnableUsbWlAsync()
    {
        var ids = UsbRows.Where(r => r.IsChecked).Select(r => r.Disk.InstanceId).ToList();
        if (ids.Count == 0) { DialogWindow.Info("Chưa chọn USB", "Cắm USB cần cho phép vào máy, bấm “Quét lại” rồi tích chọn."); return; }
        var others = UsbRows.Count(r => r.Disk.Present && !r.IsChecked);
        if (!DialogWindow.Confirm($"Chỉ cho dùng {ids.Count} USB đã chọn?",
                "Mọi USB lưu trữ / ổ cứng gắn ngoài khác cắm vào máy sẽ không được nhận (không đọc, không chép được dữ liệu)." +
                (others > 0 ? $"\n\n{others} USB đang cắm mà không được chọn sẽ bị ngắt ngay." : "") +
                "\n\nChuột, bàn phím, máy in, điện thoại sạc qua USB vẫn dùng bình thường. Ổ đĩa trong máy không bị ảnh hưởng.", "Bật", danger: true))
            return;
        IsBusy = true;
        BusyText = "Đang áp dụng danh sách USB được phép…";
        OpResult r;
        try { r = await UsbWhitelistService.EnableAsync(ids); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        await LoadUsbAsync();
    }

    [RelayCommand]
    private async Task DisableUsbWlAsync()
    {
        if (!DialogWindow.Confirm("Tắt chế độ chỉ USB được phép?", "Mọi USB lưu trữ sẽ dùng được lại như bình thường.", "Tắt")) return;
        IsBusy = true;
        BusyText = "Đang gỡ danh sách USB được phép…";
        OpResult r;
        try { r = await UsbWhitelistService.DisableAsync(); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        await LoadUsbAsync();
    }

    /// <summary>Khi đang bật: thêm / bỏ một USB khỏi danh sách phép.</summary>
    [RelayCommand]
    private async Task ToggleUsbAllowAsync(UsbAllowRow? row)
    {
        if (row is null || !UsbWlEnabled) return;
        var allow = !row.IsAllowed;
        if (!allow && UsbRows.Count(r => r.IsAllowed) <= 1)
        {
            DialogWindow.Info("Cần ít nhất một USB", "Muốn chặn hết USB thì dùng mục “Chặn USB lưu trữ”, hoặc tắt chế độ này.");
            return;
        }
        var r = UsbWhitelistService.SetAllowed(row.Disk.InstanceId, allow);
        AddLog($"{row.Disk.Display}: {r.Message}", r.Ok);
        await LoadUsbAsync();
    }

    private IEnumerable<PolicyItem> AllItems => Groups.SelectMany(g => g.Items);

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Policy: {text}"); else Log.Warn($"Policy: {text}");
    }

    private async Task Run(string busy, Func<OpResult> op, bool reload = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busy + "…";
        OpResult r;
        try { r = await Task.Run(op); }
        catch (Exception ex) { r = OpResult.Fail(ex.Message); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (r.NeedsRestart) NeedsRestart = true;
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        if (reload) await LoadStatesAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Đang đọc policy…";
        try
        {
            var keep = TargetUser?.Sid;
            var users = await PolicyService.ReadUsersAsync();
            Users.Clear();
            foreach (var u in users) Users.Add(u);
            // Mặc định chọn tài khoản thường đầu tiên (người dùng cần hạn chế), không có thì chọn chính mình
            TargetUser = Users.FirstOrDefault(u => u.Sid == keep)
                         ?? Users.FirstOrDefault(u => !u.IsAdmin && u.HasProfile)
                         ?? Users.FirstOrDefault(u => u.IsCurrent)
                         ?? Users.FirstOrDefault();
            await LoadPasswordAsync();
        }
        catch (Exception ex) { AddLog($"Không đọc được danh sách tài khoản: {ex.Message}", false); }
        finally { IsBusy = false; }
        await LoadStatesAsync();
    }

    private async Task LoadStatesAsync()
    {
        var sid = TargetUser is { HasProfile: true } t ? t.Sid : null;
        var states = await Task.Run(() => PolicyService.ReadStates(sid));
        foreach (var i in AllItems) i.SetState(states.GetValueOrDefault(i.Def.Key));
        foreach (var g in Groups) g.OnCount = g.Items.Count(i => i.IsOn);
        var on = AllItems.Count(i => i.IsOn);
        Summary = on == 0 ? "Chưa bật policy nào — máy đang theo mặc định của Windows." : $"Đang bật {on}/{AllItems.Count()} policy.";
    }

    partial void OnTargetUserChanged(PolicyUser? value)
    {
        if (_loaded && !IsBusy) _ = LoadStatesAsync();
    }

    partial void OnFilterChanged(string value)
    {
        var f = value.Trim();
        foreach (var g in Groups)
        {
            foreach (var i in g.Items)
                i.IsVisible = f.Length == 0
                              || i.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                              || i.Description.Contains(f, StringComparison.CurrentCultureIgnoreCase);
            g.IsVisible = g.Items.Any(i => i.IsVisible);
        }
    }

    [RelayCommand]
    private Task TogglePolicyAsync(PolicyItem item)
    {
        var on = item.IsOn;   // công tắc đã đổi trạng thái trước khi lệnh chạy
        if (on && item.Def.Scope == PolicyScope.User && TargetUser is { IsCurrent: true, IsAdmin: true }
            && !DialogWindow.Confirm("Áp dụng cho chính bạn?",
                $"Bạn đang áp \"{item.Title}\" cho tài khoản quản trị đang dùng ({TargetUser.Name}). " +
                "Thường nên chọn tài khoản nhân viên ở ô \"Áp dụng cho\" phía trên.", "Vẫn áp dụng"))
        {
            item.SetState(false);
            return Task.CompletedTask;
        }
        if (on && item.Warning is { } w && !DialogWindow.Confirm(item.Title, w, "Bật"))
        {
            item.SetState(false);
            return Task.CompletedTask;
        }
        var user = TargetUser;
        return Run(on ? $"Đang bật {item.Title}" : $"Đang tắt {item.Title}", () => PolicyService.Apply(item.Def, on, user));
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        if (TargetUser is not { } u) return;
        await Run($"Đang tạo hồ sơ cho {u.Name} (khoảng 20 giây)", () => PolicyService.EnsureProfile(u), reload: false);
        await LoadAsync();
    }

    [RelayCommand]
    private Task ResetAllAsync() =>
        DialogWindow.Confirm("Gỡ tất cả policy?",
            "Đưa toàn bộ policy trong trang này về mặc định của Windows" +
            (TargetUser is { } u ? $" (phần người dùng áp cho {u.Name})" : "") +
            ". Policy do domain/GPO khác đặt không bị ảnh hưởng.", "Gỡ tất cả", danger: true)
            ? Run("Đang gỡ policy", () => PolicyService.ResetAll(TargetUser is { HasProfile: true } t ? t : null))
            : Task.CompletedTask;

    // ================= Mật khẩu =================
    private async Task LoadPasswordAsync()
    {
        try
        {
            var p = await PolicyService.ReadPasswordPolicyAsync();
            MinLength = p.MinLength.ToString();
            MaxAge = p.MaxAgeDays.ToString();
            LockoutThreshold = p.LockoutThreshold.ToString();
            if (p.LockoutMinutes > 0) LockoutMinutes = p.LockoutMinutes.ToString();
        }
        catch (Exception ex) { AddLog($"Không đọc được chính sách mật khẩu: {ex.Message}", false); }
    }

    [RelayCommand]
    private async Task ApplyPasswordAsync()
    {
        if (!int.TryParse(MinLength, out var len) || !int.TryParse(MaxAge, out var age) ||
            !int.TryParse(LockoutThreshold, out var th) || !int.TryParse(LockoutMinutes, out var mins))
        {
            DialogWindow.Info("Số không hợp lệ", "Các ô chỉ nhập số nguyên.", error: true);
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Đang đặt chính sách mật khẩu…";
        OpResult r;
        try { r = await PolicyService.SetPasswordPolicyAsync(len, age, th, mins); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        await LoadPasswordAsync();
    }

    [RelayCommand]
    private void PasswordPreset(string name)
    {
        (MinLength, MaxAge, LockoutThreshold, LockoutMinutes) = name switch
        {
            "strict" => ("10", "90", "5", "15"),
            "basic" => ("8", "0", "10", "15"),
            _ => ("0", "0", "0", "15"),
        };
    }

    // ================= Công cụ =================
    [RelayCommand]
    private async Task GpUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Đang chạy gpupdate /force (có thể mất 1 phút)…";
        OpResult r;
        try { r = await PolicyService.GpUpdateAsync(); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (!r.Ok) DialogWindow.Info("gpupdate", r.Message, error: true);
    }

    [RelayCommand]
    private async Task GpResultAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Đang tạo báo cáo gpresult…";
        (OpResult Result, string? Path) r;
        try { r = await PolicyService.GpResultAsync(); }
        finally { IsBusy = false; }
        AddLog(r.Result.Message, r.Result.Ok);
        if (r.Path is not null) ShellLauncher.Open(r.Path);
        else DialogWindow.Info("gpresult", r.Result.Message, error: true);
    }

    [RelayCommand]
    private void OpenGpedit()
    {
        if (!HasGpedit)
        {
            DialogWindow.Info("Không có gpedit", "Windows Home không có Group Policy Editor. Các policy trong trang này vẫn dùng được vì app ghi thẳng vào registry.");
            return;
        }
        ShellLauncher.Open("gpedit.msc");
    }

    [RelayCommand]
    private void Restart()
    {
        if (!DialogWindow.Confirm("Khởi động lại máy?", "Lưu lại công việc đang mở. Máy sẽ khởi động lại sau 10 giây.", "Khởi động lại", danger: true))
            return;
        var r = SysConfigService.RestartComputer();
        AddLog(r.Message, r.Ok);
    }
}
