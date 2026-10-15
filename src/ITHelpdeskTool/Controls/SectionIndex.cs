using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ITHelpdeskTool.Controls;

/// <summary>Một mục trong mục lục.</summary>
public sealed partial class SectionIndexItem(string title, FrameworkElement element) : ObservableObject
{
    public string Title { get; } = title;
    public FrameworkElement Element { get; } = element;
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// Hàng chip mục lục ghim ở đầu trang. Tự tìm các phần tử trong <see cref="Target"/> có gắn
/// <c>c:SectionIndex.Title="…"</c>, bấm chip thì cuộn tới, cuộn tới đâu chip của mục đó sáng lên.
/// </summary>
public sealed class SectionIndex : ItemsControl
{
    // ---- Gắn lên từng thẻ/mục trong vùng cuộn ----
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.RegisterAttached("Title", typeof(string), typeof(SectionIndex), new PropertyMetadata(null));
    public static string? GetTitle(DependencyObject d) => (string?)d.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject d, string? v) => d.SetValue(TitleProperty, v);

    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.Register(nameof(Target), typeof(ScrollViewer), typeof(SectionIndex),
            new PropertyMetadata(null, (d, e) => ((SectionIndex)d).Attach(e.OldValue as ScrollViewer, e.NewValue as ScrollViewer)));
    /// <summary>Vùng cuộn chứa các mục.</summary>
    public ScrollViewer? Target { get => (ScrollViewer?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    private readonly ObservableCollection<SectionIndexItem> _items = [];
    private SectionIndexItem? _clicked;
    private double _clickedOffset = -1;
    private bool _rescanQueued;

    public SectionIndex()
    {
        ItemsSource = _items;
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnChipClick));
        Loaded += (_, _) => QueueRescan();
        // Ẩn khi trang không có mục nào
        _items.CollectionChanged += (_, _) => Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    private void Attach(ScrollViewer? old, ScrollViewer? now)
    {
        if (old is not null) { old.ScrollChanged -= OnScroll; old.Loaded -= OnTargetLoaded; }
        if (now is not null) { now.ScrollChanged += OnScroll; now.Loaded += OnTargetLoaded; }
        QueueRescan();
    }

    private void OnTargetLoaded(object sender, RoutedEventArgs e) => QueueRescan();

    /// <summary>Đọc lại danh sách mục sau khi bố cục xong (nội dung đổi → chiều cao vùng cuộn đổi).</summary>
    private void QueueRescan()
    {
        if (_rescanQueued) return;
        _rescanQueued = true;
        Dispatcher.BeginInvoke(() => { _rescanQueued = false; Rescan(); UpdateActive(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Rescan()
    {
        if (Target?.Content is not DependencyObject root) return;
        var found = new List<(string Title, FrameworkElement El)>();
        Walk(root, found);
        if (found.Count == _items.Count && found.Select((f, i) => ReferenceEquals(f.El, _items[i].Element) && f.Title == _items[i].Title).All(x => x))
            return;
        _items.Clear();
        foreach (var (t, el) in found) _items.Add(new SectionIndexItem(t, el));
    }

    private static void Walk(DependencyObject d, List<(string, FrameworkElement)> found)
    {
        if (d is FrameworkElement fe)
        {
            if (fe.Visibility != Visibility.Visible) return;
            if (GetTitle(fe) is { Length: > 0 } t) found.Add((t, fe));
        }
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), found);
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SectionIndexItem item || Target is null) return;
        var y = Top(item.Element);
        if (double.IsNaN(y)) return;
        var target = Math.Clamp(Target.VerticalOffset + y - 8, 0, Target.ScrollableHeight);
        _clicked = item;
        _clickedOffset = target;
        Target.ScrollToVerticalOffset(target);
        SetActive(item);
        e.Handled = true;
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) QueueRescan();
        UpdateActive();
    }

    private void UpdateActive()
    {
        if (Target is null || _items.Count == 0) return;
        if (_clicked is not null && Math.Abs(Target.VerticalOffset - _clickedOffset) < 1) { SetActive(_clicked); return; }
        _clicked = null;
        SectionIndexItem? active = null;
        foreach (var it in _items)
        {
            var y = Top(it.Element);
            if (double.IsNaN(y)) continue;
            if (y <= 60) active = it;
        }
        // Cuộn hết cỡ → mục cuối (mục ngắn ở cuối không lên tới đầu được)
        if (Target.ScrollableHeight > 0 && Target.VerticalOffset >= Target.ScrollableHeight - 1) active = _items[^1];
        SetActive(active);
    }

    private double Top(FrameworkElement el)
    {
        try
        {
            if (Target is null || !el.IsVisible || !Target.IsAncestorOf(el)) return double.NaN;
            return el.TransformToAncestor(Target).Transform(new Point(0, 0)).Y;
        }
        catch { return double.NaN; }
    }

    /// <summary>Vị trí thẻ trong khung cuộn (null nếu không hiện).</summary>
    private Rect? Bounds(FrameworkElement el)
    {
        try
        {
            if (Target is null || !el.IsVisible || !Target.IsAncestorOf(el)) return null;
            var p = el.TransformToAncestor(Target).Transform(new Point(0, 0));
            return new Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
        }
        catch { return null; }
    }

    /// <summary>
    /// Sáng mục đang xem, cộng các mục ở cột bên cạnh (không chồng lên theo chiều ngang) đang thực sự hiện trong khung nhìn
    /// — vd trang 2 cột: thẻ bên phải cùng hàng, hoặc cả cột phải nếu cửa sổ đủ cao để thấy hết.
    /// </summary>
    private void SetActive(SectionIndexItem? active)
    {
        var a = active is null ? null : Bounds(active.Element);
        var viewH = Target?.ViewportHeight ?? 0;
        foreach (var it in _items)
        {
            if (ReferenceEquals(it, active)) { it.IsActive = true; continue; }
            if (a is not { } ar || Bounds(it.Element) is not { } r) { it.IsActive = false; continue; }
            var sameColumn = Math.Min(ar.Right, r.Right) - Math.Max(ar.Left, r.Left) > 1;
            var visible = Math.Min(r.Bottom, viewH) - Math.Max(r.Top, 0);
            it.IsActive = !sameColumn && visible >= Math.Min(48, r.Height * 0.5);
        }
    }
}
