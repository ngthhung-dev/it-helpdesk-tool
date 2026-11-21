using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Kết quả chạy một lệnh ngoài.</summary>
    public sealed class RunResult
    {
        public RunResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        public int ExitCode { get; }
        public string Output { get; }
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Chạy lệnh ngoài (dism, bcdedit, powershell…). Tách giao diện để test giả lập được.</summary>
    public interface IRunner
    {
        RunResult Run(string file, string args);
    }

    public sealed class ProcessRunner : IRunner
    {
        private readonly Action<string>? _log;

        public ProcessRunner(Action<string>? log = null) => _log = log;

        public RunResult Run(string file, string args)
        {
            _log?.Invoke($"> {file} {args}");
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Oem,
                StandardErrorEncoding = Oem,
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                var text = sb.ToString();
                if (p.ExitCode != 0) _log?.Invoke($"  (mã thoát {p.ExitCode}) {FirstLines(text, 6)}");
                return new RunResult(p.ExitCode, text);
            }
        }

        /// <summary>Công cụ console cũ (dism, bcdedit) in theo code page OEM của máy.</summary>
        private static Encoding Oem
        {
            get
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { return Encoding.UTF8; }
            }
        }

        internal static string FirstLines(string s, int n)
        {
            var lines = new List<string>();
            foreach (var l in s.Split('\n'))
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                lines.Add(t);
                if (lines.Count >= n) break;
            }
            return string.Join(" | ", lines);
        }
    }

    /// <summary>Lỗi có thông điệp tiếng Việt để hiện cho người dùng.</summary>
    public sealed class ReinstallException : Exception
    {
        public ReinstallException(string message) : base(message) { }
    }
}
