using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ITHelpdeskTool.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>Output + lỗi gộp lại, bỏ dòng trống — để hiển thị cho người dùng.</summary>
    public string Text => string.Join(Environment.NewLine,
        (Output + Environment.NewLine + Error).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0));
}

/// <summary>
/// Chạy lệnh ngoài (PowerShell, DISM, winget, netsh...) ẩn cửa sổ.
/// </summary>
public static class ProcessRunner
{
    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Bảng mã của các lệnh console cũ (netsh, ipconfig, w32tm...): chúng in theo code page OEM
    /// của máy chứ không phải UTF-8, đọc sai bảng mã thì tiếng Việt bị lỗi font.
    /// </summary>
    public static Encoding Oem
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName, string arguments,
        Action<string>? onOutput = null,
        CancellationToken ct = default,
        Encoding? encoding = null,
        IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        // Windows PowerShell 5.1 mở từ một tiến trình PowerShell 7 sẽ thừa hưởng PSModulePath của bản 7
        // và nạp nhầm module (lỗi "ConvertTo-SecureString ... module could not be loaded").
        // Xoá đi để 5.1 tự dựng lại đường dẫn module mặc định.
        if (Path.GetFileName(fileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            psi.Environment.Remove("PSModulePath");
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

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
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
            throw;
        }

        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Chạy lệnh console cũ của Windows, đọc output theo bảng mã OEM.</summary>
    public static Task<ProcessResult> ConsoleAsync(string fileName, string arguments, CancellationToken ct = default) =>
        RunAsync(fileName, arguments, ct: ct, encoding: Oem);

    /// <summary>
    /// Chạy một đoạn Windows PowerShell 5.1 (mã hoá Base64 để khỏi lo escape).
    /// Dữ liệu nhạy cảm (mật khẩu) truyền qua <paramref name="env"/> thay vì nhúng vào script.
    /// Lỗi trong script làm lệnh dừng và trả ExitCode = 1.
    /// </summary>
    public static Task<ProcessResult> PowerShellAsync(string script, Action<string>? onOutput = null,
        CancellationToken ct = default, IDictionary<string, string>? env = null)
    {
        var full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" +
                   "$ErrorActionPreference='Stop';try{" + script + "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            onOutput, ct, env: env);
    }

    /// <summary>Đặt chuỗi vào dấu nháy đơn của PowerShell an toàn.</summary>
    public static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
}
