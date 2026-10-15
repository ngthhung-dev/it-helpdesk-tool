using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class WinPePage : UserControl
{
    public WinPePage() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
