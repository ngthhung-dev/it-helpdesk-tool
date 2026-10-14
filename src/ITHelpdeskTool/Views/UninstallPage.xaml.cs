using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ITHelpdeskTool.ViewModels;

namespace ITHelpdeskTool.Views;

public partial class UninstallPage : UserControl
{
    public UninstallPage() => InitializeComponent();

    /// <summary>Bấm vào một phần mềm → mở bảng gỡ bên phải.</summary>
    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row && DataContext is UninstallViewModel vm)
            vm.SelectCommand.Execute(row);
    }
}
