using System.Windows;
using System.Windows.Controls;

namespace ITHelpdeskTool.Views;

public partial class FontsPage : UserControl
{
    public FontsPage() => InitializeComponent();

    /// <summary>Khung xem trước co theo cửa sổ: ~38% bề ngang, 260–400 px — danh sách font không bị bóp mất.</summary>
    private void InstalledGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailPanel.Width = Math.Clamp(e.NewSize.Width * 0.38, 260, 400);
}
