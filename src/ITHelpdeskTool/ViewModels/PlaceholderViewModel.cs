using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.ViewModels;

/// <summary>Trang tạm cho các chức năng chưa làm.</summary>
public sealed class PlaceholderViewModel(NavItem item) : PageViewModel
{
    public override string Title => item.Title;
    public string Glyph => item.Glyph;
    public string PhaseText => item.Phase == 1
        ? "Giai đoạn 1 · đang làm"
        : $"Giai đoạn {item.Phase} · sắp có";
}
