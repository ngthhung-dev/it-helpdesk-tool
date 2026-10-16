using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ITHelpdeskTool.Services;

/// <summary>Máy khởi động kiểu UEFI hay Legacy BIOS.</summary>
public static class Firmware
{
    [DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out int type);

    /// <summary>
    /// Dùng API GetFirmwareType (chính xác nhất); dự phòng bằng giá trị registry PEFirmwareType —
    /// một số bản Windows mới không còn ghi giá trị này nên không dựa riêng vào nó.
    /// </summary>
    public static bool IsUefi
    {
        get
        {
            try { if (GetFirmwareType(out var t) && t is 1 or 2) return t == 2; } catch { }
            using var ctl = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return ctl?.GetValue("PEFirmwareType") is int fw && fw == 2;
        }
    }
}
