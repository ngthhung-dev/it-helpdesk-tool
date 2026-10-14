using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace DeskMate.Services;

public static class AdminHelper
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>Mở công cụ có sẵn của Windows (taskmgr, devmgmt.msc...).</summary>
public static class ShellLauncher
{
    public static void Open(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args ?? string.Empty) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Không mở được {target}:\n{ex.Message}", "DeskMate");
        }
    }
}
