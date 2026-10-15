using System.Windows;
using System.Windows.Media;
using ITHelpdeskTool.Services.Fonts;

namespace ITHelpdeskTool.Controls;

/// <summary>
/// Vẽ chữ mẫu bằng font mã cũ (TCVN3 / VNI) theo từng glyph lấy thẳng từ file font.
/// Không qua bộ xử lý chữ của WPF vì: mã TCVN3 của "ư" trùng ký tự gạch nối mềm (bị ẩn), "ã" trùng dấu chấm giữa…,
/// và mỗi font cũ ghi bảng ký tự hơi khác nhau. Tự xuống dòng theo bề ngang.
/// </summary>
public sealed class LegacyFontText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty FontFileProperty = Reg(nameof(FontFile), typeof(string), "");
    public static readonly DependencyProperty FamilyNameProperty = Reg(nameof(FamilyName), typeof(string), "");
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 14.0);
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty WrapProperty = Reg(nameof(Wrap), typeof(bool), true);

    private static DependencyProperty Reg(string name, Type t, object def) =>
        DependencyProperty.Register(name, t, typeof(LegacyFontText),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Đường dẫn file font (.ttf).</summary>
    public string FontFile { get => (string)GetValue(FontFileProperty); set => SetValue(FontFileProperty, value); }
    /// <summary>Tên họ font — để biết bảng mã (.Vn… = TCVN3, VNI… = VNI).</summary>
    public string FamilyName { get => (string)GetValue(FamilyNameProperty); set => SetValue(FamilyNameProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public bool Wrap { get => (bool)GetValue(WrapProperty); set => SetValue(WrapProperty, value); }

    static LegacyFontText() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private static readonly Dictionary<string, GlyphTypeface?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private List<(ushort[] Glyphs, double[] Advances)> _lines = [];
    private GlyphTypeface? _face;

    private static GlyphTypeface? Load(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var g)) return g;
            try { g = new GlyphTypeface(new Uri(file)); } catch { g = null; }
            Cache[file] = g;
            return g;
        }
    }

    /// <summary>Glyph cho một mã byte của bảng mã cũ — thử các cách font cũ hay ghi trong bảng ký tự.</summary>
    public static ushort GlyphFor(GlyphTypeface face, char ch)
    {
        var map = face.CharacterToGlyphMap;
        if (map.TryGetValue(ch, out var g) && g != 0) return g;
        if (ch is >= '\u0080' and <= 'ÿ')
        {
            // Font ký hiệu: U+F0xx
            if (map.TryGetValue(0xF000 | ch, out g) && g != 0) return g;
            // Ký tự cp1252 vùng 0x80–0x9F
            if (ch <= '\u009F')
            {
                var c = System.Text.Encoding.GetEncoding(1252).GetString([(byte)ch])[0];
                if (map.TryGetValue(c, out g) && g != 0) return g;
            }
            // Một số tên glyph cũ được ánh xạ khác
            int[] alt = ch switch
            {
                '·' => [0x2219, 0x22C5],
                '­' => [0x2010, 0x2011],
                ' ' => [0x0020],
                _ => [],
            };
            foreach (var a in alt) if (map.TryGetValue(a, out g) && g != 0) return g;
        }
        return map.TryGetValue('?', out g) ? g : (ushort)0;
    }

    protected override Size MeasureOverride(Size available)
    {
        _lines = [];
        _face = Load(FontFile);
        if (_face is null || string.IsNullOrEmpty(Text)) return new Size(0, 0);
        var size = FontSize;
        var encoded = LegacyVnEncoding.Convert(Text, FamilyName);
        var maxW = Wrap && !double.IsInfinity(available.Width) ? available.Width : double.PositiveInfinity;

        // Tách theo khoảng trắng để xuống dòng giữa các từ
        var space = GlyphFor(_face, ' ');
        var spaceW = _face.AdvanceWidths.TryGetValue(space, out var sw) ? sw * size : size / 3;
        var words = encoded.Split(' ');
        var lineG = new List<ushort>(); var lineA = new List<double>(); double lineW = 0;
        double widest = 0;
        foreach (var word in words)
        {
            var wg = new List<ushort>(); var wa = new List<double>(); double ww = 0;
            foreach (var ch in word)
            {
                var g = GlyphFor(_face, ch);
                var a = _face.AdvanceWidths.TryGetValue(g, out var aw) ? aw * size : size / 2;
                wg.Add(g); wa.Add(a); ww += a;
            }
            if (lineG.Count > 0 && lineW + spaceW + ww > maxW)
            {
                _lines.Add((lineG.ToArray(), lineA.ToArray()));
                widest = Math.Max(widest, lineW);
                lineG = []; lineA = []; lineW = 0;
            }
            if (lineG.Count > 0) { lineG.Add(space); lineA.Add(spaceW); lineW += spaceW; }
            // Từ dài hơn cả dòng: cắt theo ký tự
            for (var i = 0; i < wg.Count; i++)
            {
                if (lineG.Count > 0 && lineW + wa[i] > maxW && lineW > 0 && ww > maxW)
                {
                    _lines.Add((lineG.ToArray(), lineA.ToArray()));
                    widest = Math.Max(widest, lineW);
                    lineG = []; lineA = []; lineW = 0;
                }
                lineG.Add(wg[i]); lineA.Add(wa[i]); lineW += wa[i];
            }
        }
        if (lineG.Count > 0) { _lines.Add((lineG.ToArray(), lineA.ToArray())); widest = Math.Max(widest, lineW); }
        var h = _face.Height * size * _lines.Count;
        return new Size(double.IsInfinity(maxW) ? widest : Math.Min(widest, maxW), h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_face is null || _lines.Count == 0) return;
        var size = FontSize;
        var lineH = _face.Height * size;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < _lines.Count; i++)
        {
            var (glyphs, adv) = _lines[i];
            if (glyphs.Length == 0) continue;
            var origin = new Point(0, i * lineH + _face.Baseline * size);
            var run = new GlyphRun(_face, 0, false, size, (float)dpi, glyphs, origin, adv, null, null, null, null, null, null);
            dc.DrawGlyphRun(Foreground, run);
        }
    }
}
