using System.Text.Json;
using ITHelpdeskTool.Services.SysConfig;
using static ITHelpdeskTool.Services.ProcessRunner;

namespace ITHelpdeskTool.Services.Uninstall;

public sealed record AppxApp(string Name, string FullName, string Version, string Publisher, string FriendlyName, bool IsBloat)
{
    public string Display => FriendlyName.Length > 0 ? FriendlyName : Name;
}

/// <summary>Ứng dụng Microsoft Store / cài sẵn theo Windows (AppX).</summary>
public static class AppxService
{
    /// <summary>Ứng dụng cài sẵn hay bị coi là rác, kèm tên dễ hiểu.</summary>
    public static readonly Dictionary<string, string> Bloat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.BingNews"] = "Tin tức (News)",
        ["Microsoft.BingWeather"] = "Thời tiết (Weather)",
        ["Microsoft.BingSearch"] = "Bing Search",
        ["Microsoft.GetHelp"] = "Get Help",
        ["Microsoft.Getstarted"] = "Mẹo (Tips)",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Solitaire Collection",
        ["Microsoft.People"] = "People",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.XboxApp"] = "Xbox Console Companion",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.Xbox.TCUI"] = "Xbox TCUI",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.XboxSpeechToTextOverlay"] = "Xbox Speech to Text",
        ["Microsoft.ZuneMusic"] = "Media Player / Groove",
        ["Microsoft.ZuneVideo"] = "Phim & TV",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["MicrosoftTeams"] = "Teams (cá nhân)",
        ["MSTeams"] = "Teams",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office Hub)",
        ["Microsoft.WindowsMaps"] = "Bản đồ (Maps)",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.OutlookForWindows"] = "Outlook (new)",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.MixedReality.Portal"] = "Mixed Reality Portal",
        ["Microsoft.Microsoft3DViewer"] = "3D Viewer",
        ["Microsoft.SkypeApp"] = "Skype",
        ["Microsoft.WindowsCommunicationsApps"] = "Mail và Calendar",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Disney.37853FC22B2CE"] = "Disney+",
        ["king.com.CandyCrushSaga"] = "Candy Crush Saga",
        ["king.com.CandyCrushSodaSaga"] = "Candy Crush Soda",
        ["BytedancePte.Ltd.TikTok"] = "TikTok",
        ["Facebook.Facebook"] = "Facebook",
        ["Facebook.Instagram"] = "Instagram",
        ["AmazonVideo.PrimeVideo"] = "Prime Video",
        ["5A894077.McAfeeSecurity"] = "McAfee",
    };

    public static async Task<List<AppxApp>> ReadAsync()
    {
        const string script = """
            Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } |
            Select-Object Name, PackageFullName, Version, PublisherId, @{n='Pub';e={ ($_.Publisher -split ',')[0] -replace '^CN=' }} |
            ConvertTo-Json -Compress
            """;
        var r = await PowerShellAsync(script);
        if (!r.Success) throw new InvalidOperationException(OpResult.FirstLine(r.Text));
        var list = new List<AppxApp>();
        var json = r.Output.Trim();
        if (json.Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var name = S("Name");
            var bloat = Bloat.TryGetValue(name, out var friendly);
            list.Add(new AppxApp(name, S("PackageFullName"), S("Version"), S("Pub"), friendly ?? "", bloat));
        }
        return list.OrderByDescending(a => a.IsBloat).ThenBy(a => a.Display).ToList();
    }

    /// <summary>Gỡ ứng dụng Store. allUsers = gỡ cho mọi tài khoản và không cho Windows tự cài lại cho tài khoản mới.</summary>
    public static async Task<OpResult> RemoveAsync(AppxApp a, bool allUsers)
    {
        var script = allUsers
            ? $"Get-AppxPackage -AllUsers -Name {PsQuote(a.Name)} | Remove-AppxPackage -AllUsers -ErrorAction Stop; " +
              $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {PsQuote(a.Name)} | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null"
            : $"Remove-AppxPackage -Package {PsQuote(a.FullName)} -ErrorAction Stop";
        var r = await PowerShellAsync(script);
        return OpResult.From(r, $"Đã gỡ {a.Display}" + (allUsers ? " cho mọi tài khoản." : "."), $"Không gỡ được {a.Display}");
    }
}
