using System.Windows;
using System.Windows.Threading;
using ITHelpdeskTool.Services.Auth;

namespace ITHelpdeskTool.Views;

/// <summary>Cửa sổ đăng nhập mở app: tạo tài khoản (lần đầu), đăng nhập, hoặc đổi mật khẩu.</summary>
public partial class LoginWindow : Window
{
    public enum Kind { Setup, Login, Change }

    private readonly Kind _kind;
    private readonly Func<string, string, Task<string?>>? _signIn;
    private int _fails;
    private DateTime _lockedUntil;
    private DispatcherTimer? _lockTimer;

    /// <summary>Tên đăng nhập đã dùng (sau khi bấm OK thành công).</summary>
    public string User => UserBox.Text.Trim();
    public bool Remember => RememberBox.IsChecked == true;

    public LoginWindow(Kind kind, Func<string, string, Task<string?>>? signIn = null, string? user = null, string? sub = null, string? hint = null, bool showRemember = false)
    {
        InitializeComponent();
        _kind = kind;
        _signIn = signIn;
        UserBox.Text = user ?? "";
        RememberBox.Visibility = showRemember ? Visibility.Visible : Visibility.Collapsed;
        RememberBox.IsChecked = showRemember && !string.IsNullOrEmpty(user);
        switch (kind)
        {
            case Kind.Setup:
                SubText.Text = sub ?? "Lần đầu dùng trên máy này — đặt tên đăng nhập và mật khẩu. Lần sau mở app phải nhập đúng mới vào được.";
                PwLabel.Text = "Mật khẩu (tối thiểu 6 ký tự)";
                ConfirmPanel.Visibility = Visibility.Visible;
                OkButton.Content = "Tạo tài khoản và vào app";
                break;
            case Kind.Change:
                SubText.Text = sub ?? "Đổi tên đăng nhập / mật khẩu mở app trên máy này.";
                OldPanel.Visibility = Visibility.Visible;
                UserLabel.Text = "Tên đăng nhập mới";
                PwLabel.Text = "Mật khẩu mới (tối thiểu 6 ký tự)";
                ConfirmPanel.Visibility = Visibility.Visible;
                OkButton.Content = "Lưu";
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                break;
            default:
                SubText.Text = sub ?? "Nhập tài khoản để mở app.";
                break;
        }
        if (hint is not null) { HintText.Text = hint; HintText.Visibility = Visibility.Visible; }
        Loaded += (_, _) =>
        {
            if (kind == Kind.Change) OldPw.Focus();
            else if (UserBox.Text.Length > 0) PwBox.Focus();
            else UserBox.Focus();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Error(string? msg)
    {
        ErrorText.Text = msg ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DateTime.Now < _lockedUntil) return;
            Error(null);
            var user = UserBox.Text.Trim();
            var pw = PwBox.Password;
            switch (_kind)
            {
                case Kind.Setup:
                {
                    if (LocalAuth.ValidateNew(user, pw, Pw2Box.Password) is { } err) { Error(err); return; }
                    IsEnabled = false;
                    await Task.Run(() => LocalAuth.Create(user, pw));
                    DialogResult = true;
                    return;
                }
                case Kind.Change:
                {
                    IsEnabled = false;
                    var old = OldPw.Password; var pw2 = Pw2Box.Password;
                    var err = await Task.Run(() => LocalAuth.Change(old, user, pw, pw2));
                    IsEnabled = true;
                    if (err is not null) { Error(err); return; }
                    DialogResult = true;
                    return;
                }
                default:
                {
                    if (user.Length == 0 || pw.Length == 0) { Error("Nhập tên đăng nhập và mật khẩu."); return; }
                    IsEnabled = false;
                    OkButton.Content = "Đang kiểm tra…";
                    var err = _signIn is null ? "Chưa cấu hình cách đăng nhập." : await _signIn(user, pw);
                    IsEnabled = true;
                    OkButton.Content = "Đăng nhập";
                    if (err is null) { DialogResult = true; return; }
                    _fails++;
                    PwBox.Clear();
                    PwBox.Focus();
                    if (_fails >= 5) { StartLock(30); return; }
                    Error(err);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            Error("Lỗi: " + ex.Message);
        }
    }

    /// <summary>Sai 5 lần liên tiếp: khoá nút đăng nhập một lúc (lần sau mỗi lần sai lại khoá).</summary>
    private void StartLock(int seconds)
    {
        _lockedUntil = DateTime.Now.AddSeconds(seconds);
        OkButton.IsEnabled = false;
        _lockTimer?.Stop();
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Tick()
        {
            var left = (int)Math.Ceiling((_lockedUntil - DateTime.Now).TotalSeconds);
            if (left <= 0) { _lockTimer!.Stop(); OkButton.IsEnabled = true; Error("Có thể thử lại."); return; }
            Error($"Sai quá nhiều lần — thử lại sau {left} giây.");
        }
        _lockTimer.Tick += (_, _) => Tick();
        Tick();
        _lockTimer.Start();
    }
}
