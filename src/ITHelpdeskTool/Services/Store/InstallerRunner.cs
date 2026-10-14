using System.Diagnostics;
using System.IO;
using System.Text;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.Services.Store;

/// <summary>Kiểu bộ cài và tham số cài im lặng tương ứng.</summary>
public sealed record InstallerType(string Kind, string Label, string SilentArgs)
{
    public bool CanSilent => SilentArgs.Length > 0 || Kind == "msi";
}

/// <summary>Chạy file cài đặt (.exe / .msi), tự đoán tham số cài im lặng.</summary>
public static class InstallerRunner
{
    public static readonly string[] Extensions = [".exe", ".msi"];

    public static InstallerType Detect(string file)
    {
        if (Path.GetExtension(file).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            return new("msi", "Windows Installer (MSI)", "/qn /norestart");
        try
        {
            using var fs = File.OpenRead(file);
            var len = (int)Math.Min(fs.Length, 6 * 1024 * 1024);
            var buf = new byte[len];
            var n = fs.Read(buf, 0, len);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            // Vài bộ cài để chuỗi nhận dạng ở cuối file
            if (fs.Length > len)
            {
                fs.Seek(-Math.Min(fs.Length - len, 2 * 1024 * 1024), SeekOrigin.End);
                var tail = new byte[2 * 1024 * 1024];
                var m = fs.Read(tail, 0, tail.Length);
                text += Encoding.ASCII.GetString(tail, 0, m);
            }
            if (text.Contains("Inno Setup", StringComparison.Ordinal)) return new("inno", "Inno Setup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-");
            if (text.Contains("Nullsoft", StringComparison.Ordinal)) return new("nsis", "NSIS", "/S");
            if (text.Contains(".wixburn", StringComparison.Ordinal)) return new("burn", "WiX Burn", "/quiet /norestart");
            if (text.Contains("InstallShield", StringComparison.Ordinal)) return new("installshield", "InstallShield", "/s /v\"/qn /norestart\"");
            if (text.Contains("Advanced Installer", StringComparison.Ordinal)) return new("advinst", "Advanced Installer", "/exenoui /qn /norestart");
            if (text.Contains("Squirrel", StringComparison.Ordinal)) return new("squirrel", "Squirrel", "--silent");
        }
        catch { }
        return new("", "Không rõ (cài có giao diện)", "");
    }

    /// <summary>Chạy bộ cài và chờ xong. silent = dùng tham số im lặng nếu nhận ra kiểu bộ cài.</summary>
    public static async Task<OpResult> RunAsync(string file, string name, bool silent = true, CancellationToken ct = default)
    {
        var t = Detect(file);
        string exe, args;
        var quiet = silent && t.CanSilent;
        if (t.Kind == "msi")
        {
            exe = "msiexec.exe";
            args = $"/i \"{file}\"" + (quiet ? " /qn /norestart" : "");
        }
        else
        {
            exe = file;
            args = quiet ? t.SilentArgs : "";
        }
        Log.Info($"Cài {name}: {exe} {args}");
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = !quiet,
                CreateNoWindow = quiet,
                WorkingDirectory = Path.GetDirectoryName(file)!,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("không khởi chạy được");
            await p.WaitForExitAsync(ct);
            var code = p.ExitCode;
            var restart = code is 3010 or 1641;
            return code is 0 or 3010 or 1641
                ? OpResult.Success($"Đã cài {name}" + (quiet ? "" : " (cài có giao diện)") + (restart ? " — cần khởi động lại máy" : ""), restart)
                : code == 1602 ? OpResult.Fail($"Đã huỷ cài {name}.")
                : OpResult.Fail($"Cài {name} lỗi (mã {code}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OpResult.Fail($"Đã huỷ cài {name}.");
        }
        catch (Exception ex) { return OpResult.Fail($"Không chạy được bộ cài {name}: {ex.Message}"); }
    }
}
