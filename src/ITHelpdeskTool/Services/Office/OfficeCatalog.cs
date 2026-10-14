using System.Xml.Linq;

namespace ITHelpdeskTool.Services.Office;

public sealed record OfficeChannel(string Id, string Name, string Guid);

/// <summary>Bản Office có thể cài qua ODT. Channel: kênh cập nhật bắt buộc của bản đó.</summary>
public sealed record OfficeProduct(string Id, string Name, string Channel, bool Volume, string Note, string VisioId = "", string ProjectId = "")
{
    public override string ToString() => Name;
}

public sealed record OfficeApp(string Id, string Name, bool DefaultOn);

public sealed record OfficeLanguage(string Id, string Name);

/// <summary>Cấu hình cài Office do người dùng chọn → XML cho ODT.</summary>
public sealed class OfficeConfig
{
    public OfficeProduct Product { get; set; } = OfficeCatalog.Products[0];
    public string Channel { get; set; } = "";
    public bool X64 { get; set; } = true;
    public List<string> Languages { get; set; } = ["vi-vn"];
    public List<string> ExcludeApps { get; set; } = [];
    public bool Visio { get; set; }
    public bool Project { get; set; }
    public string ProductKey { get; set; } = "";
    public bool RemoveMsi { get; set; } = true;
    public bool ShowUi { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public bool SharedComputer { get; set; }
    /// <summary>Thư mục bộ cài offline (đã tải bằng /download). Rỗng = tải từ mạng.</summary>
    public string SourcePath { get; set; } = "";
}

public static class OfficeCatalog
{
    public static readonly IReadOnlyList<OfficeChannel> Channels =
    [
        new("Current", "Current (hằng tháng)", "492350f6-3a01-4f97-b9c0-c7c6ddf67d60"),
        new("MonthlyEnterprise", "Monthly Enterprise", "55336b82-a18d-4dd6-b5f6-9e5095c314a6"),
        new("SemiAnnual", "Semi-Annual Enterprise (6 tháng)", "7ffbc6bf-bc32-4f92-8982-f9dd17fd3114"),
        new("SemiAnnualPreview", "Semi-Annual Preview", "b8f9b850-328d-4355-9145-c59439a0c4cf"),
        new("CurrentPreview", "Current Preview", "64256afe-f5d9-4f86-8936-8840a6a4f5be"),
        new("BetaChannel", "Beta", "5440fd1f-7ecb-4221-8110-145efaa6372f"),
        new("PerpetualVL2024", "Office LTSC 2024", "7983bac0-e531-40cf-be00-fd24fe66619c"),
        new("PerpetualVL2021", "Office LTSC 2021", "5030841d-c919-4594-8d2d-84ae4f96e58e"),
        new("PerpetualVL2019", "Office 2019 Volume", "f2e724c1-748f-4b47-8fb8-8e0d210e9208"),
    ];

    /// <summary>Kênh người dùng chuyển được (bản Microsoft 365 / bán lẻ).</summary>
    public static IEnumerable<OfficeChannel> SwitchableChannels => Channels.Where(c => !c.Id.StartsWith("Perpetual"));

    public static readonly IReadOnlyList<OfficeProduct> Products =
    [
        new("O365ProPlusRetail", "Microsoft 365 Apps for enterprise", "Current", false,
            "Gói thuê bao doanh nghiệp / giáo dục — đăng nhập tài khoản để kích hoạt.", "VisioProRetail", "ProjectProRetail"),
        new("O365BusinessRetail", "Microsoft 365 Apps for business", "Current", false,
            "Gói Business Standard / Premium — đăng nhập tài khoản để kích hoạt.", "VisioProRetail", "ProjectProRetail"),
        new("O365HomePremRetail", "Microsoft 365 Personal / Family", "Current", false,
            "Gói cá nhân / gia đình — đăng nhập tài khoản Microsoft để kích hoạt."),
        new("HomeBusiness2024Retail", "Office Home & Business 2024", "Current", false,
            "Mua đứt (bán lẻ) — đăng nhập tài khoản đã gắn key hoặc nhập key.", "VisioPro2024Retail", "ProjectPro2024Retail"),
        new("Home2024Retail", "Office Home 2024", "Current", false, "Mua đứt (bán lẻ) — Word, Excel, PowerPoint, OneNote."),
        new("ProPlus2024Volume", "Office LTSC Professional Plus 2024", "PerpetualVL2024", true,
            "Bản cấp phép số lượng lớn cho tổ chức — cần key MAK / máy chủ KMS của tổ chức.", "VisioPro2024Volume", "ProjectPro2024Volume"),
        new("Standard2024Volume", "Office LTSC Standard 2024", "PerpetualVL2024", true,
            "Bản Volume không có Access — cần key MAK / KMS của tổ chức.", "VisioStd2024Volume", "ProjectStd2024Volume"),
        new("ProPlus2021Volume", "Office LTSC Professional Plus 2021", "PerpetualVL2021", true,
            "Bản Volume 2021 — cần key MAK / KMS của tổ chức.", "VisioPro2021Volume", "ProjectPro2021Volume"),
        new("HomeBusiness2021Retail", "Office Home & Business 2021", "Current", false,
            "Mua đứt (bán lẻ) 2021 — đăng nhập tài khoản đã gắn key hoặc nhập key.", "VisioPro2021Retail", "ProjectPro2021Retail"),
        new("ProPlus2019Volume", "Office Professional Plus 2019", "PerpetualVL2019", true,
            "Bản Volume 2019 — cần key MAK / KMS của tổ chức.", "VisioPro2019Volume", "ProjectPro2019Volume"),
    ];

