using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Auth;

namespace ITHelpdeskTool.Views;

public sealed class AccountRow(OnlineAccount a)
{
    public OnlineAccount A { get; } = a;
    public string AdminText => A.Admin ? " · quản trị" : "";
    public string ToggleText => A.Enabled ? "Khoá" : "Mở khoá";
    public string StatusText => !A.Enabled ? "Đã khoá" : A.Expired ? "Hết hạn" : "Hoạt động";
    public Brush StatusBrush => (Brush)Application.Current.FindResource(!A.Enabled ? "BadBrush" : A.Expired ? "WarnBrush" : "GoodBrush");
    public string Detail =>
        (A.ExpiresDate is { } d ? $" · hạn đến {d:dd/MM/yyyy}" : " · không hết hạn") +
        (string.IsNullOrWhiteSpace(A.Note) ? "" : " · " + A.Note);
}

/// <summary>Quản lý danh sách tài khoản online (chỉ tài khoản quản trị mở được).</summary>
public partial class AccountsWindow : Window
{
    private OnlineAccountsFile _file = new();
    private string _sha = "";
    private bool _busy;

    public AccountsWindow()
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Loaded += async (_, _) => { UpdateToken(); await LoadAsync(); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateToken() =>
        TokenText.Text = OnlineAuth.Token is null ? "Chưa có token GitHub — chỉ xem được, chưa lưu được thay đổi." : "Đã có token GitHub trên máy này.";

    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "BadBrush" : "MutedBrush");
    }

    private void Render() =>
        List.ItemsSource = _file.Accounts.OrderByDescending(a => a.Admin).ThenBy(a => a.User, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AccountRow(a)).ToList();

    private async Task LoadAsync()
    {
        Status("Đang tải danh sách…");
        try
        {
            if (OnlineAuth.Token is { } t) (_file, _sha) = await OnlineAuth.ReadForEditAsync(t);
            else { _file = await OnlineAuth.FetchAsync(); _sha = ""; }
            Render();
            Status($"{_file.Accounts.Count} tài khoản · {_file.Accounts.Count(a => a.Enabled && !a.Expired)} đang hoạt động.");
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Token_Click(object sender, RoutedEventArgs e)
    {
        var t = DialogWindow.AskText("Token GitHub",
            "Dán token GitHub (fine-grained, chỉ cho repo it-helpdesk-tool, quyền Contents: Read and write).\n" +
            "Tạo ở GitHub → Settings → Developer settings → Fine-grained tokens. Để trống để xoá token khỏi máy này.");
        if (t is null) return;
        OnlineAuth.Token = t;
        UpdateToken();
        await LoadAsync();
    }

    /// <summary>Sửa danh sách rồi lưu ngay lên GitHub; lỗi thì tải lại bản trên mạng.</summary>
    private async Task EditAsync(string message, Func<OnlineAccountsFile, string?> change)
    {
        if (_busy) return;
        if (OnlineAuth.Token is not { } token) { DialogWindow.Info("Cần token GitHub", "Bấm “Đặt token…” trước để lưu được thay đổi.", error: true); return; }
        _busy = true;
        IsEnabled = false;
        try
        {
            (_file, _sha) = await OnlineAuth.ReadForEditAsync(token);   // luôn sửa trên bản mới nhất
            if (change(_file) is { } err) { Status(err, true); Render(); return; }
            await OnlineAuth.SaveAsync(token, _file, _sha, message);
            Log.Info("Tài khoản online: " + message);
            await LoadAsync();
            Status("Đã lưu: " + message);
        }
        catch (Exception ex) { Status(ex.Message, true); await LoadAsync(); }
        finally { _busy = false; IsEnabled = true; }
    }

    private static OnlineAccount? Find(OnlineAccountsFile f, string user) =>
        f.Accounts.FirstOrDefault(a => a.User.Equals(user, StringComparison.OrdinalIgnoreCase));

    private static bool TryDate(string s, out string? iso)
    {
        iso = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (DateTime.TryParseExact(s.Trim(), ["d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { iso = d.ToString("yyyy-MM-dd"); return true; }
        return false;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var user = NewUser.Text.Trim();
        var pw = NewPw.Password;
        if (user.Length < 3) { Status("Tên đăng nhập tối thiểu 3 ký tự.", true); return; }
        if (pw.Length < 6) { Status("Mật khẩu tối thiểu 6 ký tự.", true); return; }
        if (!TryDate(NewExpires.Text, out var exp)) { Status("Ngày hết hạn sai, nhập dạng dd/MM/yyyy.", true); return; }
        var admin = NewAdmin.IsChecked == true;
        var note = NewNote.Text.Trim();
        await EditAsync($"Thêm tài khoản {user}", f =>
        {
            if (Find(f, user) is not null) return $"Đã có tài khoản “{user}”.";
            var a = new OnlineAccount { User = user, Admin = admin, Expires = exp, Note = note.Length > 0 ? note : null };
            OnlineAuth.SetPassword(a, pw);
            f.Accounts.Add(a);
            return null;
        });
        if (StatusText.Text.StartsWith("Đã lưu")) { NewUser.Clear(); NewPw.Clear(); NewExpires.Clear(); NewNote.Clear(); NewAdmin.IsChecked = false; }
    }

    private static AccountRow Row(object sender) => (AccountRow)((FrameworkElement)sender).Tag;

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        var pw = DialogWindow.AskPassword($"Đặt lại mật khẩu cho {r.A.User}", "Mật khẩu mới (tối thiểu 6 ký tự). Báo lại cho người dùng.", allowEmpty: false);
        if (pw is null) return;
        if (pw.Length < 6) { Status("Mật khẩu tối thiểu 6 ký tự.", true); return; }
        await EditAsync($"Đặt lại mật khẩu {r.A.User}", f =>
        {
            if (Find(f, r.A.User) is not { } a) return "Tài khoản không còn trong danh sách.";
            OnlineAuth.SetPassword(a, pw);
            return null;
        });
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        if (r.A.Enabled && r.A.User.Equals(OnlineAuth.Current?.User, StringComparison.OrdinalIgnoreCase))
        { Status("Không tự khoá tài khoản đang dùng.", true); return; }
        var on = !r.A.Enabled;
        await EditAsync($"{(on ? "Mở khoá" : "Khoá")} tài khoản {r.A.User}", f =>
        {
            if (Find(f, r.A.User) is not { } a) return "Tài khoản không còn trong danh sách.";
            if (!on && a.Admin && f.Accounts.Count(x => x.Admin && x.Enabled) <= 1) return "Phải còn ít nhất một quản trị viên hoạt động.";
            a.Enabled = on;
            return null;
        });
    }

    private async void Expire_Click(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        var s = DialogWindow.AskText($"Hạn dùng của {r.A.User}", "Nhập ngày hết hạn (dd/MM/yyyy). Để trống = không hết hạn.",
            r.A.ExpiresDate?.ToString("dd/MM/yyyy") ?? "");
        if (s is null) return;
        if (!TryDate(s, out var exp)) { Status("Ngày hết hạn sai, nhập dạng dd/MM/yyyy.", true); return; }
        await EditAsync($"Hạn dùng {r.A.User}: {exp ?? "không hết hạn"}", f =>
        {
            if (Find(f, r.A.User) is not { } a) return "Tài khoản không còn trong danh sách.";
            a.Expires = exp;
            return null;
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var r = Row(sender);
        if (r.A.User.Equals(OnlineAuth.Current?.User, StringComparison.OrdinalIgnoreCase)) { Status("Không xoá tài khoản đang dùng.", true); return; }
        if (!DialogWindow.Confirm($"Xoá tài khoản {r.A.User}?", "Người này sẽ không đăng nhập được nữa.", "Xoá", danger: true)) return;
        await EditAsync($"Xoá tài khoản {r.A.User}", f =>
        {
            if (Find(f, r.A.User) is not { } a) return "Tài khoản không còn trong danh sách.";
            if (a.Admin && f.Accounts.Count(x => x.Admin && x.Enabled) <= 1 && a.Enabled) return "Phải còn ít nhất một quản trị viên hoạt động.";
            f.Accounts.Remove(a);
            return null;
        });
    }

    private async void Message_Click(object sender, RoutedEventArgs e)
    {
        var s = DialogWindow.AskText("Thông báo ở màn hình đăng nhập", "Hiện cho mọi người dùng khi đăng nhập (vd: bảo trì, liên hệ). Để trống = không hiện.", _file.Message ?? "");
        if (s is null) return;
        await EditAsync("Sửa thông báo đăng nhập", f => { f.Message = s.Length > 0 ? s : null; return null; });
    }
}
