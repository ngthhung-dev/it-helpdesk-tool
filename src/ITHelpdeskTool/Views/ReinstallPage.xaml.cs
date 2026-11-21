using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class ReinstallPage : UserControl
{
    public ReinstallPage() => InitializeComponent();

    /// <summary>Nhật ký chạy: luôn cuộn xuống dòng mới nhất.</summary>
    private void LogChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
