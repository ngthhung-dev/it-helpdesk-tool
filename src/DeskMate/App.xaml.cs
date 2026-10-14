using System.Windows;
using System.Windows.Threading;

namespace DeskMate;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "DeskMate – lỗi không mong muốn",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
