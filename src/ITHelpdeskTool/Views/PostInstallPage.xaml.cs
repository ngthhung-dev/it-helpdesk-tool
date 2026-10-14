using System.IO;
using System.Windows;
using System.Windows.Controls;
using ITHelpdeskTool.ViewModels;

namespace ITHelpdeskTool.Views;

public partial class PostInstallPage : UserControl
{
    public PostInstallPage() => InitializeComponent();

    private void Offline_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Offline_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var dir = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]);
        if (dir is not null && DataContext is PostInstallViewModel vm) vm.LoadFolder(dir);
    }
}
