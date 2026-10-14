using System.IO;

namespace ITHelpdeskTool.Services;

/// <summary>Tạo / đọc lối tắt .lnk qua WScript.Shell.</summary>
public static class ShellLink
{
    public static void Create(string lnkPath, string target, string args = "", string? workDir = null, string? description = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Thiếu WScript.Shell");
        dynamic shell = Activator.CreateInstance(t)!;
        var l = shell.CreateShortcut(lnkPath);
        l.TargetPath = target;
        l.Arguments = args;
        l.WorkingDirectory = workDir ?? Path.GetDirectoryName(target);
        if (description is not null) l.Description = description;
        l.IconLocation = target + ",0";
        l.Save();
    }

    public static string? Target(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            return (string)shell.CreateShortcut(lnkPath).TargetPath;
        }
        catch { return null; }
    }
}
