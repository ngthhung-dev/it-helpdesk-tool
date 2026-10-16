using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class WinPePage : UserControl
{
    public WinPePage() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    /// <summary>Bấm vào chữ của thành phần cũng tích / bỏ tích như bấm ô chọn.</summary>
    private void Component_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.Pick<Services.Windows.PeComponent> p }) p.IsChecked = !p.IsChecked;
    }
}
