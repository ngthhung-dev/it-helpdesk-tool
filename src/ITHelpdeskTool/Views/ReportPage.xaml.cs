using System.Windows;
using System.Windows.Controls;
using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.Views;

public partial class ReportPage : UserControl
{
    public ReportPage() => InitializeComponent();

    /// <summary>Bấm chip mục lục → cuộn tới thẻ của mục đó.</summary>
    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReportSection section) return;
        if (SectionList.ItemContainerGenerator.ContainerFromItem(section) is not FrameworkElement el) return;
        var pos = el.TransformToAncestor(Scroller).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + pos.Y - 8);
    }

    /// <summary>Xem tất cả / Thu gọn bảng dài.</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReportTable t) t.IsExpanded = !t.IsExpanded;
    }
}