    public static readonly IReadOnlyList<OfficeApp> Apps =
    [
        new("Word", "Word", true),
        new("Excel", "Excel", true),
        new("PowerPoint", "PowerPoint", true),
        new("Outlook", "Outlook (classic)", true),
        new("OneNote", "OneNote", true),
        new("Access", "Access", false),
        new("Publisher", "Publisher", false),
        new("Teams", "Teams", false),
        new("OneDrive", "OneDrive", true),
        new("Lync", "Skype for Business", false),
        new("Groove", "OneDrive for Business (cũ)", false),
    ];

    public static readonly IReadOnlyList<OfficeLanguage> Languages =
    [
        new("vi-vn", "Tiếng Việt"),
        new("en-us", "English (United States)"),
        new("MatchOS", "Theo ngôn ngữ Windows"),
        new("ja-jp", "日本語"),
        new("ko-kr", "한국어"),
        new("zh-cn", "中文 (简体)"),
        new("fr-fr", "Français"),
    ];

    public static string ProductName(string id) =>
        Products.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id switch
        {
            var x when x.StartsWith("Visio", StringComparison.OrdinalIgnoreCase) => $"Visio ({x})",
            var x when x.StartsWith("Project", StringComparison.OrdinalIgnoreCase) => $"Project ({x})",
            "ProPlusRetail" => "Office Professional Plus (bán lẻ)",
            "ProPlus2021Retail" => "Office Professional Plus 2021",
            "ProPlus2019Retail" => "Office Professional Plus 2019",
            "HomeBusinessRetail" => "Office Home & Business 2016",
            "HomeStudentRetail" => "Office Home & Student 2016",
            "HomeStudent2021Retail" => "Office Home & Student 2021",
            "HomeBusiness2019Retail" => "Office Home & Business 2019",
            "LanguagePack" => "Gói ngôn ngữ",
            "ProofingTools" => "Công cụ soát chính tả",
            _ => id,
        };

    /// <summary>Dựng file cấu hình ODT. Bản Volume tự kích hoạt (AUTOACTIVATE) khi có key MAK hoặc KMS của tổ chức.</summary>
    public static string BuildXml(OfficeConfig c)
    {
        var langs = c.Languages.Count > 0 ? c.Languages : ["MatchOS"];
        XElement ProductEl(string id, bool main)
        {
            var el = new XElement("Product", new XAttribute("ID", id));
            if (main && c.ProductKey.Trim().Length > 0) el.Add(new XAttribute("PIDKEY", c.ProductKey.Trim().ToUpperInvariant()));
            foreach (var l in langs) el.Add(new XElement("Language", new XAttribute("ID", l)));
            foreach (var x in c.ExcludeApps) el.Add(new XElement("ExcludeApp", new XAttribute("ID", x)));
            return el;
        }

        var channel = c.Channel.Length > 0 ? c.Channel : c.Product.Channel;
        var add = new XElement("Add",
            new XAttribute("OfficeClientEdition", c.X64 ? "64" : "32"),
            new XAttribute("Channel", channel),
            ProductEl(c.Product.Id, true));
        if (c.SourcePath.Length > 0) add.Add(new XAttribute("SourcePath", c.SourcePath));
        if (c.Visio && c.Product.VisioId.Length > 0) add.Add(ProductEl(c.Product.VisioId, false));
        if (c.Project && c.Product.ProjectId.Length > 0) add.Add(ProductEl(c.Product.ProjectId, false));

        var root = new XElement("Configuration", add);
        if (c.Product.Volume) root.Add(new XElement("Property", new XAttribute("Name", "AUTOACTIVATE"), new XAttribute("Value", "1")));
        root.Add(new XElement("Property", new XAttribute("Name", "FORCEAPPSHUTDOWN"), new XAttribute("Value", "TRUE")));
        if (c.SharedComputer) root.Add(new XElement("Property", new XAttribute("Name", "SharedComputerLicensing"), new XAttribute("Value", "1")));
        root.Add(new XElement("Updates", new XAttribute("Enabled", c.AutoUpdate ? "TRUE" : "FALSE")));
        if (c.RemoveMsi) root.Add(new XElement("RemoveMSI"));
        root.Add(new XElement("Display", new XAttribute("Level", c.ShowUi ? "Full" : "None"), new XAttribute("AcceptEULA", "TRUE")));
        return new XDocument(root).ToString();
    }

    /// <summary>File cấu hình để tải bộ cài offline về thư mục (setup.exe /download).</summary>
    public static string BuildDownloadXml(OfficeConfig c, string folder)
    {
        var copy = new OfficeConfig
        {
            Product = c.Product, Channel = c.Channel, X64 = c.X64, Languages = c.Languages, Visio = c.Visio, Project = c.Project,
            SourcePath = folder, ShowUi = c.ShowUi,
        };
        var doc = XDocument.Parse(BuildXml(copy));
        // Khi tải về không cần các thuộc tính cài đặt
        doc.Root!.Elements().Where(e => e.Name != "Add").Remove();
        return doc.ToString();
    }
}
