using System.Reflection;

namespace ITHelpdeskTool.Services;

/// <summary>Thông tin sản phẩm, đọc từ metadata trong .csproj để chỉ khai báo một nơi.</summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Name { get; } =
        Asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "IT Helpdesk Tool";

    public static string Version { get; } =
        Asm.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string Author { get; } =
        Asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "ngthhung";

    public static string Copyright { get; } =
        Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    public static string Email { get; } = Meta("ContactEmail");
    public static string Phone { get; } = Meta("ContactPhone");

    private static string Meta(string key) =>
        Asm.GetCustomAttributes<AssemblyMetadataAttribute>()
           .FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
