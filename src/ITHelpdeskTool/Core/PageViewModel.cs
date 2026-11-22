using CommunityToolkit.Mvvm.ComponentModel;

namespace ITHelpdeskTool.Core;

/// <summary>Lớp cơ sở cho mọi trang. Shell gọi OnNavigatedTo/From khi chuyển trang.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual string Subtitle => string.Empty;

    /// <summary>Trang đang nằm trong một trang khác (vd tab của "Cài lại Windows") → ẩn tiêu đề lớn của chính nó.</summary>
    public bool Embedded { get; set; }

    public virtual void OnNavigatedTo() { }
    public virtual void OnNavigatedFrom() { }
}
