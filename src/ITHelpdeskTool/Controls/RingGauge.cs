using System.Windows;
using System.Windows.Media;

namespace ITHelpdeskTool.Controls;

/// <summary>Vòng tròn phần trăm (0–100), vẽ trực tiếp, đổi màu theo ngưỡng.</summary>
public sealed class RingGauge : FrameworkElement
{
    private static readonly Brush DefaultTrack = Frozen(0x1E, 0x22, 0x2B);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(7d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bật thì tự đổi sang vàng ≥ 75%, đỏ ≥ 90%.</summary>
    public static readonly DependencyProperty UseThresholdsProperty = DependencyProperty.Register(
        nameof(UseThresholds), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public bool UseThresholds { get => (bool)GetValue(UseThresholdsProperty); set => SetValue(UseThresholdsProperty, value); }

    private static readonly Brush Warn = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush Bad = Frozen(0xF8, 0x71, 0x71);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var t = Thickness;
        var r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new Pen(Track, t), c, r, r);

        var v = Math.Clamp(Value, 0, 100);
        if (v <= 0) return;

        var brush = !UseThresholds ? Stroke : v >= 90 ? Bad : v >= 75 ? Warn : Stroke;
        var pen = new Pen(brush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (v >= 99.99)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }

        var angle = v / 100 * 360;
        var start = PointOn(c, r, 0);
        var end = PointOn(c, r, angle);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    // 0 độ = đỉnh trên, quay theo chiều kim đồng hồ
    private static Point PointOn(Point c, double r, double deg)
    {
        var rad = (deg - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
