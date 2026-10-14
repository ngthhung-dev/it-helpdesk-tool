using System.Windows;
using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class SysConfigPage : UserControl
{
    public SysConfigPage() => InitializeComponent();

    /// <summary>Bấm chip mục lục → cuộn tới thẻ có tên trong Tag.</summary>
    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name || FindName(name) is not FrameworkElement el) return;
        var pos = el.TransformToAncestor(Scroller).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + pos.Y - 8);
    }
}
