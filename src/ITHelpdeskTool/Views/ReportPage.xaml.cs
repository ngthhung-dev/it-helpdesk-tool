using System.Windows;
using System.Windows.Controls;
using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.Views;

public partial class ReportPage : UserControl
{
    /// <summary>Mục vừa bấm trên mục lục (giữ sáng kể cả khi mục ngắn ở cuối trang không cuộn lên tới đầu được).</summary>
    private ReportSection? _clicked;
    private double _clickedOffset = -1;

    public ReportPage() => InitializeComponent();

    /// <summary>Bấm chip mục lục → cuộn tới thẻ của mục đó.</summary>
    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReportSection section) return;
        if (SectionList.ItemContainerGenerator.ContainerFromItem(section) is not FrameworkElement el) return;
        var pos = el.TransformToAncestor(Scroller).Transform(new Point(0, 0));
        var target = Math.Clamp(Scroller.VerticalOffset + pos.Y - 8, 0, Scroller.ScrollableHeight);
        _clicked = section;
        _clickedOffset = target;
        Scroller.ScrollToVerticalOffset(target);
        SetActive(section);
    }

    /// <summary>Cuộn tới đâu thì chip của mục đó sáng lên.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ViewModels.ReportViewModel vm) return;
        if (_clicked is not null && Math.Abs(Scroller.VerticalOffset - _clickedOffset) < 1) { SetActive(_clicked); return; }
        _clicked = null;

        ReportSection? active = null;
        const double threshold = 60; // mép trên thẻ đã lên gần đầu vùng cuộn
        foreach (var s in vm.Sections)
        {
            if (SectionList.ItemContainerGenerator.ContainerFromItem(s) is not FrameworkElement el || !el.IsVisible) continue;
            double y;
            try { y = el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y; } catch { continue; }
            if (y <= threshold) active = s;
            else break;
        }
        // Cuộn hết cỡ → mục cuối (mục ngắn ở cuối không bao giờ lên tới đầu)
        if (Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1 && vm.Sections.Count > 0)
            active = vm.Sections[^1];
        SetActive(active);
    }

    private void SetActive(ReportSection? active)
    {
        if (DataContext is not ViewModels.ReportViewModel vm) return;
        foreach (var s in vm.Sections) s.IsActive = ReferenceEquals(s, active);
    }

    /// <summary>Xem tất cả / Thu gọn bảng dài.</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReportTable t) t.IsExpanded = !t.IsExpanded;
    }
}
