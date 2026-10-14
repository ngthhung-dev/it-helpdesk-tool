using System.IO;
using System.Xml.Linq;
using static ITHelpdeskTool.Services.SelfTestKit;

namespace ITHelpdeskTool.Services.Office;

/// <summary>Dựng cấu hình ODT, tải ODT, cài Office thật (chỉ Word), đọc bản quyền, gỡ sạch. --selftest office</summary>
public static class OfficeSelfTest
{
    public static async Task<int> RunToFileAsync(string path)
    {
        var t = new SelfTestKit();

        await t.Do("xml.build", async () =>
        {
            var c = new OfficeConfig
            {
                Product = OfficeCatalog.Products.First(p => p.Id == "ProPlus2024Volume"),
                Languages = ["vi-vn", "en-us"], ExcludeApps = ["Access", "Teams"], Visio = true, ProductKey = "abcde-fghij-klmno-pqrst-uvwxy",
            };
            var x = XDocument.Parse(OfficeCatalog.BuildXml(c));
            var add = x.Root!.Element("Add")!;
            Check(add.Attribute("Channel")!.Value == "PerpetualVL2024" && add.Attribute("OfficeClientEdition")!.Value == "64", "Add sai");
            var prods = add.Elements("Product").ToList();
            Check(prods.Count == 2 && prods[1].Attribute("ID")!.Value == "VisioPro2024Volume", "thiếu Visio");
            Check(prods[0].Attribute("PIDKEY")!.Value == "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", "PIDKEY");
            Check(prods[0].Elements("Language").Count() == 2 && prods[0].Elements("ExcludeApp").Count() == 2, "ngôn ngữ / loại trừ");
            Check(x.Root.Elements("Property").Any(p => p.Attribute("Name")!.Value == "AUTOACTIVATE"), "bản Volume phải tự kích hoạt");
            Check(x.Root.Element("RemoveMSI") is not null, "RemoveMSI");
            var dl = XDocument.Parse(OfficeCatalog.BuildDownloadXml(c, @"D:\OfficeSetup"));
            Check(dl.Root!.Elements().Count() == 1 && dl.Root.Element("Add")!.Attribute("SourcePath")!.Value == @"D:\OfficeSetup", "XML tải về");
            var rm = XDocument.Parse(OfficeService.RemoveAllXml(true));
            Check(rm.Root!.Element("Remove")!.Attribute("All")!.Value == "TRUE", "XML gỡ");
            await Task.CompletedTask;
            return "cài / tải offline / gỡ ok";
        });

        await t.Do("dstatus.parse", async () =>
        {
            const string sample = """
                ---Processing--------------------------
                ---------------------------------------
                PRODUCT ID: 00410-12345-00000-AA123
                SKU ID: 12345678-1234-1234-1234-123456789012
                LICENSE NAME: Office 21, Office21ProPlus2021VL_MAK_AE edition
                LICENSE DESCRIPTION: Office 21, RETAIL(MAK) channel
                LICENSE STATUS:  ---LICENSED---
                Last 5 characters of installed product key: ABCDE
                ---------------------------------------
                LICENSE NAME: Office 21, Office21VisioPro2021VL_KMS_Client_AE edition
                LICENSE DESCRIPTION: Office 21, VOLUME_KMSCLIENT channel
                LICENSE STATUS:  ---OOB_GRACE---
                ERROR CODE: 0x4004F00C
                REMAINING GRACE: 29 days  (41760 minute(s) before expiring)
                Last 5 characters of installed product key: 2B7RT
                ---------------------------------------
                ---Exiting-----------------------------
                """;
            var l = OfficeService.ParseDstatus(sample);
            Check(l.Count == 2, $"{l.Count} giấy phép");
            Check(l[0].Licensed && l[0].Last5 == "ABCDE", l[0].ToString());
            Check(!l[1].Licensed && l[1].StatusText.StartsWith("Đang dùng thử") && l[1].Last5 == "2B7RT", l[1].StatusText);
            Check(OfficeService.IsKeyFormat("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY") && !OfficeService.IsKeyFormat("ABCDE-FGHIJ"), "định dạng key");
            await Task.CompletedTask;
            return string.Join(" | ", l.Select(x => $"{x.Name}: {x.StatusText}"));
        });

        await t.Do("detect.before", async () =>
        {
            var c = OfficeService.ReadC2R();
            var msi = OfficeService.ReadMsiOffice();
            await Task.CompletedTask;
            return c is null ? $"chưa có Office C2R · MSI: {msi.Count}" : $"đã có {c.ProductText} {c.Version} ({c.Channel})";
        });

        await t.Do("odt.download", async () =>
        {
            var p = await OfficeService.EnsureOdtAsync();
            return $"{p} · {Fmt.Size(new FileInfo(p).Length)} · chữ ký Microsoft hợp lệ";
        });

        await t.Do("install.word", async () =>
        {
            var c = new OfficeConfig
            {
                Product = OfficeCatalog.Products.First(p => p.Id == "O365ProPlusRetail"),
                Languages = ["en-us"],
                ExcludeApps = OfficeCatalog.Apps.Where(a => a.Id != "Word").Select(a => a.Id).ToList(),
                ShowUi = false, RemoveMsi = false,
            };
            var r = await OfficeService.RunOdtAsync(OfficeCatalog.BuildXml(c), "configure");
            Check(r.Ok, r.Message);
            var d = OfficeService.ReadC2R();
            Check(d is not null && d.Products.Contains("O365ProPlusRetail"), "không đọc được Office vừa cài");
            Check(d!.ChannelId == "Current" && d.Platform == "x64", $"kênh / nền tảng: {d.Channel} {d.Platform}");
            var word = Path.Combine(d.InstallPath, @"root\Office16\WINWORD.EXE");
            Check(File.Exists(word), "không thấy WINWORD.EXE");
            Check(!File.Exists(Path.Combine(d.InstallPath, @"root\Office16\EXCEL.EXE")), "Excel không được cài");
            return $"{d.ProductText} {d.Version} · {d.Channel} · {d.Culture}";
        });

        await t.Do("license.read", async () =>
        {
            var ospp = OfficeService.OsppPath();
            var found = Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office"),
                "OSPP.VBS", SearchOption.AllDirectories).ToList();
            var l = await OfficeService.LicensesAsync();
            var vnext = OfficeService.HasSubscriptionLicense();
            return $"OSPP: {ospp ?? "không có"} (tìm thấy: {string.Join(", ", found)}) · {l.Count} giấy phép: " +
                   string.Join(" | ", l.Select(x => $"{x.Name} {x.StatusText} …{x.Last5}")) + $" · vNext: {vnext}";
        });

        await t.Do("license.badkey", async () =>
        {
            if (OfficeService.OsppPath() is null) return "bỏ qua (không có OSPP.VBS)";
            var r = await OfficeService.ActivateWithKeyAsync("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE");
            Check(!r.Ok, "key giả phải bị từ chối");
            return r.Message;
        });

        await t.Do("remove.all", async () =>
        {
            var r = await OfficeService.RunOdtAsync(OfficeService.RemoveAllXml(false, showUi: false), "configure");
            Check(r.Ok, r.Message);
            Check(OfficeService.ReadC2R() is null, "Office vẫn còn");
            return r.Message;
        });

        return await t.ToFileAsync(path);
    }
}
