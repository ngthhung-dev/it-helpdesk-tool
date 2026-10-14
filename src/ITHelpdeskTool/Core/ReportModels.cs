using CommunityToolkit.Mvvm.ComponentModel;

namespace ITHelpdeskTool.Core;

/// <summary>Mức độ của một giá trị: dùng để tô màu và gom vào "Điểm cần chú ý".</summary>
public enum Tone { Normal, Good, Warn, Bad, Muted }

public sealed record ReportField(string Key, string Value, Tone Tone = Tone.Normal);

public sealed record ReportCell(string Text, Tone Tone = Tone.Normal)
{
    public static implicit operator ReportCell(string? text) => new(text ?? "");
    public override string ToString() => Text;
}

/// <summary>Bảng trong một mục báo cáo (RAM theo khe, ổ cứng, phần mềm...).</summary>
public sealed partial class ReportTable : ObservableObject
{
    public ReportTable(string title, string[] columns, double[]? weights = null)
    {
        Title = title;
        Columns = columns;
        Weights = weights ?? columns.Select(_ => 1d).ToArray();
    }

    public string Title { get; }
    public string[] Columns { get; }
    /// <summary>Tỉ lệ độ rộng các cột (kiểu star).</summary>
    public double[] Weights { get; }
    public List<ReportCell[]> Rows { get; } = [];

    /// <summary>Số dòng hiện khi thu gọn (0 = luôn hiện hết).</summary>
    public int Preview { get; init; }
    public bool Filterable { get; init; }

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isExpanded;

    public bool CanExpand => Preview > 0 && Rows.Count > Preview;
    public string ExpandText => IsExpanded ? "Thu gọn" : $"Xem tất cả {Rows.Count} dòng";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));

    public void Add(params ReportCell[] cells) => Rows.Add(cells);
}

/// <summary>Kết quả thô do bộ thu thập trả về (tạo trên luồng nền).</summary>
public sealed class SectionData
{
    public List<ReportField> Fields { get; } = [];
    public List<ReportTable> Tables { get; } = [];
    /// <summary>Có giá trị thì mục này được coi là "không áp dụng" (vd máy bàn không có pin).</summary>
    public string? SkipReason { get; set; }

    public SectionData F(string key, string? value, Tone tone = Tone.Normal)
    {
        if (!string.IsNullOrWhiteSpace(value)) Fields.Add(new ReportField(key, value.Trim(), tone));
        return this;
    }

    public ReportTable Table(string title, string[] columns, double[]? weights = null, int preview = 0, bool filterable = false)
    {
        var t = new ReportTable(title, columns, weights) { Preview = preview, Filterable = filterable };
        Tables.Add(t);
        return t;
    }
}

public enum SectionState { Waiting, Loading, Done, Skipped, Error }

/// <summary>Một mục trên trang báo cáo.</summary>
public sealed partial class ReportSection(string key, string title, string glyph) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    [ObservableProperty] private SectionState _state = SectionState.Waiting;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private IReadOnlyList<ReportField> _fields = [];
    [ObservableProperty] private IReadOnlyList<ReportTable> _tables = [];

    public void Apply(SectionData d)
    {
        Fields = d.Fields;
        Tables = d.Tables;
        if (d.SkipReason is { } why) { Message = why; State = SectionState.Skipped; }
        else { Message = ""; State = SectionState.Done; }
    }

    public void Fail(string error)
    {
        Message = error;
        State = SectionState.Error;
    }

    public void Reset()
    {
        Fields = [];
        Tables = [];
        Message = "";
        State = SectionState.Waiting;
    }
}

/// <summary>Một dòng trong "Điểm cần chú ý".</summary>
public sealed record ReportIssue(string Section, string What, Tone Tone);
