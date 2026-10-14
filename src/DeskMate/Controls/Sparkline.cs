using System.Windows;
using System.Windows.Media;

namespace DeskMate.Controls;

/// <summary>Biểu đồ đường mini (thang 0–100) có tô nền mờ bên dưới.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Số điểm tối đa trên trục X (để đường chạy từ phải sang).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Values { get => (double[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var vals = Values;
        if (w <= 0 || h <= 0 || vals is not { Length: > 1 }) return;

        var step = w / Math.Max(Capacity - 1, 1);
        var x0 = w - (vals.Length - 1) * step;
        Point P(int i) => new(x0 + i * step, h - Math.Clamp(vals[i], 0, 100) / 100 * (h - 2) - 1);

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var l = line.Open())
        using (var f = fill.Open())
        {
            l.BeginFigure(P(0), false, false);
            f.BeginFigure(new Point(x0, h), true, true);
            f.LineTo(P(0), false, false);
            for (var i = 1; i < vals.Length; i++)
            {
                l.LineTo(P(i), true, true);
                f.LineTo(P(i), false, false);
            }
            f.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        fill.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var grad = new LinearGradientBrush(
            Color.FromArgb(70, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B), 90);
        grad.Freeze();

        dc.DrawGeometry(grad, null, fill);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
