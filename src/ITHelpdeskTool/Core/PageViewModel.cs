using CommunityToolkit.Mvvm.ComponentModel;

namespace ITHelpdeskTool.Core;

/// <summary>Lớp cơ sở cho mọi trang. Shell gọi OnNavigatedTo/From khi chuyển trang.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual string Subtitle => string.Empty;

    public virtual void OnNavigatedTo() { }
    public virtual void OnNavigatedFrom() { }
}
