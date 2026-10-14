using System.Windows;

namespace ITHelpdeskTool.Views;

/// <summary>Hộp thoại tối màu dùng chung: xác nhận, nhập mật khẩu.</summary>
public partial class DialogWindow : Window
{
    private bool _askPassword;
    private bool _allowEmptyPassword;
    private bool _askText;

    public string Password { get; private set; } = "";

    private DialogWindow()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    /// <summary>Hỏi xác nhận. <paramref name="danger"/> = nút đỏ cho thao tác khó hoàn tác.</summary>
    public static bool Confirm(string title, string message, string okText = "Đồng ý", bool danger = false)
    {
        var d = new DialogWindow();
        d.Setup(title, message, okText, danger ? "\uE7BA" : "\uE946", danger);
        return d.ShowDialog() == true;
    }

    /// <summary>Hỏi mật khẩu mới (nhập 2 lần). Trả null nếu huỷ.</summary>
    public static string? AskPassword(string title, string message, bool allowEmpty = true)
    {
        var d = new DialogWindow { _askPassword = true, _allowEmptyPassword = allowEmpty };
        d.Setup(title, message, "Lưu", "\uE72E", false);
        d.PasswordPanel.Visibility = Visibility.Visible;
        d.Loaded += (_, _) => d.Pw1.Focus();
        return d.ShowDialog() == true ? d.Password : null;
    }

    /// <summary>Hỏi một dòng chữ. Trả null nếu huỷ.</summary>
    public static string? AskText(string title, string message, string initial = "")
    {
        var d = new DialogWindow { _askText = true };
        d.Setup(title, message, "Lưu", "\uE70F", false);
        d.InputText.Text = initial;
        d.InputText.Visibility = Visibility.Visible;
        d.Loaded += (_, _) => { d.InputText.Focus(); d.InputText.SelectAll(); };
        return d.ShowDialog() == true ? d.InputText.Text.Trim() : null;
    }

    /// <summary>Thông báo đơn giản (một nút).</summary>
    public static void Info(string title, string message, bool error = false)
    {
        var d = new DialogWindow();
        d.Setup(title, message, "Đóng", error ? "\uE783" : "\uE946", false);
        d.CancelButton.Visibility = Visibility.Collapsed;
        if (error) d.IconText.Foreground = (System.Windows.Media.Brush)d.FindResource("BadBrush");
        d.ShowDialog();
    }

    private void Setup(string title, string message, string okText, string glyph, bool danger)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        IconText.Text = glyph;
        if (danger)
        {
            IconText.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
            OkButton.Background = (System.Windows.Media.Brush)FindResource("BadBrush");
            OkButton.BorderBrush = OkButton.Background;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_askPassword)
        {
            if (Pw1.Password != Pw2.Password) { ShowError("Hai lần nhập không khớp."); return; }
            if (!_allowEmptyPassword && Pw1.Password.Length == 0) { ShowError("Vui lòng nhập mật khẩu."); return; }
            Password = Pw1.Password;
        }
        if (_askText && InputText.Text.Trim().Length == 0) { ShowError("Vui lòng nhập nội dung."); return; }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
