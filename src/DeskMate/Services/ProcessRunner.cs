using System.Diagnostics;
using System.Text;

namespace DeskMate.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Chạy lệnh ngoài (PowerShell, DISM, winget, reg...) ẩn cửa sổ, đọc output dạng UTF-8.
/// Dùng chung cho các module giai đoạn sau.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName, string arguments,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
            throw;
        }

        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Chạy một đoạn PowerShell (mã hoá Base64 để khỏi lo escape).</summary>
    public static Task<ProcessResult> PowerShellAsync(string script, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", onOutput, ct);
    }
}
