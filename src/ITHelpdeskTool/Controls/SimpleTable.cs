using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.Controls;

/// <summary>
/// Bảng nhẹ dựng bằng Grid cho ReportTable: cột theo tỉ lệ, chữ chọn/chép được,
/// tô màu theo Tone, hỗ trợ lọc và thu gọn (theo FilterText / IsExpanded của bảng).
/// </summary>
public sealed class SimpleTable : ContentControl
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(ReportTable), typeof(SimpleTable),
        new PropertyMetadata(null, (d, e) => ((SimpleTable)d).OnTableChanged(e.OldValue as ReportTable, e.NewValue as ReportTable)));

    public ReportTable? Table { get => (ReportTable?)GetValue(TableProperty); set => SetValue(TableProperty, value); }

    public SimpleTable() => Focusable = false;

    private void OnTableChanged(ReportTable? oldT, ReportTable? newT)
    {
        if (oldT is not null) oldT.PropertyChanged -= OnTableProp;
        if (newT is not null) newT.PropertyChanged += OnTableProp;
        Rebuild();
    }

    private void OnTableProp(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportTable.FilterText) or nameof(ReportTable.IsExpanded)) Rebuild();
    }

    private Brush B(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void Rebuild()
    {
        var t = Table;
        if (t is null) { Content = null; return; }

        var text = B("TextBrush", Brushes.White);
        var muted = B("MutedBrush", Brushes.Gray);
        var line = B("BorderBrush", Brushes.DimGray);
        var tones = new Dictionary<Tone, Brush>
        {
            [Tone.Good] = B("GoodBrush", Brushes.LightGreen),
            [Tone.Warn] = B("WarnBrush", Brushes.Gold),
            [Tone.Bad] = B("BadBrush", Brushes.IndianRed),
            [Tone.Muted] = muted,
        };

        IEnumerable<ReportCell[]> rows = t.Rows;
        var filter = t.FilterText.Trim();
        var filtering = filter.Length > 0;
        if (filtering)
            rows = rows.Where(r => r.Any(c => c.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        else if (t.Preview > 0 && !t.IsExpanded)
            rows = rows.Take(t.Preview);
        var list = rows.ToList();

        var g = new Grid { SnapsToDevicePixels = true };
        foreach (var w in t.Weights)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

        // Hàng tiêu đề
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < t.Columns.Length; c++)
        {
            var h = new TextBlock
            {
                Text = t.Columns[c],
                Foreground = muted,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(c == 0 ? 0 : 8, 0, 8, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(h, c);
            g.Children.Add(h);
        }
        var head = new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetColumnSpan(head, t.Columns.Length);
        g.Children.Add(head);

        for (var r = 0; r < list.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = r + 1;
            if (r < list.Count - 1)
            {
                var sep = new Border { BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1), Opacity = 0.5 };
                Grid.SetRow(sep, row);
                Grid.SetColumnSpan(sep, t.Columns.Length);
                g.Children.Add(sep);
            }
            for (var c = 0; c < t.Columns.Length; c++)
            {
                var cell = c < list[r].Length ? list[r][c] : new ReportCell("");
                var tb = new TextBlock
                {
                    Text = cell.Text,
                    Foreground = tones.TryGetValue(cell.Tone, out var tone) ? tone : text,
                    FontWeight = cell.Tone is Tone.Good or Tone.Warn or Tone.Bad ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(c == 0 ? 0 : 8, 6, 8, 6),
                    FontSize = 12.5,
                    Background = Brushes.Transparent,   // để chuột phải trúng ô
                };
                Grid.SetRow(tb, row);
                Grid.SetColumn(tb, c);
                g.Children.Add(tb);
            }
        }

        if (list.Count == 0)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = new TextBlock
            {
                Text = filtering ? "Không có dòng nào khớp." : "Không có dữ liệu.",
                Foreground = muted,
                Margin = new Thickness(0, 8, 0, 4),
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, t.Columns.Length);
            g.Children.Add(empty);
        }

        // Chuột phải: chép dòng / chép cả bảng (dạng tab, dán vào Excel được)
        var rightRow = -1;
        g.Background = Brushes.Transparent;
        g.PreviewMouseRightButtonDown += (_, e) =>
        {
            rightRow = e.OriginalSource is FrameworkElement fe ? Grid.GetRow(fe) - 1 : -1;
        };
        var copyRow = new MenuItem { Header = "Chép dòng này" };
        copyRow.Click += (_, _) =>
        {
            if (rightRow >= 0 && rightRow < list.Count) TryCopy(string.Join("\t", list[rightRow].Select(x => x.Text)));
        };
        var copyAll = new MenuItem { Header = "Chép cả bảng" };
        copyAll.Click += (_, _) => TryCopy(string.Join(Environment.NewLine,
            new[] { string.Join("\t", t.Columns) }.Concat(t.Rows.Select(r => string.Join("\t", r.Select(x => x.Text))))));
        var menu = new ContextMenu();
        menu.Items.Add(copyRow);
        menu.Items.Add(copyAll);
        menu.Opened += (_, _) => copyRow.IsEnabled = rightRow >= 0 && rightRow < list.Count;
        g.ContextMenu = menu;

        Content = g;
    }

    private static void TryCopy(string s)
    {
        try { Clipboard.SetText(s); } catch { /* clipboard bận */ }
    }
}
