using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class RepairPage : UserControl
{
    public RepairPage() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
