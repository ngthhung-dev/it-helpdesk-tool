using ITHelpdeskTool.Core;
using System.Windows;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeButton();
        StateChanged += (_, _) =>
        {
            AppState.IsMinimized = WindowState == WindowState.Minimized;
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Thu về" : "Phóng to";
        };
        ContentRendered += (_, _) =>
        {
            Log.Mark("Cửa sổ đã hiển thị");
            UiWatchdog.Start(Dispatcher);   // bắt đầu canh treo sau khi đã mở xong
        };
    }

    private void UpdateThemeButton()
    {
        var m = ThemeService.Mode;
        ThemeButton.Content = m switch { AppTheme.Light => "\uE706", AppTheme.Dark => "\uE708", _ => "\uE7F4" };
        ThemeButton.ToolTip = "Giao diện: " + m switch { AppTheme.Light => "sáng", AppTheme.Dark => "tối", _ => "theo hệ thống" };
        ThemeSystem.IsChecked = m == AppTheme.System;
        ThemeLight.IsChecked = m == AppTheme.Light;
        ThemeDark.IsChecked = m == AppTheme.Dark;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        UpdateThemeButton();
        ThemeMenu.PlacementTarget = ThemeButton;
        ThemeMenu.IsOpen = true;
    }

    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string t } && Enum.TryParse<AppTheme>(t, out var m)) ThemeService.Set(m);
        UpdateThemeButton();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
