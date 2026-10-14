using System.Windows;
using System.Windows.Controls;

namespace ITHelpdeskTool.Controls;

/// <summary>
/// Xếp phần tử thành 2 cột theo hàng (trái, phải, trái, phải...).
/// Mỗi hàng cao bằng phần tử cao nhất của chính hàng đó (khác UniformGrid làm mọi hàng cao bằng nhau).
/// Hẹp hơn <see cref="SingleColumnBelow"/> thì về 1 cột.
/// </summary>
public sealed class TwoColumnPanel : Panel
{
    public double ColumnGap { get; set; } = 28;
    public double SingleColumnBelow { get; set; } = 620;

    private int Columns(double width) => double.IsInfinity(width) || width < SingleColumnBelow ? 1 : 2;

    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var colW = double.IsInfinity(available.Width) ? double.PositiveInfinity
            : cols == 1 ? available.Width : (available.Width - ColumnGap) / 2;

        double height = 0, rowH = 0, maxW = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(colW, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            maxW = Math.Max(maxW, c.DesiredSize.Width);
            if (i % cols == cols - 1 || i == InternalChildren.Count - 1) { height += rowH; rowH = 0; }
        }
        var w = double.IsInfinity(available.Width) ? maxW : available.Width;
        return new Size(w, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var cols = Columns(final.Width);
        var colW = cols == 1 ? final.Width : (final.Width - ColumnGap) / 2;
        double y = 0;
        for (var i = 0; i < InternalChildren.Count; i += cols)
        {
            var rowH = 0d;
            for (var j = 0; j < cols && i + j < InternalChildren.Count; j++)
                rowH = Math.Max(rowH, InternalChildren[i + j].DesiredSize.Height);
            for (var j = 0; j < cols && i + j < InternalChildren.Count; j++)
                InternalChildren[i + j].Arrange(new Rect(j * (colW + ColumnGap), y, colW, rowH));
            y += rowH;
        }
        return final;
    }
}
