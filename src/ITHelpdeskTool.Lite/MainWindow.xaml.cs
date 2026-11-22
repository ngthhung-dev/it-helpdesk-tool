using System.Windows;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.ViewModels;

namespace ITHelpdeskTool;

public partial class MainWindow : Window
{
    private readonly ReinstallViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        Page.DataContext = _vm;
        Footer.Text = $"{AppInfo.Name} Lite {AppInfo.Version} · bản cho Windows 7 / 8 / 8.1 — chỉ có tính năng Cài lại sạch · {Environment.OSVersion.VersionString}";
        Loaded += (_, _) => _vm.OnNavigatedTo();
        Closed += (_, _) => _vm.OnNavigatedFrom();
    }
}
