using System.Windows;
using ITHelpdeskTool.Services;

namespace ITHelpdeskTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
