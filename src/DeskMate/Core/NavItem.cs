using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskMate.Core;

public sealed partial class NavItem(string key, string title, string glyph, int phase, Func<PageViewModel> factory)
    : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>Mã ký tự Segoe Fluent Icons.</summary>
    public string Glyph { get; } = glyph;
    /// <summary>Giai đoạn phát triển (1 = đã làm).</summary>
    public int Phase { get; } = phase;
    public bool IsReady => Phase == 1;
    public Func<PageViewModel> Factory { get; } = factory;

    [ObservableProperty] private bool _isSelected;
}

public sealed class NavGroup(string title, IReadOnlyList<NavItem> items)
{
    public string Title { get; } = title;
    public IReadOnlyList<NavItem> Items { get; } = items;
}
