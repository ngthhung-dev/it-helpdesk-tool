using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace ITHelpdeskTool.Services;

public static class AdminHelper
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
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
            System.Windows.MessageBox.Show($"Không mở được {target}:\n{ex.Message}", AppInfo.Name);
        }
    }
}
