using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Đọc mẫu nhúng trong Resources và thay {{KHOÁ}}.</summary>
    public static class Templates
    {
        public static string Load(string name)
        {
            using (var s = typeof(Templates).Assembly.GetManifestResourceStream("ITH." + name))
            {
                if (s == null) throw new InvalidOperationException("Thiếu tài nguyên " + name);
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static string Fill(string text, IDictionary<string, string> values)
        {
            foreach (var kv in values) text = text.Replace("{{" + kv.Key + "}}", kv.Value);
            if (text.Contains("{{")) throw new InvalidOperationException("Mẫu còn chỗ chưa điền: " + text.Substring(text.IndexOf("{{", StringComparison.Ordinal), 20));
            return text;
        }

        /// <summary>cmd.exe cần CRLF (nhãn / goto lỗi với LF).</summary>
        public static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>
    /// unattend.xml cho Windows mới: bỏ qua các màn hình thiết lập ban đầu, tạo tài khoản cục bộ, tự đăng nhập một lần
    /// để chạy FirstLogon.ps1 (tinh chỉnh + cài phần mềm). Không chứa key / activate.
    /// </summary>
    public static class UnattendBuilder
    {
        private static string X(string s) => SecurityElement.Escape(s) ?? "";

        private static string Comp(string name, string pass, string arch, string body) =>
            $"    <component name=\"{name}\" processorArchitecture=\"{arch}\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" " +
            "xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
            body + "    </component>\r\n";

        public const string FirstLogonCommand =
            "cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%ProgramData%\\ITHReinstall\\FirstLogon.ps1\"";

        public static string Build(ReinstallPlan p)
        {
            var arch = p.Architecture;
            var pwd = $"<Password><Value>{X(p.Password)}</Value><PlainText>true</PlainText></Password>";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\r\n");

            // ---- specialize: tên máy, múi giờ, bỏ yêu cầu mạng của Windows 11 ----
            sb.Append("  <settings pass=\"specialize\">\r\n");
            sb.Append(Comp("Microsoft-Windows-Shell-Setup", "specialize", arch,
                $"      <ComputerName>{X(p.ComputerName)}</ComputerName>\r\n      <TimeZone>{X(p.TimeZone)}</TimeZone>\r\n"));
            sb.Append(Comp("Microsoft-Windows-Deployment", "specialize", arch,
                "      <RunSynchronous>\r\n" +
                "        <RunSynchronousCommand wcm:action=\"add\"><Order>1</Order>" +
                "<Path>reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>\r\n" +
                "      </RunSynchronous>\r\n"));
            sb.Append("  </settings>\r\n");

            // ---- oobeSystem: vùng, bỏ màn hình OOBE, tài khoản, tự đăng nhập 1 lần, chạy FirstLogon.ps1 ----
            sb.Append("  <settings pass=\"oobeSystem\">\r\n");
            sb.Append(Comp("Microsoft-Windows-International-Core", "oobeSystem", arch,
                $"      <InputLocale>{X(p.InputLocale)}</InputLocale>\r\n" +
                (p.UiLanguage.Length > 0 ? $"      <UILanguage>{X(p.UiLanguage)}</UILanguage>\r\n" : "") +
                $"      <SystemLocale>{X(p.SystemLocale)}</SystemLocale>\r\n" +
                $"      <UserLocale>{X(p.UserLocale)}</UserLocale>\r\n"));
            // Tài khoản Microsoft: để OOBE hiện màn hình đăng nhập (và Wi-Fi), không tạo tài khoản cục bộ / tự đăng nhập
            var ms = p.MicrosoftAccount;
            var accounts = ms ? "" :
                "      <UserAccounts><LocalAccounts>\r\n" +
                $"        <LocalAccount wcm:action=\"add\"><Name>{X(p.AccountName)}</Name><DisplayName>{X(p.AccountName)}</DisplayName>" +
                $"<Group>Administrators</Group>{pwd}</LocalAccount>\r\n" +
                "      </LocalAccounts></UserAccounts>\r\n" +
                $"      <AutoLogon><Enabled>true</Enabled><LogonCount>1</LogonCount><Username>{X(p.AccountName)}</Username>{pwd}</AutoLogon>\r\n";
            var hide = ms ? "false" : "true";
            sb.Append(Comp("Microsoft-Windows-Shell-Setup", "oobeSystem", arch,
                "      <OOBE>\r\n" +
                "        <HideEULAPage>true</HideEULAPage>\r\n" +
                "        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>\r\n" +
                $"        <HideOnlineAccountScreens>{hide}</HideOnlineAccountScreens>\r\n" +
                $"        <HideWirelessSetupInOOBE>{hide}</HideWirelessSetupInOOBE>\r\n" +
                $"        <HideLocalAccountScreen>{hide}</HideLocalAccountScreen>\r\n" +
                "        <ProtectYourPC>3</ProtectYourPC>\r\n" +
                "      </OOBE>\r\n" +
                accounts +
                "      <FirstLogonCommands>\r\n" +
                $"        <SynchronousCommand wcm:action=\"add\"><Order>1</Order><CommandLine>{X(FirstLogonCommand)}</CommandLine>" +
                "<Description>IT Helpdesk Tool - cai phan mem va tinh chinh</Description></SynchronousCommand>\r\n" +
                "      </FirstLogonCommands>\r\n" +
                $"      <TimeZone>{X(p.TimeZone)}</TimeZone>\r\n"));
            sb.Append("  </settings>\r\n");
            sb.Append("</unattend>\r\n");
            return sb.ToString();
        }
    }

    /// <summary>postinstall.json cho FirstLogon.ps1 (viết tay vì netstandard2.0 không có System.Text.Json).</summary>
    public static class PostInstallBuilder
    {
        public static string Json(ReinstallPlan p, string id, string stagingRel)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append($"  \"id\": {Q(id)},\r\n");
            sb.Append($"  \"stagingRel\": {Q(stagingRel)},\r\n");
            sb.Append($"  \"machine\": {Arr(p.Tweaks.Where(t => t.Scope == TweakScope.Machine).SelectMany(t => t.Commands))},\r\n");
            sb.Append($"  \"user\": {Arr(p.Tweaks.Where(t => t.Scope == TweakScope.User).SelectMany(t => t.Commands))},\r\n");
            sb.Append("  \"software\": [");
            sb.Append(string.Join(",", p.Software.Select(s =>
                $"\r\n    {{ \"id\": {Q(s.Id)}, \"name\": {Q(s.Name)}, \"kind\": {Q(s.Kind.ToString())}, \"url\": {Q(s.Url)}, \"args\": {Q(s.Args)}, \"exe\": {Q(s.Exe)} }}")));
            sb.Append(p.Software.Count > 0 ? "\r\n  ],\r\n" : "],\r\n");
            var restore = p.Restore.Where(b => b.Restore != RestoreKind.None).ToList();
            sb.Append("  \"restore\": [");
            sb.Append(string.Join(",", restore.Select(b =>
                $"\r\n    {{ \"folder\": {Q(b.Folder)}, \"kind\": {Q(b.Restore.ToString())}, \"to\": {Q(b.RestoreTo)}, \"title\": {Q(b.Title)} }}")));
            sb.Append(restore.Count > 0 ? "\r\n  ],\r\n" : "],\r\n");
            sb.Append($"  \"after\": {Q(p.AfterPostInstall)}\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static string Arr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Q)) + "]";

        public static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
