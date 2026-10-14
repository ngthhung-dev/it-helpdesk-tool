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
    /// <summary>Mã đặc biệt: mở Windows Terminal, máy không có thì mở PowerShell.</summary>
    public const string Terminal = "@terminal";

    public static void Open(string target, string? args = null)
    {
        // App chạy quyền admin nên cmd/PowerShell mở ra cũng là admin; mở ở thư mục người dùng thay vì System32
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (target == Terminal)
        {
            if (!TryStart("wt.exe", $"-d \"{home}\"", home) && !TryStart("powershell.exe", "-NoLogo", home))
                Fail("Windows Terminal / PowerShell", null);
            return;
        }

        var workDir = target.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ? home : null;
        if (!TryStart(target, args, workDir, out var err))
            Fail(target, err);
    }

    private static bool TryStart(string file, string? args, string? workDir) => TryStart(file, args, workDir, out _);

    private static bool TryStart(string file, string? args, string? workDir, out Exception? error)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args ?? string.Empty) { UseShellExecute = true };
            if (workDir is not null) psi.WorkingDirectory = workDir;
            Process.Start(psi);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Không mở được {file}: {ex.Message}");
            error = ex;
            return false;
        }
    }

    private static void Fail(string what, Exception? ex) =>
        System.Windows.MessageBox.Show($"Không mở được {what}" + (ex is null ? "" : $":\n{ex.Message}"), AppInfo.Name);
}
