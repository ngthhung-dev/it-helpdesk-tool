using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ITHelpdeskTool.Core;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Đổi giao diện Sáng / Tối / Theo hệ thống. Mọi màu trong app lấy bằng DynamicResource từ các khoá *Brush,
/// nên chỉ cần thay brush trong Application.Resources là cả app đổi màu ngay, không cần mở lại.
/// </summary>
public static class ThemeService
{
    private const string Key = @"HKEY_CURRENT_USER\Software\ngthhung\IT Helpdesk Tool";
    private static bool _hooked;

    public static AppTheme Mode { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; } = true;

    /// <summary>Báo khi đổi màu — cho các control tự vẽ (bảng, vòng tròn) vẽ lại.</summary>
    public static event Action? Changed;

    private static readonly Dictionary<string, (uint Dark, uint Light)> Palette = new()
    {
        ["Bg"] = (0xFF0E1014, 0xFFF3F4F7),
        ["Sidebar"] = (0xFF121419, 0xFFE9ECF1),
        ["Surface"] = (0xFF171A21, 0xFFFFFFFF),
        ["Surface2"] = (0xFF1E222B, 0xFFF0F2F6),
        ["Border"] = (0xFF262B36, 0xFFDCE0E8),
        ["Text"] = (0xFFE8EAF0, 0xFF1A1D24),
        ["Muted"] = (0xFF8A92A6, 0xFF586174),
        ["Faint"] = (0xFF5A6175, 0xFF8B93A5),
        ["Accent"] = (0xFF5B8CFF, 0xFF2F6BEB),
        ["AccentSoft"] = (0xFF1F2A44, 0xFFDDE7FF),
        ["Good"] = (0xFF34D399, 0xFF0F9F6E),
        ["Warn"] = (0xFFFBBF24, 0xFFB76E00),
        ["Bad"] = (0xFFF87171, 0xFFD93636),
        ["Violet"] = (0xFFA78BFA, 0xFF7C4DEB),
        ["Hover"] = (0x14FFFFFF, 0x0F000000),
        ["Hover2"] = (0x33FFFFFF, 0x1F000000),
    };

    /// <summary>Gọi một lần khi khởi động (trước khi mở cửa sổ). override: "light"/"dark" từ dòng lệnh.</summary>
    public static void Init(string? overrideMode = null)
    {
        Mode = overrideMode?.ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            "system" => AppTheme.System,
            _ => ReadSaved(),
        };
        Apply();
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (Mode == AppTheme.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(Apply);
            };
        }
    }

    public static void Set(AppTheme mode)
    {
        Mode = mode;
        try { Registry.SetValue(Key, "Theme", mode.ToString()); } catch { }
        Apply();
    }

    private static AppTheme ReadSaved()
    {
        try { return Enum.TryParse<AppTheme>(Registry.GetValue(Key, "Theme", null) as string, out var m) ? m : AppTheme.System; }
        catch { return AppTheme.System; }
    }

    public static bool SystemIsDark()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { return false; }
    }

    private static void Apply()
    {
        var res = Application.Current?.Resources;
        if (res is null) return;
        var dark = Mode switch { AppTheme.Dark => true, AppTheme.Light => false, _ => SystemIsDark() };
        var first = !_applied;
        _applied = true;
        if (!first && dark == IsDark) return;
        IsDark = dark;
        foreach (var (name, (d, l)) in Palette)
        {
            var c = ToColor(dark ? d : l);
            res[name + "Color"] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            res[name + "Brush"] = b;
        }
        Changed?.Invoke();
    }

    private static bool _applied;

    private static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
