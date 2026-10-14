using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.Services.Report;

/// <summary>
/// Thu thập dữ liệu cho trang Báo cáo cấu hình. Mỗi mục là một hàm chạy độc lập trên luồng nền,
/// lỗi ở mục nào chỉ ảnh hưởng mục đó.
/// </summary>
public static partial class ReportCollector
{
    public sealed record SectionDef(string Key, string Title, string Glyph, Func<Task<SectionData>> Collect);

    public static IReadOnlyList<SectionDef> Sections { get; } =
    [
        new("machine", "Tổng quan máy", "\uE7F8", () => Task.Run(Machine)),
        new("os", "Hệ điều hành", "\uE770", () => Task.Run(Os)),
        new("cpu", "Bộ xử lý (CPU)", "\uE950", () => Task.Run(Cpu)),
        new("ram", "Bộ nhớ RAM", "\uEEA0", () => Task.Run(Memory)),
        new("gpu", "Đồ hoạ & màn hình", "\uE7F4", () => Task.Run(Graphics)),
        new("disk", "Ổ cứng", "\uEDA2", StorageAsync),
        new("net", "Mạng", "\uE839", NetworkAsync),
        new("battery", "Pin", "\uE83F", () => Task.Run(Battery)),
        new("security", "Bảo mật", "\uE72E", () => Task.Run(Security)),
        new("apps", "Phần mềm đã cài", "\uE71D", () => Task.Run(InstalledApps)),
        new("updates", "Cập nhật Windows", "\uE895", () => Task.Run(Updates)),
        new("printers", "Máy in", "\uE749", () => Task.Run(Printers)),
    ];

    /// <summary>Thời gian tối đa cho một mục; quá thì báo lỗi mục đó.</summary>
    public static readonly TimeSpan SectionTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Gom các giá trị Warn/Bad thành danh sách "Điểm cần chú ý".</summary>
    public static List<ReportIssue> FindIssues(IEnumerable<ReportSection> sections)
    {
        var list = new List<ReportIssue>();
        foreach (var s in sections)
        {
            foreach (var f in s.Fields)
                if (f.Tone is Tone.Warn or Tone.Bad)
                    list.Add(new ReportIssue(s.Title, $"{f.Key}: {f.Value}", f.Tone));

            foreach (var t in s.Tables)
                foreach (var row in t.Rows)
                    for (var i = 0; i < row.Length; i++)
                        if (row[i].Tone is Tone.Warn or Tone.Bad)
                            list.Add(new ReportIssue(s.Title,
                                $"{row[0].Text} — {t.Columns[i]}: {row[i].Text}", row[i].Tone));
        }
        return list.OrderBy(i => i.Tone == Tone.Bad ? 0 : 1).ToList();
    }
}

public static partial class ReportCollector
{
    /// <summary>Tạo danh sách mục rỗng theo thứ tự hiển thị.</summary>
    public static List<ReportSection> CreateSections() =>
        Sections.Select(s => new ReportSection(s.Key, s.Title, s.Glyph)).ToList();

    /// <summary>
    /// Quét song song mọi mục. Gọi từ luồng UI: kết quả được gán lại trên luồng gọi
    /// (await quay về đúng context), còn việc đọc dữ liệu chạy nền.
    /// </summary>
    public static async Task RunAsync(IReadOnlyList<ReportSection> sections, Action? onSectionDone = null)
    {
        var defs = Sections.ToDictionary(s => s.Key);
        foreach (var s in sections) { s.Reset(); s.State = SectionState.Loading; }

        await Task.WhenAll(sections.Select(async s =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var data = await defs[s.Key].Collect().WaitAsync(SectionTimeout);
                s.Apply(data);
            }
            catch (TimeoutException)
            {
                s.Fail($"Quá {SectionTimeout.TotalSeconds:0} giây chưa đọc xong, đã bỏ qua mục này.");
            }
            catch (Exception ex)
            {
                Log.Error($"Báo cáo: mục {s.Key} lỗi", ex);
                s.Fail($"Không đọc được: {ex.Message}");
            }
            Log.Info($"Báo cáo: {s.Key} {s.State} sau {sw.ElapsedMilliseconds} ms");
            onSectionDone?.Invoke();
        }));
    }
}
